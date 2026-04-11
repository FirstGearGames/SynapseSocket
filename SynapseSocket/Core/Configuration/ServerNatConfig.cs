using System.Net;

namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Settings specific to <see cref="NatTraversalMode.Server"/> rendezvous-assisted hole punching.
/// The host calls <see cref="SynapseSocket.Core.SynapseManager.HostViaNatServerAsync"/> to obtain a
/// server-assigned session ID, then shares it out-of-band with the peer.
/// The peer calls <see cref="SynapseSocket.Core.SynapseManager.JoinViaNatServerAsync"/> with that ID.
/// </summary>
public sealed class ServerNatConfig
{
    /// <summary>
    /// Endpoint of the publicly reachable NAT rendezvous server.
    /// Must be set before calling <see cref="SynapseSocket.Core.SynapseManager.HostViaNatServerAsync"/>
    /// or <see cref="SynapseSocket.Core.SynapseManager.JoinViaNatServerAsync"/>.
    /// </summary>
    public IPEndPoint? ServerEndPoint;

    /// <summary>
    /// Milliseconds to wait for the peer to register before the attempt is abandoned.
    /// </summary>
    public uint RegistrationTimeoutMilliseconds = 15000;

    /// <summary>
    /// Milliseconds between heartbeat packets sent to the rendezvous server while waiting for a peer.
    /// </summary>
    public uint HeartbeatIntervalMilliseconds = 3000;

    /// <summary>
    /// Length of a session ID in characters.
    /// </summary>
    public const int SessionIdLength = 6;
}
