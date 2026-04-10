namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for NAT traversal. Assign to <see cref="SynapseConfig.NatTraversal"/>.
/// <para>
/// Shared probe settings (<see cref="ProbeCount"/>, <see cref="IntervalMilliseconds"/>,
/// <see cref="MaximumAttempts"/>) apply to both <see cref="NatTraversalMode.FullCone"/> and
/// <see cref="NatTraversalMode.Server"/> modes once an endpoint is known.
/// Mode-exclusive settings live in <see cref="FullCone"/> and <see cref="Server"/> respectively.
/// </para>
/// </summary>
public sealed class NatTraversalConfig
{
    /// <summary>
    /// Which NAT traversal strategy to use.
    /// Defaults to <see cref="NatTraversalMode.Disabled"/>; servers with a public IP need no traversal.
    /// </summary>
    public NatTraversalMode Mode = NatTraversalMode.Disabled;

    /// <summary>
    /// Number of probe packets sent per punch attempt.
    /// Each probe is a minimal packet that opens a mapping in the local NAT table without
    /// initiating a full handshake.
    /// </summary>
    public uint ProbeCount = 3;

    /// <summary>
    /// Milliseconds between successive hole-punch attempts.
    /// </summary>
    public uint IntervalMilliseconds = 200;

    /// <summary>
    /// Maximum number of hole-punch attempts before the connection is declared failed.
    /// </summary>
    public uint MaximumAttempts = 10;

    /// <summary>
    /// Settings exclusive to <see cref="NatTraversalMode.FullCone"/> direct hole-punching.
    /// </summary>
    public FullConeNatConfig FullCone = new();

    /// <summary>
    /// Settings exclusive to <see cref="NatTraversalMode.Server"/> rendezvous-assisted punching.
    /// </summary>
    public ServerNatConfig Server = new();
}