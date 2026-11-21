using System;
using CodeBoost.CodeAnalysis;

namespace SynapseSocket.Connections;

public sealed partial class SynapseConnection
{
    /// <summary>
    /// True to exempt this connection from the per-connection packet and byte rate limits.
    /// </summary>
    /// <remarks>
    /// For a peer whose traffic for many others arrives on this one connection, such as the server side of a relayed room, which sends
    /// every client's packets down a single link. The oversized packet check and every other security check still apply. Cleared when
    /// the connection returns to its pool.
    /// </remarks>
    public bool IsRateLimitExempt { get; set; }
    /// <summary>
    /// The session token this side expects at the end of every disconnect, keep-alive, acknowledgement and segment acknowledgement from
    /// the peer, unless <see cref="SessionTokenState"/> is <see cref="Connections.SessionTokenState.Unsupported"/>. It is random, chosen
    /// when the connection is initialised, and opens the nonce of every handshake this side sends on the connection, so the peer learns
    /// it whichever of those handshakes it answers.
    /// </summary>
    /// <remarks>
    /// A reconnect that resets the session keeps it, because a handshake this side sent for the old session can still be in flight,
    /// and a peer that accepts that one instead of the new one must still present a token this side accepts.
    /// </remarks>
    [PoolResettableMember]
    internal uint LocalSessionToken;
    /// <summary>
    /// The session token this side ends its own control packets with while <see cref="SessionTokenState"/> is
    /// <see cref="Connections.SessionTokenState.Supported"/>: the first bytes of the nonce in the peer's handshake that established the
    /// session, which is the peer's <see cref="LocalSessionToken"/>.
    /// </summary>
    [PoolResettableMember]
    internal uint RemoteSessionToken;
    /// <summary>
    /// Whether the peer takes part in session tokens, read from the handshake that established the session.
    /// </summary>
    [PoolResettableMember]
    internal SessionTokenState SessionTokenState;

    /// <summary>
    /// The payload bytes this peer has held waiting for a reliable gap: <see cref="ReorderBufferByteCount"/> and
    /// <see cref="SplitMessageByteCount"/> together, which the ingress path keeps within the peer's byte allowance.
    /// </summary>
    internal ulong HeldByteCount => ReorderBufferByteCount + SplitMessageByteCount;
    /// <summary>
    /// The payload bytes held in <see cref="ReorderBuffer"/>.
    /// </summary>
    [PoolResettableMember]
    internal ulong ReorderBufferByteCount;
    /// <summary>
    /// The reliable messages this peer has in reassembly, one entry each, packed at the front with <see cref="SplitMessageCount"/> in use.
    /// </summary>
    /// <remarks>
    /// Mirrors the reliable assemblies held by <see cref="Reassembler"/>, which publishes no byte count, so what they hold counts toward
    /// <see cref="HeldByteCount"/>. Allocated once with the connection and kept across returns to the pool.
    /// </remarks>
    [PoolResettableMember]
    internal readonly SplitMessage[] SplitMessages = new SplitMessage[SplitMessageTableLength];
    /// <summary>
    /// The entries of <see cref="SplitMessages"/> in use.
    /// </summary>
    [PoolResettableMember]
    internal int SplitMessageCount;
    /// <summary>
    /// The payload bytes the entries of <see cref="SplitMessages"/> hold together.
    /// </summary>
    [PoolResettableMember]
    internal ulong SplitMessageByteCount;
    /// <summary>
    /// The length of <see cref="SplitMessages"/>, which is the default cap on messages in reassembly.
    /// </summary>
    internal const int SplitMessageTableLength = 16;
    /// <summary>
    /// The index <see cref="FindSplitMessage"/> returns for a message with no entry in <see cref="SplitMessages"/>.
    /// </summary>
    internal const int UnsetSplitMessageIndex = -1;

    /// <summary>
    /// Number of packets received by this connection since <see cref="_inboundRateCountersResetTick"/> was last set.
    /// Incremented on every inbound packet and cleared by <see cref="ResetInboundRateCounters"/>.
    /// </summary>
    [PoolResettableMember]
    private int _receivedByPacketCount;
    /// <summary>
    /// Number of bytes received by this connection since <see cref="_inboundRateCountersResetTick"/> was last set.
    /// Incremented by each inbound packet's length and cleared by <see cref="ResetInboundRateCounters"/>.
    /// </summary>
    [PoolResettableMember]
    private int _receivedByBytesCount;
    /// <summary>
    /// Monotonic ticks of the last time the inbound rate counters were reset to zero.
    /// Used to enforce the one-second window for per-connection rate limiting across both
    /// <see cref="_receivedByPacketCount"/> and <see cref="_receivedByBytesCount"/>.
    /// </summary>
    [PoolResettableMember]
    private long _inboundRateCountersResetTick;

    /// <summary>
    /// Increments the inbound packet counter and returns whether the connection is within the allowed rate.
    /// Returns false when <see cref="_receivedByPacketCount"/> exceeds <paramref name="maximumPacketsPerSecond"/>.
    /// </summary>
    /// <param name="maximumPacketsPerSecond">The per-connection packet rate cap for the current second.</param>
    /// <returns>True if the packet should be processed; false if it should be dropped.</returns>
    internal bool AllowReceivePacket(uint maximumPacketsPerSecond)
    {
        if (++_receivedByPacketCount > maximumPacketsPerSecond)
            return false;

        return true;
    }

    /// <summary>
    /// Adds <paramref name="packetLength"/> to the inbound byte counter and returns whether the
    /// connection is within the allowed rate. Returns false when
    /// <see cref="_receivedByBytesCount"/> exceeds <paramref name="maximumBytesPerSecond"/>.
    /// </summary>
    /// <param name="packetLength">Size of the inbound packet in bytes (assumed non-negative and already validated by the oversize check).</param>
    /// <param name="maximumBytesPerSecond">The per-connection byte rate cap for the current second.</param>
    /// <returns>True if the packet should be processed; false if it should be dropped.</returns>
    internal bool AllowReceiveBytes(int packetLength, uint maximumBytesPerSecond)
    {
        _receivedByBytesCount += packetLength;

        if (_receivedByBytesCount > maximumBytesPerSecond)
            return false;

        return true;
    }

    /// <summary>
    /// Resets both <see cref="_receivedByPacketCount"/> and <see cref="_receivedByBytesCount"/>
    /// to zero once per second. Has no effect if called within the same one-second window as the
    /// previous reset.
    /// </summary>
    /// <param name="nowTicks">Current monotonic ticks, used to determine whether the one-second window has elapsed.</param>
    internal void ResetInboundRateCounters(long nowTicks)
    {
        if (nowTicks - _inboundRateCountersResetTick < TimeSpan.TicksPerSecond)
            return;

        _inboundRateCountersResetTick = nowTicks;
        _receivedByPacketCount = 0;
        _receivedByBytesCount = 0;
    }

    /// <summary>
    /// Finds the entry of <see cref="SplitMessages"/> counting the reliable message sent under <paramref name="segmentId"/>.
    /// </summary>
    /// <param name="segmentId">The segment id the message's segments carry.</param>
    /// <returns>The entry's index, or <see cref="UnsetSplitMessageIndex"/> when the message has none.</returns>
    internal int FindSplitMessage(ushort segmentId)
    {
        for (int i = 0; i < SplitMessageCount; i++)
        {
            if (SplitMessages[i].SegmentId == segmentId)
                return i;
        }

        return UnsetSplitMessageIndex;
    }

    /// <summary>
    /// Counts a segment <see cref="Reassembler"/> stored for the reliable message sent under <paramref name="segmentId"/>, opening an entry
    /// for the message first when <paramref name="splitMessageIndex"/> is <see cref="UnsetSplitMessageIndex"/>.
    /// </summary>
    /// <param name="splitMessageIndex">The message's entry, or <see cref="UnsetSplitMessageIndex"/> to open one; the caller has checked there is room.</param>
    /// <param name="segmentId">The segment id the message's segments carry.</param>
    /// <param name="segmentLength">The stored segment's payload length.</param>
    /// <param name="nowTicks">When the segment arrived, which for a new entry is when its message began, as the reassembler records it.</param>
    /// <returns>The message's entry.</returns>
    internal int AddSplitMessageBytes(int splitMessageIndex, ushort segmentId, int segmentLength, long nowTicks)
    {
        if (splitMessageIndex == UnsetSplitMessageIndex)
        {
            splitMessageIndex = SplitMessageCount++;
            SplitMessages[splitMessageIndex] = new(segmentId, nowTicks);
        }

        SplitMessages[splitMessageIndex].HeldLength += (uint)segmentLength;
        SplitMessageByteCount += (uint)segmentLength;

        return splitMessageIndex;
    }

    /// <summary>
    /// Stops counting the reliable message at <paramref name="splitMessageIndex"/>, whose assembly is complete or gone.
    /// </summary>
    /// <param name="splitMessageIndex">The message's entry.</param>
    internal void RemoveSplitMessage(int splitMessageIndex)
    {
        SplitMessageByteCount -= SplitMessages[splitMessageIndex].HeldLength;
        SplitMessages[splitMessageIndex] = SplitMessages[--SplitMessageCount];
    }

    /// <summary>
    /// Stops counting every reliable message whose first segment arrived longer ago than <paramref name="timeoutTicks"/>, by the rule
    /// <see cref="CodeBoost.Networking.PacketReassembler.RemoveExpiredSegments"/> discards their assemblies by.
    /// </summary>
    /// <param name="nowTicks">The current time, the same one the reassembler's sweep is given.</param>
    /// <param name="timeoutTicks">How long a message may wait for its segments.</param>
    internal void RemoveExpiredSplitMessages(long nowTicks, long timeoutTicks)
    {
        // Backward, so the entry swapped into a removed slot has already been checked.
        for (int i = SplitMessageCount - 1; i >= 0; i--)
        {
            if (nowTicks - SplitMessages[i].FirstReceivedTicks > timeoutTicks)
                RemoveSplitMessage(i);
        }
    }

    /// <summary>
    /// True when a duplicate segment of the message at <paramref name="splitMessageIndex"/> may draw a segment acknowledgement: none has
    /// gone out for the message within <paramref name="intervalTicks"/>, or the message has no entry to keep the time in.
    /// </summary>
    /// <param name="splitMessageIndex">The message's entry, or <see cref="UnsetSplitMessageIndex"/> for one the table does not count.</param>
    /// <param name="nowTicks">The current time.</param>
    /// <param name="intervalTicks">The least time between two segment acknowledgements for one message.</param>
    /// <returns>True when the acknowledgement may be sent.</returns>
    internal bool IsSegmentAckDue(int splitMessageIndex, long nowTicks, long intervalTicks)
    {
        // A message the table does not count has nowhere to keep the time, so its acknowledgements go unpaced, as they always have.
        if (splitMessageIndex == UnsetSplitMessageIndex)
            return true;

        long lastSegmentAckTicks = SplitMessages[splitMessageIndex].LastSegmentAckTicks;

        return lastSegmentAckTicks == SplitMessage.UnsetSegmentAckTicks || nowTicks - lastSegmentAckTicks >= intervalTicks;
    }

    /// <summary>
    /// Records that a segment acknowledgement for the message at <paramref name="splitMessageIndex"/> went out at
    /// <paramref name="nowTicks"/>, which paces the next one a duplicate segment can draw.
    /// </summary>
    /// <param name="splitMessageIndex">The message's entry, or <see cref="UnsetSplitMessageIndex"/> for one the table does not count.</param>
    /// <param name="nowTicks">When the acknowledgement went out.</param>
    internal void RecordSegmentAck(int splitMessageIndex, long nowTicks)
    {
        if (splitMessageIndex != UnsetSplitMessageIndex)
            SplitMessages[splitMessageIndex].LastSegmentAckTicks = nowTicks;
    }

    /// <summary>
    /// One reliable message in reassembly, as <see cref="SplitMessages"/> counts it.
    /// </summary>
    internal struct SplitMessage
    {
        /// <summary>
        /// The segment id the message's segments carry.
        /// </summary>
        public ushort SegmentId;
        /// <summary>
        /// The payload bytes of the message's segments stored so far, each counted once.
        /// </summary>
        public uint HeldLength;
        /// <summary>
        /// When the message's first segment arrived, which its expiry is measured from.
        /// </summary>
        public long FirstReceivedTicks;
        /// <summary>
        /// When the last segment acknowledgement for the message went out, or <see cref="UnsetSegmentAckTicks"/> before the first.
        /// </summary>
        public long LastSegmentAckTicks;
        /// <summary>
        /// The value of <see cref="LastSegmentAckTicks"/> before any segment acknowledgement for the message has gone out.
        /// </summary>
        public const long UnsetSegmentAckTicks = long.MinValue;

        /// <summary>
        /// Opens an entry for a message whose first segment arrived at <paramref name="firstReceivedTicks"/>.
        /// </summary>
        /// <param name="segmentId">The segment id the message's segments carry.</param>
        /// <param name="firstReceivedTicks">When the message's first segment arrived.</param>
        public SplitMessage(ushort segmentId, long firstReceivedTicks)
        {
            SegmentId = segmentId;
            HeldLength = 0;
            FirstReceivedTicks = firstReceivedTicks;
            LastSegmentAckTicks = UnsetSegmentAckTicks;
        }
    }
}
