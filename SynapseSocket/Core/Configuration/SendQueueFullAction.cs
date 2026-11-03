namespace SynapseSocket.Core.Configuration;

/// <summary>
/// What a throttled send does when its datagram would take the connection's send queue past
/// <see cref="SendThrottleConfig.MaximumQueuedBytes"/>.
/// </summary>
public enum SendQueueFullAction : byte
{
    /// <summary>
    /// The oldest queued datagrams are sent immediately, without waiting for their release, until the new datagram fits, and the
    /// new datagram is then queued. Nothing is lost, at the cost of a burst the throttle would otherwise have spread out.
    /// </summary>
    SendOldest,

    /// <summary>
    /// The new payload is not sent at all, and the send reports <see cref="SendResult.Dropped"/>. A reliable payload is refused
    /// before it takes a sequence number, so the reliable channel is not left waiting for it.
    /// </summary>
    DropNewest
}
