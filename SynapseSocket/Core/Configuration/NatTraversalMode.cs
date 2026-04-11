namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Controls which NAT traversal strategy the engine uses when a direct connection fails.
/// </summary>
public enum NatTraversalMode
{
    /// <summary>
    /// NAT traversal is disabled. Only direct connections are attempted.
    /// Use this for servers with a public endpoint.
    /// </summary>
    Disabled,

    /// <summary>
    /// Full-cone UDP hole punching. Both peers must have this mode enabled and must already know each other's external endpoint.
    /// Works for full-cone and address-restricted-cone NAT.
    /// </summary>
    FullCone,

    /// <summary>
    /// Rendezvous-server-assisted hole punching. A shared <see cref="ServerNatConfig.ServerEndPoint"/> is contacted to exchange external endpoints; hole punching then proceeds as in <see cref="FullCone"/>.
    /// </summary>
    Server
}