namespace SynapseSocket.Packets;

/// <summary>
/// Identifies which way a datagram is travelling when it is handed to <see cref="IPacketTransform"/>.
/// </summary>
public enum PacketTransformDirection : byte
{
    /// <summary>
    /// The packet is leaving the engine and is about to be written to the socket.
    /// </summary>
    Outbound = 0,

    /// <summary>
    /// The packet has arrived from the socket and has not yet been parsed.
    /// </summary>
    Inbound = 1
}
