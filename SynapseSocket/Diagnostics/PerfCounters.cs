#if PERFTEST
using System.Diagnostics;
using System.Threading;

namespace SynapseSocket.Diagnostics;

/// <summary>
/// Internal performance counters for measuring hot-path iteration speed.
/// DEBUG builds only — guarded by <c>#if PERFTEST</c> at every call site.
/// Each tracked path exposes three fields: last elapsed ticks, cumulative elapsed ticks, and call count.
/// Use <see cref="TicksToMilliseconds"/> to convert to human-readable durations.
/// </summary>
internal sealed class PerfCounters
{
    // ── Full maintenance tick ────────────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent full maintenance loop iteration.
    /// </summary>
    public long MaintenanceTickLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent across all maintenance loop iterations.
    /// </summary>
    public long MaintenanceTickTotalElapsedTicks;
    /// <summary>
    /// Number of completed maintenance loop iterations.
    /// </summary>
    public long MaintenanceTickCallCount;

    // ── ProgressiveKeepAliveSweep ────────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent keep-alive sweep.
    /// </summary>
    public long KeepAliveSweepLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent across all keep-alive sweeps.
    /// </summary>
    public long KeepAliveSweepTotalElapsedTicks;
    /// <summary>
    /// Number of completed keep-alive sweep iterations.
    /// </summary>
    public long KeepAliveSweepCallCount;

    // ── ReliableRetransmitSweep ──────────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent reliable-retransmit sweep.
    /// </summary>
    public long ReliableRetransmitSweepLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent across all reliable-retransmit sweeps.
    /// </summary>
    public long ReliableRetransmitSweepTotalElapsedTicks;
    /// <summary>
    /// Number of completed reliable-retransmit sweep iterations.
    /// </summary>
    public long ReliableRetransmitSweepCallCount;

    // ── SegmentAssemblyTimeoutSweep ──────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent segment-assembly timeout sweep.
    /// </summary>
    public long SegmentAssemblyTimeoutSweepLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent across all segment-assembly timeout sweeps.
    /// </summary>
    public long SegmentAssemblyTimeoutSweepTotalElapsedTicks;
    /// <summary>
    /// Number of completed segment-assembly timeout sweep iterations.
    /// </summary>
    public long SegmentAssemblyTimeoutSweepCallCount;

    // ── AckBatchFlushSweep ───────────────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent ACK batch flush sweep.
    /// </summary>
    public long AckBatchFlushSweepLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent across all ACK batch flush sweeps.
    /// </summary>
    public long AckBatchFlushSweepTotalElapsedTicks;
    /// <summary>
    /// Number of completed ACK batch flush sweep iterations.
    /// </summary>
    public long AckBatchFlushSweepCallCount;

    // ── RemoveExpiredRateBuckets ─────────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent rate-bucket cleanup sweep.
    /// </summary>
    public long RateBucketCleanupLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent across all rate-bucket cleanup sweeps.
    /// </summary>
    public long RateBucketCleanupTotalElapsedTicks;
    /// <summary>
    /// Number of completed rate-bucket cleanup sweep iterations.
    /// </summary>
    public long RateBucketCleanupCallCount;

    // ── ProcessPacket (per datagram) ─────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent packet processing call.
    /// </summary>
    public long ProcessPacketLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent across all packet processing calls.
    /// </summary>
    public long ProcessPacketTotalElapsedTicks;
    /// <summary>
    /// Number of completed packet processing calls.
    /// </summary>
    public long ProcessPacketCallCount;

    // ── DeliverOrdered lock section (per reliable packet) ────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed inside the ordered-delivery lock during the most recent call.
    /// </summary>
    public long DeliverOrderedLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent inside the ordered-delivery lock across all calls.
    /// </summary>
    public long DeliverOrderedTotalElapsedTicks;
    /// <summary>
    /// Number of completed ordered-delivery lock sections.
    /// </summary>
    public long DeliverOrderedCallCount;

    // ── DeliverOrdered callbacks (outside lock, per reliable packet) ──────────
    /// <summary>
    /// Stopwatch ticks elapsed in the PayloadDelivered callback loop in DeliverOrdered during the most recent call.
    /// </summary>
    public long DeliverOrderedCallbacksLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent in the PayloadDelivered callback loop in DeliverOrdered across all calls.
    /// </summary>
    public long DeliverOrderedCallbacksTotalElapsedTicks;
    /// <summary>
    /// Number of times the DeliverOrdered callback loop ran with at least one payload to deliver.
    /// </summary>
    public long DeliverOrderedCallbacksCallCount;

    // ── DateTimeUtcNow (per datagram) ────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent DateTime.UtcNow.Ticks call in the receive loop.
    /// </summary>
    public long DateTimeUtcNowLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent in DateTime.UtcNow.Ticks across all datagrams.
    /// </summary>
    public long DateTimeUtcNowTotalElapsedTicks;
    /// <summary>
    /// Number of DateTime.UtcNow.Ticks calls recorded.
    /// </summary>
    public long DateTimeUtcNowCallCount;

    // ── SecurityFilter (per datagram) ────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent security filter (connection lookup + rate/size check) in the receive loop.
    /// </summary>
    public long SecurityFilterLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent in the security filter across all datagrams.
    /// </summary>
    public long SecurityFilterTotalElapsedTicks;
    /// <summary>
    /// Number of security filter calls recorded.
    /// </summary>
    public long SecurityFilterCallCount;
    
    // ── SecurityFilter Inspect (per datagram) ────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent InspectEstablished/InspectNew call inside the security filter.
    /// </summary>
    public long SecurityFilterInspectLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent in InspectEstablished/InspectNew across all datagrams.
    /// </summary>
    public long SecurityFilterInspectTotalElapsedTicks;
    /// <summary>
    /// Number of security filter inspect calls recorded.
    /// </summary>
    public long SecurityFilterInspectCallCount;

    // ── HeaderParse ──────────────────────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent PacketHeader.Read call.
    /// </summary>
    public long HeaderParseLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent in PacketHeader.Read across all calls.
    /// </summary>
    public long HeaderParseTotalElapsedTicks;
    /// <summary>
    /// Number of completed PacketHeader.Read calls.
    /// </summary>
    public long HeaderParseCallCount;

    // ── ConnectionLookup ─────────────────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent connection table lookup in ProcessPacket.
    /// </summary>
    public long ConnectionLookupLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent in connection table lookups across all ProcessPacket calls.
    /// </summary>
    public long ConnectionLookupTotalElapsedTicks;
    /// <summary>
    /// Number of completed connection table lookups in ProcessPacket.
    /// </summary>
    public long ConnectionLookupCallCount;

    // ── PayloadCopy ──────────────────────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent Buffer.BlockCopy of a received payload.
    /// </summary>
    public long PayloadCopyLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent copying received payloads across all calls.
    /// </summary>
    public long PayloadCopyTotalElapsedTicks;
    /// <summary>
    /// Number of completed payload copies.
    /// </summary>
    public long PayloadCopyCallCount;

    // ── EnqueueOrSendAck ─────────────────────────────────────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed during the most recent EnqueueOrSendAck call.
    /// </summary>
    public long EnqueueOrSendAckLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent in EnqueueOrSendAck across all calls.
    /// </summary>
    public long EnqueueOrSendAckTotalElapsedTicks;
    /// <summary>
    /// Number of completed EnqueueOrSendAck calls.
    /// </summary>
    public long EnqueueOrSendAckCallCount;

    // ── PayloadDeliveredCallback (unreliable / None path) ────────────────────
    /// <summary>
    /// Stopwatch ticks elapsed in the PayloadDelivered callback for the most recent unreliable packet.
    /// </summary>
    public long PayloadDeliveredCallbackLastElapsedTicks;
    /// <summary>
    /// Cumulative Stopwatch ticks spent in PayloadDelivered callbacks on the unreliable path across all calls.
    /// </summary>
    public long PayloadDeliveredCallbackTotalElapsedTicks;
    /// <summary>
    /// Number of completed PayloadDelivered callbacks on the unreliable path.
    /// </summary>
    public long PayloadDeliveredCallbackCallCount;
    /// <summary>
    /// Number of times to run perf encapsulated task in a loop.
    /// </summary>
    public const int IterationMultiplier = 200; //Fails frequently over 200; not sure why. Tried generously increasing MaximumPacketsPerSecond, issue seems to be in SecurityProvider.Allow().
    // ── Methods ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a Stopwatch tick count to milliseconds.
    /// </summary>
    /// <param name="ticks">Elapsed ticks from <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <returns>Elapsed time in milliseconds.</returns>
    public static double TicksToMilliseconds(long ticks) => (double)ticks / Stopwatch.Frequency * 1000.0;

    /// <summary>
    /// Records a completed full maintenance loop iteration.
    /// </summary>
    internal void RecordMaintenanceTick(long elapsedTicks)
    {
        Interlocked.Exchange(ref MaintenanceTickLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref MaintenanceTickTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref MaintenanceTickCallCount);
    }

    /// <summary>
    /// Records a completed keep-alive sweep.
    /// </summary>
    internal void RecordKeepAliveSweep(long elapsedTicks)
    {
        Interlocked.Exchange(ref KeepAliveSweepLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref KeepAliveSweepTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref KeepAliveSweepCallCount);
    }

    /// <summary>
    /// Records a completed reliable-retransmit sweep.
    /// </summary>
    internal void RecordReliableRetransmitSweep(long elapsedTicks)
    {
        Interlocked.Exchange(ref ReliableRetransmitSweepLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref ReliableRetransmitSweepTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref ReliableRetransmitSweepCallCount);
    }

    /// <summary>
    /// Records a completed segment-assembly timeout sweep.
    /// </summary>
    internal void RecordSegmentAssemblyTimeoutSweep(long elapsedTicks)
    {
        Interlocked.Exchange(ref SegmentAssemblyTimeoutSweepLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref SegmentAssemblyTimeoutSweepTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref SegmentAssemblyTimeoutSweepCallCount);
    }

    /// <summary>
    /// Records a completed ACK batch flush sweep.
    /// </summary>
    internal void RecordAckBatchFlushSweep(long elapsedTicks)
    {
        Interlocked.Exchange(ref AckBatchFlushSweepLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref AckBatchFlushSweepTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref AckBatchFlushSweepCallCount);
    }

    /// <summary>
    /// Records a completed rate-bucket cleanup sweep.
    /// </summary>
    internal void RecordRateBucketCleanup(long elapsedTicks)
    {
        Interlocked.Exchange(ref RateBucketCleanupLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref RateBucketCleanupTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref RateBucketCleanupCallCount);
    }

    /// <summary>
    /// Records a completed packet processing call.
    /// </summary>
    internal void RecordProcessPacket(long elapsedTicks)
    {
        Interlocked.Exchange(ref ProcessPacketLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref ProcessPacketTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref ProcessPacketCallCount);
    }

    /// <summary>
    /// Records a completed ordered-delivery lock section.
    /// </summary>
    internal void RecordDeliverOrdered(long elapsedTicks)
    {
        Interlocked.Exchange(ref DeliverOrderedLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref DeliverOrderedTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref DeliverOrderedCallCount);
    }

    /// <summary>
    /// Records the PayloadDelivered callback loop in DeliverOrdered (outside the lock).
    /// </summary>
    internal void RecordDeliverOrderedCallbacks(long elapsedTicks)
    {
        Interlocked.Exchange(ref DeliverOrderedCallbacksLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref DeliverOrderedCallbacksTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref DeliverOrderedCallbacksCallCount);
    }

    /// <summary>
    /// Records the InspectEstablished/InspectNew call inside the security filter.
    /// </summary>
    internal void RecordSecurityFilterInspect(long elapsedTicks)
    {
        Interlocked.Exchange(ref SecurityFilterInspectLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref SecurityFilterInspectTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref SecurityFilterInspectCallCount);
    }

    /// <summary>
    /// Records a DateTime.UtcNow.Ticks call in the receive loop.
    /// </summary>
    internal void RecordDateTimeUtcNow(long elapsedTicks)
    {
        Interlocked.Exchange(ref DateTimeUtcNowLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref DateTimeUtcNowTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref DateTimeUtcNowCallCount);
    }

    /// <summary>
    /// Records a security filter pass (connection lookup + rate/size check) in the receive loop.
    /// </summary>
    internal void RecordSecurityFilter(long elapsedTicks)
    {
        Interlocked.Exchange(ref SecurityFilterLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref SecurityFilterTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref SecurityFilterCallCount);
    }

    /// <summary>
    /// Records a completed PacketHeader.Read call.
    /// </summary>
    internal void RecordHeaderParse(long elapsedTicks)
    {
        Interlocked.Exchange(ref HeaderParseLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref HeaderParseTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref HeaderParseCallCount);
    }

    /// <summary>
    /// Records a completed connection table lookup inside ProcessPacket.
    /// </summary>
    internal void RecordConnectionLookup(long elapsedTicks)
    {
        Interlocked.Exchange(ref ConnectionLookupLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref ConnectionLookupTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref ConnectionLookupCallCount);
    }

    /// <summary>
    /// Records a completed Buffer.BlockCopy of a received payload.
    /// </summary>
    internal void RecordPayloadCopy(long elapsedTicks)
    {
        Interlocked.Exchange(ref PayloadCopyLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref PayloadCopyTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref PayloadCopyCallCount);
    }

    /// <summary>
    /// Records a completed EnqueueOrSendAck call.
    /// </summary>
    internal void RecordEnqueueOrSendAck(long elapsedTicks)
    {
        Interlocked.Exchange(ref EnqueueOrSendAckLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref EnqueueOrSendAckTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref EnqueueOrSendAckCallCount);
    }

    /// <summary>
    /// Records a completed PayloadDelivered callback on the unreliable (None) packet path.
    /// </summary>
    internal void RecordPayloadDeliveredCallback(long elapsedTicks)
    {
        Interlocked.Exchange(ref PayloadDeliveredCallbackLastElapsedTicks, elapsedTicks);
        Interlocked.Add(ref PayloadDeliveredCallbackTotalElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref PayloadDeliveredCallbackCallCount);
    }
}
#endif