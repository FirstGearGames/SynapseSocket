namespace SynapseSocket.Core.Events;

/// <summary>
/// Reasons an already-established connection committed an offense after being connected.
/// Surfaced on the <c>ViolationDetected</c> event via <see cref="ViolationEventArgs"/>.
/// </summary>
public enum ViolationReason
{
    /// <summary>
    /// Connection timed out (no traffic received within the configured window).
    /// </summary>
    Timeout,

    /// <summary>
    /// Peer requested disconnection.
    /// </summary>
    PeerDisconnect,

    /// <summary>
    /// Reliable retransmission exhausted the retry budget.
    /// </summary>
    ReliableExhausted,

    /// <summary>
    /// Incoming packet exceeded the configured maximum size.
    /// </summary>
    Oversized,

    /// <summary>
    /// Peer exceeded the per-second packet rate limit.
    /// </summary>
    RateLimitExceeded,

    /// <summary>
    /// Received a malformed or unparseable packet header.
    /// </summary>
    Malformed
}
