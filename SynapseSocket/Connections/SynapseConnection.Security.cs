using System;
using CodeBoost.CodeAnalysis;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Connections;

/// <summary>
/// Represents the state of a single remote peer session, including reliable send/receive windows, keep-alive timestamps, and signature binding.
/// </summary>
public sealed partial class SynapseConnection
{

    /// <summary>
    /// Number of packets received by this connection since <see cref="_receivedPacketCountResetTick"/> was last set.
    /// Incremented on every inbound packet and cleared by <see cref="ResetReceivedByPacketCount"/>.
    /// </summary>
    [PoolResettableMember]
    private uint _receivedByPacketCount;
    /// <summary>
    /// UTC ticks of the last time <see cref="_receivedByPacketCount"/> was reset to zero.
    /// Used to enforce the one-second window for per-connection rate limiting.
    /// </summary>
    [PoolResettableMember]
    private long _receivedPacketCountResetTick;

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
    /// Resets <see cref="_receivedByPacketCount"/> to zero once per second.
    /// Has no effect if called within the same one-second window as the previous reset.
    /// </summary>
    /// <param name="nowTicks">Current UTC ticks, used to determine whether the one-second window has elapsed.</param>
    internal void ResetReceivedByPacketCount(long nowTicks)
    {
        if (nowTicks - _receivedPacketCountResetTick < TimeSpan.TicksPerSecond)
            return;

        _receivedPacketCountResetTick = nowTicks;
        _receivedByPacketCount = 0;
    }
}