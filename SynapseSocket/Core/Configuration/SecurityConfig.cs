using SynapseSocket.Security;

namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Security settings for a <see cref="SynapseSocket.Core.SynapseManager"/> instance.
/// Controls rate limiting, replay protection, signature validation, and packet filtering.
/// </summary>
public sealed class SecurityConfig
{
    // ReSharper disable FieldCanBeMadeReadOnly.Global

    /// <summary>
    /// Maximum packets per second allowed per signature.
    /// Set to <see cref="DisabledMaximumPacketsPerSecond"/> (0) to disable packet rate limiting.
    /// </summary>
    public uint MaximumPacketsPerSecond = 500;

    /// <summary>
    /// Maximum received bytes per second allowed per signature.
    /// Paired with <see cref="MaximumPacketsPerSecond"/>: the packet count alone cannot catch
    /// a peer sending near the pps cap at maximum packet size, which would sustain
    /// <c>MaximumPacketsPerSecond * MaximumPacketSize</c> bytes/sec — well above a realistic
    /// realtime-game upstream. Defaults to 2 MiB/s, which allows comfortable legitimate headroom
    /// while cutting off bandwidth floods.
    /// Set to <see cref="DisabledMaximumBytesPerSecond"/> (0) to disable bytes rate limiting.
    /// </summary>
    public uint MaximumBytesPerSecond = 2 * 1024 * 1024;

    /// <summary>
    /// Maximum number of out-of-order reliable packets buffered per connection before raising a violation.
    /// Default is 64. Set to <see cref="DisabledMaximumOutOfOrderReliablePackets"/> (0) to disable.
    /// </summary>
    public uint MaximumOutOfOrderReliablePackets = 64;

    /// <summary>
    /// Maximum reassembled payload size in bytes.
    /// If a segment header declares a segment count such that <c>segmentCount * MaximumTransmissionUnit</c>
    /// exceeds this value, the sender is immediately blacklisted.
    /// Set to <see cref="DisabledMaximumReassembledPacketSize"/> (0) to disable this check.
    /// </summary>
    public uint MaximumReassembledPacketSize = 0;

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
    /// When false, all established-connection security enforcement is disabled: rate limiting,
    /// oversized packet checks, reorder buffer overflow, and segment assembly size checks are
    /// skipped. Pre-connection checks (blacklist, handshake replay, signature validation) remain
    /// active regardless. Defaults to true.
    /// </summary>
    public bool Enabled = true;

    /// <summary>
    /// When true, datagrams whose first byte does not match any known <see cref="SynapseSocket.Packets.PacketType"/>
    /// are passed to the <see cref="SynapseSocket.Core.SynapseManager.UnknownPacketReceived"/> delegate, which returns a
    /// <see cref="SynapseSocket.Security.FilterResult"/> to indicate whether the packet is accepted.
    /// A result other than <see cref="SynapseSocket.Security.FilterResult.Allowed"/> raises a violation.
    /// When false (default), any such datagram immediately raises a violation without invoking the delegate.
    /// Enable this only when an external protocol (e.g. a rendezvous/beacon client) intentionally
    /// piggybacks on the Synapse UDP socket.
    /// </summary>
    public bool AllowUnknownPackets = false;

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumPacketsPerSecond"/> to disable packet rate limiting.
    /// </summary>
    public const uint DisabledMaximumPacketsPerSecond = 0;

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumBytesPerSecond"/> to disable bytes rate limiting.
    /// </summary>
    public const uint DisabledMaximumBytesPerSecond = 0;

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumOutOfOrderReliablePackets"/> to disable the reorder buffer cap.
    /// </summary>
    public const uint DisabledMaximumOutOfOrderReliablePackets = 0;

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumReassembledPacketSize"/> to disable the reassembled packet size check.
    /// </summary>
    public const uint DisabledMaximumReassembledPacketSize = 0;
}
