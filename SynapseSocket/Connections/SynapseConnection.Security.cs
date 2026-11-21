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
}
