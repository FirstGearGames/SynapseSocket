using System;
using System.Collections.Generic;
using SynapseSocket.Connections;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Core;

/// <summary>
/// Background maintenance for <see cref="SynapseManager"/>: keep-alive sweeps, timeout detection, and reliable
/// retransmission. Driven synchronously from <see cref="SynapseManager.Poll"/> on the host's thread — there are no
/// background loops.
/// </summary>
public sealed partial class SynapseManager
{
    private const string ViolationReliableExhausted = "Connection exceeded the maximum reliable packet retry limit.";
    /// <summary>
    /// Ticks between keep-alive heartbeats, derived from <see cref="SynapseSocket.Core.Configuration.ConnectionConfig.KeepAliveIntervalMilliseconds"/>.
    /// </summary>
    private readonly long _connectionKeepAliveTicks;
    /// <summary>
    /// Ticks of idle time after which a connection is considered timed out, derived from <see cref="SynapseSocket.Core.Configuration.ConnectionConfig.TimeoutMilliseconds"/>.
    /// </summary>
    private readonly long _connectionTimeoutTicks;
    /// <summary>
    /// Ticks between reliable packet retransmission attempts, derived from <see cref="SynapseSocket.Core.Configuration.ReliableConfig.ResendMilliseconds"/>.
    /// </summary>
    private readonly long _reliableResendTicks;
    /// <summary>
    /// Maximum number of retransmission attempts before a reliable packet is considered lost, derived from <see cref="SynapseSocket.Core.Configuration.ReliableConfig.MaximumRetries"/>.
    /// </summary>
    private readonly uint _maximumReliableRetries;
    /// <summary>
    /// Ticks after which an incomplete segment assembly is evicted. Set to <see cref="UnsetSegmentAssemblyTimeoutTicks"/> when the timeout is disabled or segmentation is off.
    /// </summary>
    private readonly long _segmentAssemblyTimeoutTicks;
    /// <summary>
    /// Maximum number of packets a single connection may receive per second before further packets are dropped.
    /// Derived from <see cref="SynapseSocket.Core.Configuration.SecurityConfig.MaximumPacketsPerSecond"/>.
    /// Set to <see cref="SynapseSocket.Core.Configuration.SecurityConfig.DisabledMaximumPacketsPerSecond"/> when the limit is disabled.
    /// </summary>
    private readonly uint _maximumPacketsPerSecond;
    /// <summary>
    /// Maximum number of bytes a single connection may receive per second before further packets are dropped.
    /// Derived from <see cref="SynapseSocket.Core.Configuration.SecurityConfig.MaximumBytesPerSecond"/>.
    /// Set to <see cref="SynapseSocket.Core.Configuration.SecurityConfig.DisabledMaximumBytesPerSecond"/> when the limit is disabled.
    /// </summary>
    private readonly uint _maximumBytesPerSecond;
    /// <summary>
    /// Cached value of <see cref="SynapseSocket.Core.Configuration.ReliableConfig.AckBatchingEnabled"/> to avoid repeated config lookups on the hot maintenance path.
    /// </summary>
    private readonly bool _isAckBatchingEnabled;
    /// <summary>
    /// Sentinel value indicating that ACK batching interval is unset (batching disabled).
    /// </summary>
    public const long UnsetAckBatchingIntervalTicks = 0;
    /// <summary>
    /// Sentinel value indicating that segment assembly timeout is disabled.
    /// </summary>
    private const long UnsetSegmentAssemblyTimeoutTicks = 0;

    /// <summary>
    /// Runs one maintenance pass over every connection: keep-alive, timeout detection, reliable retransmission,
    /// segment-assembly timeout, and inbound rate-counter reset. Called once per <see cref="Poll"/>.
    /// </summary>
    /// <param name="nowTicks">Current time in <see cref="DateTime.Ticks"/>.</param>
    private void RunMaintenance(long nowTicks)
    {
        if (_transmissionEngine is null)
            return;

        IReadOnlyList<SynapseConnection> connections = Connections.Connections;

        // Iterate backward: a connection removed mid-sweep (timeout or reliable-exhaustion does a swap-remove of
        // the last entry into the freed slot) must not cause an entry to be skipped or visited twice. The engine is
        // single-threaded, so a removal only ever targets the connection currently being processed.
        for (int i = connections.Count - 1; i >= 0; i--)
        {
            if (i >= connections.Count)
                continue;

            SynapseConnection connection = connections[i];

            try
            {
                if (PerformKeepAlive(nowTicks, connection))
                {
                    RetransmitReliable(nowTicks, connection);
                    TimeoutAssembledSegments(nowTicks, connection);
                    ResetInboundRateCounters(nowTicks, connection);
                }
            }
            catch (Exception unexpectedException)
            {
                UnhandledException?.Invoke(unexpectedException);
            }
        }
    }

    /// <summary>
    /// Flushes batched outbound ACKs for every connection. Called once per <see cref="Poll"/> when ACK batching is enabled.
    /// </summary>
    private void FlushPendingAcks()
    {
        IReadOnlyList<SynapseConnection> connections = Connections.Connections;

        for (int i = connections.Count - 1; i >= 0; i--)
        {
            if (i >= connections.Count)
                continue;

            try
            {
                connections[i].SendPendingAcks();
            }
            catch (Exception unexpectedException)
            {
                UnhandledException?.Invoke(unexpectedException);
            }
        }
    }

    /// <summary>
    /// Keep-alive: detects timed-out peers and emits heartbeats with exponential backoff.
    /// </summary>
    /// <returns>True if the connection is still valid. False if the connection was disconnected (timed out).</returns>
    private bool PerformKeepAlive(long nowTicks, SynapseConnection synapseConnection)
    {
        if (_transmissionEngine is null)
            return false;

        if (synapseConnection.State == ConnectionState.Disconnected)
            return false;

        // Timeout check - treated as a (benign) violation.
        // Default initial action is Kick (disconnect without blacklisting); a listener can escalate or downgrade.
        if (nowTicks - synapseConnection.LastReceivedTicks > _connectionTimeoutTicks)
        {
            synapseConnection.State = ConnectionState.Disconnected;
            Connections.Remove(synapseConnection.RemoteEndPoint, out _);
            ReturnReorderBufferToPool(synapseConnection);
            SynapseConnection.DrainPendingReliableQueue(synapseConnection);
            RaiseConnectionClosed(synapseConnection);
            HandleViolation(synapseConnection.RemoteEndPoint, synapseConnection.Signature, ViolationReason.Timeout, 0, null, ViolationAction.Kick);

            return false;
        }

        // Keep-alive: skip when traffic is already flowing; reset backoff when active.
        if (nowTicks - synapseConnection.LastReceivedTicks < _connectionKeepAliveTicks)
        {
            synapseConnection.UnansweredKeepAlives = 0;
            return true;
        }

        // Exponential backoff: double the interval for each consecutive unanswered keep-alive, capped at 8×.
        long effectiveIntervalTicks = _connectionKeepAliveTicks << Math.Min(synapseConnection.UnansweredKeepAlives, 3);

        if (nowTicks - synapseConnection.LastKeepAliveSentTicks < effectiveIntervalTicks)
            return true;

        // Track whether the previous keep-alive was answered.
        if (synapseConnection.LastKeepAliveSentTicks > 0 && synapseConnection.LastReceivedTicks < synapseConnection.LastKeepAliveSentTicks)
            synapseConnection.UnansweredKeepAlives++;
        else
            synapseConnection.UnansweredKeepAlives = 0;

        synapseConnection.LastKeepAliveSentTicks = nowTicks;

        _transmissionEngine.SendKeepAlive(synapseConnection);

        return true;
    }

    /// <summary>
    /// Resets the per-connection inbound packet and byte counters for <paramref name="synapseConnection"/>
    /// if the one-second window has elapsed. No-op when both
    /// <see cref="SynapseSocket.Core.Configuration.SecurityConfig.MaximumPacketsPerSecond"/> and
    /// <see cref="SynapseSocket.Core.Configuration.SecurityConfig.MaximumBytesPerSecond"/> are disabled.
    /// </summary>
    private void ResetInboundRateCounters(long nowTicks, SynapseConnection synapseConnection)
    {
        if (_maximumPacketsPerSecond == SecurityConfig.DisabledMaximumPacketsPerSecond && _maximumBytesPerSecond == SecurityConfig.DisabledMaximumBytesPerSecond)
            return;

        synapseConnection.ResetInboundRateCounters(nowTicks);
    }

    /// <summary>
    /// Reliable retransmission sweep: any pending reliable packet whose resend timer has expired is re-sent.
    /// Packets exceeding the retry cap are treated as a <see cref="ViolationReason.ReliableExhausted"/> violation.
    /// </summary>
    private void RetransmitReliable(long nowTicks, SynapseConnection synapseConnection)
    {
        if (_transmissionEngine is null)
            return;

        if (synapseConnection.State != ConnectionState.Connected)
            return;

        foreach (KeyValuePair<ushort, SynapseConnection.PendingReliable> keyValuePair in synapseConnection.PendingReliableQueue)
        {
            SynapseConnection.PendingReliable pendingReliable = keyValuePair.Value;

            if (nowTicks - pendingReliable.SentTicks < _reliableResendTicks)
                continue;

            if (pendingReliable.Retries >= _maximumReliableRetries)
            {
                // Remove the exhausted entry and free its buffer immediately. The loop is abandoned (return) right
                // after, so mutating the dictionary here does not invalidate the in-progress enumeration. The engine
                // is single-threaded, so no resend can be reading the buffer.
                if (synapseConnection.PendingReliableQueue.Remove(keyValuePair.Key, out SynapseConnection.PendingReliable? exhausted))
                    SynapseConnection.ReleasePendingReliable(exhausted);
                Telemetry.OnLost();
                HandleViolation(synapseConnection.RemoteEndPoint, synapseConnection.Signature, ViolationReason.ReliableExhausted, 0, ViolationReliableExhausted, ViolationAction.Kick);

                return;
            }

            pendingReliable.Retries++;
            pendingReliable.SentTicks = nowTicks;

            Telemetry.OnReliableResend();

            List<ArraySegment<byte>>? segments = pendingReliable.Segments;
            if (segments is null)
                continue;

            try
            {
                for (int i = 0; i < segments.Count; i++)
                    _transmissionEngine.SendRaw(segments[i], synapseConnection.RemoteEndPoint);
            }
            catch (Exception)
            {
                // Best-effort resend: a transient send failure must not abort the sweep. The packet stays pending
                // and is retried on a later sweep.
            }
        }
    }

    /// <summary>
    /// Evicts incomplete segment assemblies (reliable or unreliable) that have exceeded <see cref="SynapseSocket.Core.Configuration.SegmentConfig.AssemblyTimeoutMilliseconds"/> on each connection that has an active segmenter.
    /// </summary>
    private void TimeoutAssembledSegments(long nowTicks, SynapseConnection synapseConnection)
    {
        if (_segmentAssemblyTimeoutTicks == UnsetSegmentAssemblyTimeoutTicks)
            return;

        synapseConnection.Reassembler?.RemoveExpiredSegments(nowTicks, _segmentAssemblyTimeoutTicks);
    }
}
