namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for connection lifecycle: keep-alive heartbeats and timeout detection.
/// </summary>
public sealed class ConnectionConfig
{
    /// <summary>
    /// Interval between keep-alive heartbeats in milliseconds.
    /// </summary>
    public uint KeepAliveIntervalMilliseconds = 5000;

    /// <summary>
    /// Time in milliseconds after which an idle connection is considered timed out.
    /// </summary>
    public uint TimeoutMilliseconds = 15000;
}
