namespace SynapseBeacon.Wire;

/// <summary>
/// Identifies the type of a SynapseBeacon wire packet.
/// Encoded as the first byte of every packet.
/// <para>
/// All values are &gt; 0x7F so they do not collide with any SynapseSocket <c>PacketType</c>. When a beacon client
/// piggybacks on the Synapse UDP socket, the ingress path routes beacon packets through
/// <c>SynapseManager.UnknownPacketReceived</c>.
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
    CloseSession = 0x88,

    /// <summary>
    /// Rendezvous server challenges a <see cref="JoinSession"/> to prove the joiner can receive at the address it
    /// sent from. Payload is an 8-byte cookie followed by the joiner's echoed nonce.
    /// <para>
    /// Sent before the session is looked up, and sent to every first-hand join whatever the ID names, so the
    /// challenge itself distinguishes nothing. The joiner repeats its <see cref="JoinSession"/> with the cookie
    /// appended, and only that second request can disclose an endpoint or start a hole punch.
    /// </para>
    /// </summary>
    JoinChallenge = 0x89
}
