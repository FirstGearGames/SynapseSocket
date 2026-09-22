namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for connection lifecycle: keep-alive heartbeats and timeout detection.
/// </summary>
public sealed class ConnectionConfig
{
    /// <summary>
    /// Interval between keep-alive heartbeats in milliseconds.
    /// </summary>
    public uint KeepAliveIntervalMilliseconds = 5000;

    /// <summary>
    /// Time in milliseconds after which an idle connection is considered timed out.
    /// </summary>
    public uint TimeoutMilliseconds = 15000;

    /// <summary>
    /// Time in milliseconds a connection may spend waiting on its handshake before it is considered timed out.
    /// The wait is measured from when the handshake began, not from the last packet received, so traffic from a peer
    /// that already believes itself connected cannot hold the handshake open.
    /// <see cref="UnsetHandshakeTimeoutMilliseconds"/> falls back to <see cref="TimeoutMilliseconds"/>.
    /// </summary>
    /// <remarks>
    /// A connection waits on its handshake while it is <see cref="SynapseSocket.Connections.ConnectionState.Pending"/>,
    /// which in practice is an outgoing <see cref="SynapseSocket.Core.SynapseManager.Connect"/> the remote has not yet
    /// answered. An inbound handshake is answered and promoted to
    /// <see cref="SynapseSocket.Connections.ConnectionState.Connected"/> in the same pass that registers it, so a
    /// server never holds a connection in this state. A handshake that times out raises the same
    /// <see cref="SynapseSocket.Core.SynapseManager.ConnectionClosed"/> and
    /// <see cref="SynapseSocket.Core.Events.ViolationReason.Timeout"/> violation as an idle timeout.
    /// <para>
    /// Under <see cref="NatTraversalMode.FullCone"/> a set value must cover the whole hole-punch schedule, which is
    /// <see cref="FullConeNatConfig.DirectAttemptMilliseconds"/> plus <see cref="NatTraversalConfig.MaximumAttempts"/>
    /// intervals of <see cref="NatTraversalConfig.IntervalMilliseconds"/>, or the <see cref="SynapseSocket.Core.SynapseManager"/>
    /// constructor rejects it. A shorter timeout would end the connection mid-punch, before the punch could report
    /// <see cref="SynapseSocket.Core.Events.ConnectionRejectedReason.NatTraversalFailed"/>.
    /// </para>
    /// </remarks>
    public uint HandshakeTimeoutMilliseconds = UnsetHandshakeTimeoutMilliseconds;

    /// <summary>
    /// Sentinel value: pass as <see cref="HandshakeTimeoutMilliseconds"/> to hold a pending handshake to
    /// <see cref="TimeoutMilliseconds"/>.
    /// </summary>
    public const uint UnsetHandshakeTimeoutMilliseconds = 0;
}
