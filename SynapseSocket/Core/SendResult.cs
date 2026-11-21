namespace SynapseSocket.Core;

/// <summary>
/// What became of a payload handed to <see cref="SynapseManager.Send"/>.
/// </summary>
public enum SendResult : byte
{
    /// <summary>
    /// Every datagram of the payload was put on the wire before the call returned.
    /// </summary>
    Sent,

    /// <summary>
    /// The send throttle accepted the payload, and at least one of its datagrams waits in the connection's send queue for a
    /// later <see cref="SynapseManager.Poll"/> to release it.
    /// </summary>
    Queued,

    /// <summary>
    /// Nothing of the payload was sent: the connection's send queue was full and
    /// <see cref="Configuration.SendThrottleConfig.QueueFullAction"/> is <see cref="Configuration.SendQueueFullAction.DropNewest"/>, or
    /// the payload was reliable and <see cref="Configuration.ReliableConfig.MaximumPending"/> reliable messages already await
    /// acknowledgement.
    /// </summary>
    Dropped
}
