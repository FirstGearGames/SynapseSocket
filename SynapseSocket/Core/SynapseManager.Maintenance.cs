using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core.Events;
using SynapseSocket.Transport;

namespace SynapseSocket.Core;

/// <summary>
/// Background maintenance for <see cref="SynapseManager"/>: progressive keep-alive sweeps, timeout detection, and reliable retransmission.
/// Implemented as a partial class to separate feature sets per the spec.
/// </summary>
public sealed partial class SynapseManager
{
    private const string ViolationReliableExhausted = "Connection exceeded the maximum reliable packet retry limit.";
    /// <summary>
    /// UTC ticks of the last ACK batch flush. Used by <see cref="SendPendingAcks"/> to enforce the configured interval.
    /// </summary>
    private long _lastAckFlushTicks;
    /// <summary>
    /// Ticks between keep-alive heartbeats, derived from <see cref="SynapseSocket.Core.Configuration.ConnectionConfig.KeepAliveIntervalMilliseconds"/>.
    /// </summary>
    private long _connectionKeepAliveTicks;
    /// <summary>
    /// Ticks of idle time after which a connection is considered timed out, derived from <see cref="SynapseSocket.Core.Configuration.ConnectionConfig.TimeoutMilliseconds"/>.
    /// </summary>
    private long _connectionTimeoutTicks;
    /// <summary>
    /// Ticks between reliable packet retransmission attempts, derived from <see cref="SynapseSocket.Core.Configuration.ReliableConfig.ResendMilliseconds"/>.
    /// </summary>
    private long _reliableResendTicks;
    /// <summary>
    /// Maximum number of retransmission attempts before a reliable packet is considered lost, derived from <see cref="SynapseSocket.Core.Configuration.ReliableConfig.MaximumRetries"/>.
    /// </summary>
    private uint _maximumReliableRetries;
    /// <summary>
    /// Ticks after which an incomplete segment assembly is evicted. Set to <see cref="UnsetSegmentAssemblyTimeoutTicks"/> when the timeout is disabled or segmentation is off.
    /// </summary>
    private long _segmentAssemblyTimeoutTicks;
    /// <summary>
    /// Cached value of <see cref="SynapseSocket.Core.Configuration.ReliableConfig.AckBatchingEnabled"/> to avoid repeated config lookups on the hot maintenance path.
    /// </summary>
    private bool _isAckBatchingEnabled;
    /// <summary>
    /// Ticks between ACK batch flushes, derived from <see cref="SynapseSocket.Core.Configuration.ReliableConfig.AckBatchIntervalMilliseconds"/>. Unset when ACK batching is disabled.
    /// </summary>
    private long _ackBatchingIntervalTicks;
    /// <summary>
    /// Index of the connection to process on the next maintenance tick. Wraps around when it reaches the connection count.
    /// </summary>
    private int _nextMaintenanceConnectionIndex;
    /// <summary>
    /// Index of the connection to process on the next ACK flush tick. Reserved for future per-connection ACK batching.
    /// </summary>
    private int _nextAckConnectionIndex;
    /// <summary>
    /// Sentinel value indicating that segment assembly timeout is disabled.
    /// </summary>
    private const long UnsetSegmentAssemblyTimeoutTicks = 0;
    /// <summary>
    /// Sentinel value indicating that ACK batching interval is unset (batching disabled).
    /// </summary>
    private const long UnsetAckBatchingIntervalTicks = 0;

    /// <summary>
    /// Background loop that periodically runs keep-alive, retransmit, and segment-timeout sweeps.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        const int MaintenanceLoopTargetMilliseconds = 50;
        
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Connections is null || Connections.Connections.Count == 0)
            {
                await Task.Delay(MaintenanceLoopTargetMilliseconds, cancellationToken).ConfigureAwait(false);
                continue;
            }

            int connectionsCount = Connections.Connections.Count;

            try
            {
                if (_nextMaintenanceConnectionIndex >= connectionsCount)
                    _nextMaintenanceConnectionIndex = 0;

                SynapseConnection connection = Connections.Connections[_nextMaintenanceConnectionIndex];

                long nowTicks = DateTime.UtcNow.Ticks;

                if (PerformKeepAlive(nowTicks, connection, cancellationToken))
                {
                    RetransmitReliable(nowTicks, connection, cancellationToken);
                    SegmentAssemblyTimeoutSweep(nowTicks, connection);
                    SendPendingAcks(nowTicks, cancellationToken);
                }

                _nextMaintenanceConnectionIndex++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception unexpectedException)
            {
                UnhandledException?.Invoke(unexpectedException);
            }

            try
            {
                /* Wait time is calculated to have iterated all connections
                 * at roughly the target milliseconds. */
                int waitMilliseconds = Math.Max(1, MaintenanceLoopTargetMilliseconds / connectionsCount);
                await Task.Delay(waitMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Progressive keep-alive: iterates a slice of connections per tick so heartbeat traffic is spread across the configured sweep window.
    /// Also detects timeouts and disconnects non-responsive peers.
    /// </summary>
    /// <returns>True if the connection is still valid. False will be returned if the connection had been disconnected.</returns>
    private bool PerformKeepAlive(long nowTicks, SynapseConnection synapseConnection, CancellationToken cancellationToken)
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

        _ = _transmissionEngine.SendKeepAliveAsync(synapseConnection, cancellationToken);

        return true;
    }

    /// <summary>
    /// Flushes queued ACKs from all ingress engines when ACK batching is enabled and the flush interval has elapsed.
    /// </summary>
    private void SendPendingAcks(long nowTicks, CancellationToken cancellationToken)
    {
        if (!Config.Reliable.AckBatchingEnabled)
            return;

        if (nowTicks - _lastAckFlushTicks < _ackBatchingIntervalTicks)
            return;

        _lastAckFlushTicks = nowTicks;

        foreach (IngressEngine engine in _ingressEngines)
            engine.SendPendingAcks(cancellationToken);
    }

    /// <summary>
    /// Reliable retransmission sweep: any pending reliable packet whose resend timer has expired is re-sent.
    /// Packets exceeding the retry cap are treated as a <see cref="ViolationReason.ReliableExhausted"/> violation.
    /// </summary>
    private void RetransmitReliable(long nowTicks, SynapseConnection synapseConnection, CancellationToken cancellationToken)
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
                synapseConnection.PendingReliableQueue.TryRemove(keyValuePair.Key, out _);
                SynapseConnection.ReleasePendingReliable(pendingReliable);
                Telemetry.OnLost();
                HandleViolation(synapseConnection.RemoteEndPoint, synapseConnection.Signature, ViolationReason.ReliableExhausted, 0, ViolationReliableExhausted, ViolationAction.Kick);

                return;
            }

            pendingReliable.Retries++;
            pendingReliable.SentTicks = nowTicks;

            Telemetry.OnReliableResend();

            if (pendingReliable.Segments is not null)
            {
                for (int i = 0; i < pendingReliable.SegmentCount; i++)
                    _ = _transmissionEngine.SendRawAsync(pendingReliable.Segments[i], synapseConnection.RemoteEndPoint, cancellationToken);
            }
            else if (pendingReliable.Payload is not null)
            {
                _ = _transmissionEngine.SendRawAsync(new(pendingReliable.Payload, 0, pendingReliable.PacketLength), synapseConnection.RemoteEndPoint, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Evicts incomplete segment assemblies (reliable or unreliable) that have exceeded <see cref="SynapseSocket.Core.Configuration.SynapseConfig.SegmentAssemblyTimeoutMilliseconds"/> on each connection that has an active segmenter.
    /// </summary>
    private void SegmentAssemblyTimeoutSweep(long nowTicks, SynapseConnection synapseConnection)
    {
        if (_segmentAssemblyTimeoutTicks == UnsetSegmentAssemblyTimeoutTicks)
            return;

        synapseConnection.Reassembler?.RemoveExpiredSegments(nowTicks, _segmentAssemblyTimeoutTicks);
    }
}