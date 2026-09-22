using System.Collections.Generic;
using System.Net;
using SynapseSocket.Packets;

namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for a <see cref="SynapseManager"/> instance.
/// All fields have sensible defaults; override only what you need.
/// </summary>
public sealed class SynapseConfig
{
    // ReSharper disable FieldCanBeMadeReadOnly.Global

    /// <summary>
    /// Local endpoints to bind.
    /// At least one must be supplied before calling <see cref="SynapseManager.Start"/>.
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
    /// When a <see cref="PacketTransform"/> is supplied its <see cref="SynapseSocket.Packets.IPacketTransform.ReservedBytes"/>
    /// are deducted from this value, so the engine packs packets against the smaller
    /// <see cref="SynapseManager.MaximumTransmissionUnit"/> and a transformed datagram still fits this size on the wire.
    /// </summary>
    public uint MaximumTransmissionUnit = 1200;

    /// <summary>
    /// Optional layer that rewrites the payload of every Synapse packet on its way to and from the socket, for
    /// encryption, compression, obfuscation, or a custom integrity check.
    /// Leave null (the default) to send and receive packets exactly as the engine builds them.
    /// </summary>
    public IPacketTransform? PacketTransform = null;

    /// <summary>
    /// Maximum number of simultaneous connections the engine will accept.
    /// Handshakes from new peers are rejected with <see cref="Core.Events.ConnectionRejectedReason.ServerFull"/>
    /// when the limit is reached. Set to <see cref="DisabledMaximumConcurrentConnections"/> (0) to disable.
    /// <para>
    /// Defaults to 4096 rather than unlimited. A handshake is unauthenticated, so every datagram bearing an unseen
    /// source endpoint allocates a connection object and its associated tables; with no cap that growth is bounded
    /// only by the attacker's send rate. The cap is the one bound that holds regardless of how much the attacker
    /// varies its source addresses, which is why it (not a per-address rate limit) is the defence here. It is set
    /// well above the engine's tested concurrency so legitimate fan-in, including many peers behind one NAT, is
    /// unaffected.
    /// </para>
    /// <para>
    /// This bounds the damage; it does not prevent it. A complete fix requires proving return-routability before
    /// allocating any state. Responding to a first handshake with a token bound to the source and a time bucket,
    /// and only creating a connection when that token comes back. The NAT challenge path already implements exactly
    /// that pattern.
    /// </para>
    /// </summary>
    public uint MaximumConcurrentConnections = 4096;

    /// <summary>
    /// Maximum datagrams a single <see cref="SynapseManager.Poll"/> will take from each socket before returning.
    /// Defaults to 4096.
    /// <para>
    /// The drain loop otherwise runs until the socket is empty, so traffic arriving faster than the engine processes
    /// it keeps the loop fed and <c>Poll</c> never returns. The host frame loop stalls for as long as the flood
    /// lasts. A budget turns that livelock into ordinary packet loss, which is what the kernel receive buffer is
    /// for; whatever is left stays queued for the next poll.
    /// </para>
    /// <para>
    /// 4096 per socket per poll is roughly 245k datagrams/second at 60 Hz (far above any realistic session) so the
    /// bound only engages under abuse. Set to <see cref="DisabledMaximumReceivesPerPoll"/> (0) to drain without limit.
    /// </para>
    /// </summary>
    public uint MaximumReceivesPerPoll = 4096;

    /// <summary>
    /// Receives datagrams through a direct <c>recvfrom</c> binding on runtimes whose managed socket API cannot
    /// receive from an unspecified sender without allocating. Defaults to true; has no effect on .NET 8+, which
    /// already has an allocation-free overload.
    /// <para>
    /// Measured on Unity Mono 6.13, the managed any-sender receive costs 6 managed allocations per datagram and
    /// the native path costs none. Set to false to stay on the managed API everywhere.
    /// </para>
    /// </summary>
    public bool NativeReceiveEnabled = true;

    /// <summary>
    /// Payload segmentation settings: enable/disable per channel, segment limits, and assembly timeouts.
    /// </summary>
    public SegmentConfig Segment = new();


    /// <summary>
    /// Kernel-level UDP socket receive buffer size (SO_RCVBUF) in bytes, applied on bind.
    /// Defaults to 1 MiB: the OS default (~64 KiB on Windows) is too small to absorb bursty
    /// fan-in from many peers sending segmented payloads concurrently, and undersized buffers
    /// cause silent datagram drops that also let a single noisy peer degrade delivery for
    /// other peers until the rate limiter kicks them. 1 MiB comfortably absorbs hundreds of
    /// concurrent segments and matches what most production realtime-UDP libraries default to.
    /// Set to <see cref="DisabledSocketBufferOverride"/> (0) to leave the OS default untouched.
    /// </summary>
    public int SocketReceiveBufferBytes = 1 * 1024 * 1024;

    /// <summary>
    /// Kernel-level UDP socket send buffer size (SO_SNDBUF) in bytes, applied on bind.
    /// Defaults to <see cref="DisabledSocketBufferOverride"/> (leave the OS default untouched).
    /// Send-side traffic is fan-out from a single process and paced by the app loop, so the
    /// typical OS default (~64 KiB) easily absorbs the small runs of segments a sender emits.
    /// Raise this only if you observe send-side back-pressure under genuine burst workloads.
    /// </summary>
    public int SocketSendBufferBytes = DisabledSocketBufferOverride;

    /// <summary>
    /// When true (default), received payloads are copied into a fresh buffer before being dispatched via <see cref="SynapseManager.PacketReceived"/>.
    /// The copy is recycled after the event returns; do not retain references to <see cref="SynapseSocket.Core.Events.PacketReceivedEventArgs.Payload"/> beyond the handler.
    /// When false, the unreliable fast path dispatches a segment of the ingress engine's own receive buffer directly, which removes the per-packet rental and copy entirely.
    /// The engine keeps that buffer and overwrites it with the next datagram, so the payload is valid only for the duration of the handler and the handler must not poll the engine re-entrantly.
    /// Copy payload data within the handler if it is needed beyond the callback.
    /// </summary>
    /// <remarks>
    /// The default stays true because zero-copy narrows what a handler may safely assume. The delivered segment carries a
    /// non-zero <see cref="System.ArraySegment{T}.Offset"/> into a 64 KiB buffer whose bytes outside
    /// <see cref="System.ArraySegment{T}.Count"/> are the leading packet-type byte and the residue of earlier datagrams, so
    /// a handler that reads <see cref="System.ArraySegment{T}.Array"/> wholesale instead of honouring the offset and count
    /// sees the wrong bytes. Set this to false once the receiving handlers are known to respect both.
    /// Note: reliable and segmented receives always copy internally regardless of this setting.
    /// </remarks>
    public bool CopyReceivedPayloads = true;

    /// <summary>
    /// Enables telemetry counters.
    /// Has a minor performance cost; disable in production if not needed.
    /// </summary>
    public bool EnableTelemetry = false;

    /// <summary>
    /// OS-connects the bound socket to the single remote passed to <see cref="SynapseManager.Connect"/>, so datagrams flow
    /// through the endpoint-free Receive and Send socket calls instead of ReceiveFrom and SendTo.
    /// </summary>
    /// <remarks>
    /// This is the client-mode allocation fix: ReceiveFrom serializes an endpoint and materializes the sender per datagram, and
    /// SendTo re-serializes the target per datagram, on runtimes without the SocketAddress overloads (Unity's Mono) there is no
    /// other way around either cost. A connected socket touches no endpoint at all in steady state, on every runtime.
    /// The OS also filters inbound datagrams to the connected remote, so this mode is only for an engine that talks to exactly
    /// one peer: it cannot host multiple remotes, and it cannot receive the third-party probes full-cone NAT traversal relies on
    /// (<see cref="SynapseManager.Connect"/> rejects the combination). The socket connects to the first
    /// <see cref="SynapseManager.Connect"/> target whose address family matches a bound socket.
    /// </remarks>
    public bool ConnectedSocketEnabled = false;

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
    /// Security settings: rate limiting, replay protection, signature validation, and packet filtering.
    /// </summary>
    public SecurityConfig Security = new();

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumConcurrentConnections"/> to remove the connection cap.
    /// </summary>
    public const uint DisabledMaximumConcurrentConnections = 0;

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumReceivesPerPoll"/> to drain each socket without a per-poll bound.
    /// </summary>
    public const uint DisabledMaximumReceivesPerPoll = 0;

    /// <summary>
    /// Sentinel value: pass as <see cref="SocketReceiveBufferBytes"/> or <see cref="SocketSendBufferBytes"/>
    /// to leave the OS default untouched.
    /// </summary>
    public const int DisabledSocketBufferOverride = 0;
    /// <summary>
    /// Sentinel used internally when a <c>uint</c> limit of 0 (disabled) is converted to an effective no-limit value.
    /// Stored in place of 0 so hot-path checks can use a single comparison without a separate zero guard.
    /// </summary>
    internal const uint EffectiveUnlimitedValueUInt32 = uint.MaxValue;
    /// <summary>
    /// Sentinel used internally when an <c>int</c> limit of 0 (disabled) is converted to an effective no-limit value.
    /// Stored in place of 0 so hot-path checks can use a single comparison without a separate zero guard.
    /// </summary>
    internal const uint EffectiveUnlimitedValueInt32 = int.MaxValue;

    /// <summary>
    /// Converts a configured <c>uint</c> limit into the value stored for runtime checks, replacing 0 (disabled)
    /// with <see cref="EffectiveUnlimitedValueUInt32"/>.
    /// Every call site is constructor or initialisation code, so the conversion is paid once per engine, provider or
    /// connection; each later check is then a single comparison against the stored limit with no separate zero guard.
    /// </summary>
    /// <param name="configuredLimit">The configured limit, where 0 means unlimited.</param>
    /// <returns><paramref name="configuredLimit"/>, or <see cref="EffectiveUnlimitedValueUInt32"/> when it is 0.</returns>
    internal static uint ToEffectiveLimit(uint configuredLimit) => configuredLimit == 0 ? EffectiveUnlimitedValueUInt32 : configuredLimit;
}
