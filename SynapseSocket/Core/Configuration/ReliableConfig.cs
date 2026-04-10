namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for the reliable delivery channel.
/// </summary>
public sealed class ReliableConfig
{
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
}
