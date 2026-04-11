using System;
using System.Collections.Generic;
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
    /// <summary>
    /// UTC ticks of the last ACK batch flush. Used by <see cref="AckBatchFlushSweep"/> to enforce the configured interval.
    /// </summary>
    private long _lastAckFlushTicks;

    /// <summary>
    /// Background loop that periodically runs keep-alive, retransmit, and segment-timeout sweeps.
    /// </summary>
    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        int maintenanceLoopDelayMilliseconds = 50;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                ProgressiveKeepAliveSweep(nowTicks, cancellationToken);
                ReliableRetransmitSweep(nowTicks, cancellationToken);
                SegmentAssemblyTimeoutSweep(nowTicks);
                AckBatchFlushSweep(nowTicks, cancellationToken);
                Security.RemoveExpiredRateBuckets(nowTicks, TimeSpan.FromMinutes(5).Ticks);
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
                await Task.Delay(maintenanceLoopDelayMilliseconds, cancellationToken).ConfigureAwait(false);
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
    private void ProgressiveKeepAliveSweep(long nowTicks, CancellationToken cancellationToken)
    {
        if (_sender is null)
            return;

        long keepAliveTicks = TimeSpan.FromMilliseconds(Config.Connection.KeepAliveIntervalMilliseconds).Ticks;
        long timeoutTicks = TimeSpan.FromMilliseconds(Config.Connection.TimeoutMilliseconds).Ticks;

        List<SynapseConnection> snapshot = [];

        foreach (SynapseConnection synapseConnection in Connections.Snapshot())
            snapshot.Add(synapseConnection);

        foreach (SynapseConnection synapseConnection in snapshot)
        {
            if (synapseConnection.State == ConnectionState.Disconnected)
                continue;

            // Timeout check - treated as a (benign) violation.
            // Default initial action is Kick (disconnect without blacklisting); a listener can escalate or downgrade.
            if (nowTicks - synapseConnection.LastReceivedTicks > timeoutTicks)
            {
                synapseConnection.State = ConnectionState.Disconnected;
                Connections.Remove(synapseConnection.RemoteEndPoint, out _);
                ReturnReorderBufferToPool(synapseConnection);
                RaiseConnectionClosed(synapseConnection);
                HandleViolation(synapseConnection.RemoteEndPoint, synapseConnection.Signature, ViolationReason.Timeout, 0, null, ViolationAction.Kick);
                continue;
            }

            // Keep-alive
            if (nowTicks - synapseConnection.LastKeepAliveSentTicks > keepAliveTicks)
            {
                synapseConnection.LastKeepAliveSentTicks = nowTicks;
                _ = _sender.SendKeepAliveAsync(synapseConnection, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Flushes queued ACKs from all ingress engines when ACK batching is enabled and the flush interval has elapsed.
    /// </summary>
    private void AckBatchFlushSweep(long nowTicks, CancellationToken cancellationToken)
    {
        if (!Config.Reliable.AckBatchingEnabled)
            return;

        long intervalTicks = Config.Reliable.AckBatchIntervalMilliseconds * TimeSpan.TicksPerMillisecond;

        if (nowTicks - _lastAckFlushTicks < intervalTicks)
            return;

        _lastAckFlushTicks = nowTicks;

        foreach (IngressEngine engine in _ingressEngines)
            engine.FlushPendingAcks(cancellationToken);
    }

    /// <summary>
    /// Reliable retransmission sweep: any pending reliable packet whose resend timer has expired is re-sent.
    /// Packets exceeding the retry cap are treated as a <see cref="ViolationReason.ReliableExhausted"/> violation.
    /// </summary>
    private void ReliableRetransmitSweep(long nowTicks, CancellationToken cancellationToken)
    {
        if (_sender is null)
            return;

        long resendTicks = TimeSpan.FromMilliseconds(Config.Reliable.ResendMilliseconds).Ticks;

        foreach (SynapseConnection synapseConnection in Connections.Snapshot())
        {
            if (synapseConnection.State != ConnectionState.Connected)
                continue;

            foreach (KeyValuePair<ushort, SynapseConnection.PendingReliable> keyValuePair in synapseConnection.PendingReliableQueue)
            {
                SynapseConnection.PendingReliable pendingReliable = keyValuePair.Value;

                if (nowTicks - pendingReliable.SentTicks < resendTicks)
                    continue;

                if (pendingReliable.Retries >= Config.Reliable.MaximumRetries)
                {
                    synapseConnection.PendingReliableQueue.TryRemove(keyValuePair.Key, out _);
                    SynapseConnection.ReturnPendingReliableBuffers(pendingReliable);
                    Telemetry.OnLost();
                    HandleViolation(synapseConnection.RemoteEndPoint, synapseConnection.Signature, ViolationReason.ReliableExhausted, 0, $"seq={pendingReliable.Sequence}", ViolationAction.Kick);
                    continue;
                }

                pendingReliable.Retries++;
                pendingReliable.SentTicks = nowTicks;
                Telemetry.OnReliableResend();

                if (pendingReliable.Segments is not null)
                {
                    for (int i = 0; i < pendingReliable.SegmentCount; i++)
                        _ = _sender.SendRawAsync(pendingReliable.Segments[i], synapseConnection.RemoteEndPoint, cancellationToken);
                }
                else
                {
                    _ = _sender.SendRawAsync(new(pendingReliable.Payload, 0, pendingReliable.PacketLength), synapseConnection.RemoteEndPoint, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Evicts incomplete segment assemblies (reliable or unreliable) that have exceeded <see cref="SynapseSocket.Core.Configuration.SynapseConfig.SegmentAssemblyTimeoutMilliseconds"/> on each connection that has an active segmenter.
    /// </summary>
    private void SegmentAssemblyTimeoutSweep(long nowTicks)
    {
        if (!_isSegmentingEnabled || Config.SegmentAssemblyTimeoutMilliseconds == 0)
            return;

        long timeoutTicks = TimeSpan.FromMilliseconds(Config.SegmentAssemblyTimeoutMilliseconds).Ticks;

        foreach (SynapseConnection synapseConnection in Connections.Snapshot())
            synapseConnection.Reassembler?.RemoveExpiredSegments(nowTicks, timeoutTicks);
    }
}