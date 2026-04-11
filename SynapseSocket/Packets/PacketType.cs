namespace SynapseSocket.Packets;

/// <summary>
/// Identifies the type of a Synapse wire packet.
/// Encoded as the first byte of every packet header.
/// Optional header fields follow based on type:
/// <list type="bullet">
/// <item><see cref="Reliable"/>, <see cref="Ack"/>, <see cref="ReliableSegmented"/> — sequence number (2 bytes LE).</item>
/// <item><see cref="Segmented"/> — segment ID (2 bytes LE), segment index (1 byte), segment count (1 byte).</item>
/// <item><see cref="ReliableSegmented"/> — sequence number, then segment fields.</item>
/// </list>
/// All other types carry no additional header fields; any further bytes are payload.
/// </summary>
public enum PacketType : byte
{
    /// <summary>Unreliable, unsegmented data payload.</summary>
    None = 0,

    /// <summary>Reliable, unsegmented data payload. Header includes a sequence number.</summary>
    Reliable = 1,

    /// <summary>Acknowledgment for a reliable packet. Header includes the acknowledged sequence number.</summary>
    Ack = 2,

    /// <summary>Handshake or handshake acknowledgment. Payload contains a 4-byte nonce.</summary>
    Handshake = 3,

    /// <summary>Keep-alive heartbeat. No payload.</summary>
    KeepAlive = 4,

    /// <summary>Graceful disconnect notification. No payload.</summary>
    Disconnect = 5,

    /// <summary>Unreliable, segmented data payload. Header includes segment fields.</summary>
    Segmented = 6,

    /// <summary>Reliable, segmented data payload. Header includes a sequence number then segment fields.</summary>
    ReliableSegmented = 7,

    /// <summary>NAT punch probe. Sent to open a NAT table mapping. No payload.</summary>
    NatProbe = 8,

    /// <summary>NAT challenge or challenge echo. Payload is an 8-byte HMAC token.</summary>
    NatChallenge = 9,

    /// <summary>Registers with a NAT rendezvous server. Payload is the ASCII session ID.</summary>
    NatRegister = 10,

    /// <summary>Keep-alive heartbeat to a NAT rendezvous server. Payload is the ASCII session ID.</summary>
    NatHeartbeat = 11,

    /// <summary>Rendezvous server acknowledges a heartbeat. No payload.</summary>
    NatHeartbeatAck = 12,

    /// <summary>Rendezvous server reports the peer's external endpoint. Payload: address family byte + IP bytes + port (2 bytes LE).</summary>
    NatPeerReady = 13,

    /// <summary>Rendezvous server rejects a registration because the session is full or was not found. No payload.</summary>
    NatSessionFull = 14,

    /// <summary>Client requests the rendezvous server to create and assign a new session ID. No payload.</summary>
    NatRequestSession = 15,

    /// <summary>Rendezvous server responds with the newly created session ID. Payload is the ASCII session ID.</summary>
    NatSessionCreated = 16,

    /// <summary>Host requests the rendezvous server to close a session and stop accepting new joiners. Payload is the ASCII session ID.</summary>
    NatCloseSession = 17,

    /// <summary>Rendezvous server rejects a session-creation request because its concurrent session limit has been reached. No payload.</summary>
    NatSessionUnavailable = 18,
}