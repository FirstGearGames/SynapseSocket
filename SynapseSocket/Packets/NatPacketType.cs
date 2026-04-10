namespace SynapseSocket.Packets;

/// <summary>
/// Sub-type byte carried in the payload of an <see cref="PacketFlags.Extended"/> packet
/// exchanged between an engine and a NAT rendezvous server.
/// The first byte after the <see cref="PacketFlags"/> header identifies the operation.
/// </summary>
public enum NatPacketType : byte
{
    /// <summary>
    /// Client registers for a session. Payload: 16-byte session <see cref="System.Guid"/>.
    /// </summary>
    Register = 1,

    /// <summary>
    /// Server notifies a client that its peer has registered.
    /// Payload: <c>[addr_family: 1 byte (4=IPv4 / 6=IPv6)] [IP: 4 or 16 bytes] [port: 2 bytes LE]</c>.
    /// </summary>
    PeerReady = 2,

    /// <summary>
    /// Client heartbeat to keep its registration alive. Payload: 16-byte session <see cref="System.Guid"/>.
    /// </summary>
    Heartbeat = 3,

    /// <summary>
    /// Server acknowledges a heartbeat. No payload.
    /// </summary>
    HeartbeatAck = 4,

    /// <summary>
    /// Server rejects a registration because the session already has two peers.
    /// No payload.
    /// </summary>
    SessionFull = 5
}