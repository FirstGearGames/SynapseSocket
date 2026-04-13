using System.Collections.Generic;
using System.Net;
using SynapseSocket.Diagnostics;
using SynapseSocket.Security;

namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for a <see cref="SynapseManager"/> instance.
/// All fields have sensible defaults; override only what you need.
/// </summary>
public sealed class SynapseConfig
{
    /// <summary>
    /// Local endpoints to bind.
    /// At least one must be supplied before calling <see cref="SynapseManager.StartAsync"/>.
    /// </summary>
    public List<IPEndPoint> BindEndPoints = [];

    /// <summary>
    /// Maximum datagram size the engine will accept or send.
    /// Packets larger than this value are treated as oversized and trigger a violation.
    /// </summary>
    public uint MaximumPacketSize = 1400;

    /// <summary>
    /// Maximum transmission unit used for segmentation.
    /// Should be less than or equal to <see cref="MaximumPacketSize"/>.
    /// </summary>
    public uint MaximumTransmissionUnit = 1200;

    /// <summary>
    /// Maximum packets per second allowed per signature.
    /// Set to <see cref="UnsetMaximumPacketsPerSecond"/> (0) to disable packet rate limiting.
    /// </summary>
    public uint MaximumPacketsPerSecond = 2000;

    /// <summary>
    /// Maximum bytes per second allowed per signature.
    /// Set to <see cref="UnsetMaximumBytesPerSecond"/> (0) to disable byte rate limiting.
    /// </summary>
    public uint MaximumBytesPerSecond = 0;

    /// <summary>
    /// Maximum number of segments a segmented payload may be split into.
    /// Set to <see cref="DisabledMaximumSegments"/> to disable this feature.
    /// </summary>
    public uint MaximumSegments = DisabledMaximumSegments;

    /// <summary>
    /// Maximum number of concurrent incomplete segment assemblies per connection.
    /// Once reached, further segmented packets from that connection are treated as a protocol violation.
    /// Default is 16.
    /// </summary>
    public uint MaximumConcurrentSegmentAssembliesPerConnection = 16;

    /// <summary>
    /// Maximum number of simultaneous connections the engine will accept.
    /// Handshakes from new peers are rejected with <see cref="Core.Events.ConnectionRejectedReason.ServerFull"/>
    /// when the limit is reached. Set to 0 to disable.
    /// </summary>
    public uint MaximumConcurrentConnections = 0;

    /// <summary>
    /// Maximum number of out-of-order reliable packets buffered per connection before raising a violation.
    /// Default is 64. Set to 0 to disable.
    /// </summary>
    public uint MaximumOutOfOrderReliablePackets = 64;

    /// <summary>
    /// Controls how the engine handles unreliable payloads that exceed the MTU.
    /// Defaults to <see cref="UnreliableSegmentMode.SegmentUnreliable"/>: oversized sends are split into unreliable segments.
    /// </summary>
    public UnreliableSegmentMode UnreliableSegmentMode = UnreliableSegmentMode.SegmentUnreliable;

    /// <summary>
    /// How long (in milliseconds) an incomplete segment assembly is kept before being evicted.
    /// Set to 0 to disable eviction.
    /// </summary>
    public uint SegmentAssemblyTimeoutMilliseconds = 5000;

    /// <summary>
    /// Maximum reassembled payload size in bytes.
    /// If a segment header declares a segment count such that <c>segmentCount * MaximumTransmissionUnit</c>
    /// exceeds this value, the sender is immediately blacklisted.
    /// Set to 0 to disable this check.
    /// </summary>
    public uint MaximumReassembledPacketSize = 0;

    /// <summary>
    /// When true (default), received payloads are copied into a fresh buffer before being dispatched via <see cref="SynapseManager.PacketReceived"/>.
    /// The copy is recycled after the event returns; do not retain references to <see cref="SynapseSocket.Core.Events.PacketReceivedEventArgs.Payload"/> beyond the handler.
    /// When false, the internal payload buffer is dispatched directly and recycled immediately after the event returns.
    /// Copy payload data within the handler if it is needed beyond the callback.
    /// Note: reliable and segmented receives always copy internally regardless of this setting.
    /// </summary>
    public bool CopyReceivedPayloads = false;

    /// <summary>
    /// Enables telemetry counters.
    /// Has a minor performance cost; disable in production if not needed.
    /// </summary>
    public bool EnableTelemetry = false;

    /// <summary>
    /// Optional custom signature provider.
    /// Defaults to <see cref="DefaultSignatureProvider"/> when null.
    /// </summary>
    public ISignatureProvider? SignatureProvider;

    /// <summary>
    /// Optional signature validator applied during handshake.
    /// When null, all valid signatures are accepted.
    /// </summary>
    public ISignatureValidator? SignatureValidator;

    /// <summary>
    /// Connection lifecycle settings: keep-alive interval, timeout, and sweep window.
    /// </summary>
    public ConnectionConfig Connection = new();

    /// <summary>
    /// Reliable delivery channel settings: pending queue limit, resend interval, and retry cap.
    /// </summary>
    public ReliableConfig Reliable = new();

    /// <summary>
    /// Latency simulation settings.
    /// Disabled by default.
    /// </summary>
    public LatencySimulatorConfig LatencySimulator = new();

    /// <summary>
    /// NAT traversal (hole punching) settings.
    /// Disabled by default; set <see cref="NatTraversalConfig.Mode"/> to enable.
    /// Has no effect when connecting to a server with a public IP (no NAT traversal required).
    /// </summary>
    public NatTraversalConfig NatTraversal = new();

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumSegments"/> to disable segmentation.
    /// </summary>
    public const uint DisabledMaximumSegments = 0;

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumPacketsPerSecond"/> to disable packet rate limiting.
    /// </summary>
    public const uint UnsetMaximumPacketsPerSecond = 0;

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumBytesPerSecond"/> to disable byte rate limiting.
    /// </summary>
    public const uint UnsetMaximumBytesPerSecond = 0;

    /// <summary>
    /// Sentinel value: pass as <see cref="SegmentAssemblyTimeoutMilliseconds"/> to disable assembly timeout.
    /// </summary>
    public const uint UnsetSegmentAssemblyTimeout = 0;
}