namespace SynapseBeacon.Wire;

/// <summary>
/// Identifies the type of a SynapseBeacon wire packet.
/// Encoded as the first byte of every packet.
/// <para>
/// All values are &gt; 0x7F so they do not collide with any SynapseSocket <c>PacketType</c> —
/// when a beacon client piggybacks on the Synapse UDP socket, the ingress path routes beacon
/// packets through <c>SynapseManager.UnknownPacketReceived</c>.
/// </para>
/// </summary>
public enum BeaconPacketType : byte
{
    /// <summary>
    /// Client requests the rendezvous server to create and assign a new session ID.
    /// No payload.
    /// </summary>
    RequestSession = 0x80,

    /// <summary>
    /// Rendezvous server responds with the newly created session ID.
    /// Payload is a 4-byte big-endian session ID.
    /// </summary>
    SessionCreated = 0x81,

    /// <summary>
    /// Rendezvous server rejects a session-creation request because its concurrent session cap
    /// has been reached. No payload.
    /// </summary>
    ServerAtCapacity = 0x82,

    /// <summary>
    /// Sent by a joiner to register with the rendezvous server against an existing session ID.
    /// Payload is a 4-byte big-endian session ID.
    /// </summary>
    JoinSession = 0x83,

    /// <summary>
    /// Rendezvous server reports the matched peer's external endpoint.
    /// Payload: address family byte + IP bytes + port (2 bytes LE).
    /// </summary>
    PeerReady = 0x84,

    /// <summary>
    /// Rendezvous server rejects a registration because the session ID was not found or has expired.
    /// No payload.
    /// </summary>
    SessionNotFound = 0x85,

    /// <summary>
    /// Keep-alive heartbeat sent to a rendezvous server.
    /// Payload is a 4-byte big-endian session ID.
    /// </summary>
    Heartbeat = 0x86,

    /// <summary>
    /// Rendezvous server acknowledges a heartbeat.
    /// No payload.
    /// </summary>
    HeartbeatAck = 0x87,

    /// <summary>
    /// Host requests the rendezvous server to close a session and stop accepting new joiners.
    /// Payload is a 4-byte big-endian session ID.
    /// </summary>
    CloseSession = 0x88
}
