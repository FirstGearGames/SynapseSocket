namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for payload segmentation on both the reliable and unreliable channels.
/// </summary>
public sealed class SegmentConfig
{
    /// <summary>
    /// Enables reliable segmentation: payloads that exceed the MTU are automatically split into
    /// reliable segments and reassembled on the receiver. Set to false to disable; oversized
    /// reliable sends will throw.
    /// </summary>
    public bool ReliableEnabled = true;

    /// <summary>
    /// Controls how unreliable payloads that exceed the MTU are handled.
    /// Defaults to <see cref="UnreliableSegmentMode.SegmentUnreliable"/>.
    /// </summary>
    public UnreliableSegmentMode UnreliableMode = UnreliableSegmentMode.SegmentUnreliable;

    /// <summary>
    /// Maximum number of segments a single message may be split into on the receive side.
    /// Set to 0 for no limit (allows up to the protocol maximum of 255 segments).
    /// Payloads that declare more segments than this limit are treated as a protocol violation.
    /// </summary>
    public uint MaximumSegments = 0;

    /// <summary>
    /// Maximum number of simultaneous in-progress segment assemblies per connection.
    /// Excess assemblies are treated as a protocol violation. Set to 0 to disable the limit.
    /// Default is 16.
    /// </summary>
    public uint MaximumConcurrentAssembliesPerConnection = 16;

    /// <summary>
    /// How long (in milliseconds) an incomplete segment assembly is kept before being evicted.
    /// Set to <see cref="DisabledAssemblyTimeout"/> (0) to disable eviction.
    /// </summary>
    public uint AssemblyTimeoutMilliseconds = 5000;

    /// <summary>
    /// Sentinel value: pass as <see cref="AssemblyTimeoutMilliseconds"/> to disable assembly timeout eviction.
    /// </summary>
    public const uint DisabledAssemblyTimeout = 0;
}
