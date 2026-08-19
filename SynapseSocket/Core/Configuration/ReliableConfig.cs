namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for the reliable delivery channel.
/// </summary>
public sealed class ReliableConfig
{
    // ReSharper disable FieldCanBeMadeReadOnly.Global

    /// <summary>
    /// Maximum number of unacknowledged reliable packets per connection before backpressure is applied.
    /// </summary>
    public uint MaximumPending = 256;

    /// <summary>
    /// Time in milliseconds before an unacknowledged reliable packet is retransmitted.
    /// </summary>
    public uint ResendMilliseconds = 250;

    /// <summary>
    /// Maximum number of retransmission attempts before the connection is terminated.
    /// </summary>
    public uint MaximumRetries = 10;

    /// <summary>
    /// When true, outgoing ACKs are queued and flushed once per poll rather than sent immediately per packet.
    /// Reduces ACK traffic under burst receive conditions at the cost of a delivery delay of one poll.
    /// </summary>
    /// <remarks>The flush cadence is the poll cadence and is not separately configurable. An interval setting and its two clamp constants used to sit here, and none of the three was ever read: the flush has always run unconditionally from the maintenance pass.</remarks>
    public bool AckBatchingEnabled = true;

}
