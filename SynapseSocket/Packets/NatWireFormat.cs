namespace SynapseSocket.Packets;

/// <summary>
/// Shared wire-format constants for NAT rendezvous packets.
/// Referenced by both the main engine and the standalone NAT server so that
/// the on-wire layout cannot drift between the two projects.
/// </summary>
public static class NatWireFormat
{
    /// <summary>
    /// Byte count required to write a NAT session ID on the wire.
    /// Session IDs are encoded as a 4-byte big-endian unsigned integer.
    /// </summary>
    public const int SessionIdBytes = sizeof(uint);
}
