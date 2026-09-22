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
    /// <para>
    /// Calibrated against <see cref="MaximumBytesPerSecond"/>: at the default 1200-byte MTU, 2 MiB/s needs roughly
    /// 1750 packets/second, so a lower packet cap would make the byte allowance unreachable and silently become the
    /// real bandwidth ceiling. At 2000 the byte cap binds first for MTU-sized traffic and the packet cap binds first
    /// for floods of small packets, which is the division of labour the two are meant to have. Raise both together
    /// if you raise the MTU.
    /// </para>
    /// </summary>
    public uint MaximumPacketsPerSecond = 2000;

    /// <summary>
    /// Maximum received bytes per second allowed per signature.
    /// Paired with <see cref="MaximumPacketsPerSecond"/>: the packet count alone cannot catch
    /// a peer sending near the pps cap at maximum packet size, which would sustain
    /// <c>MaximumPacketsPerSecond * MaximumPacketSize</c> bytes/sec, well above a realistic
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
    /// Live connection count at or above which a handshake from an unknown endpoint must prove return-routability
    /// before any connection state is allocated for it. Defaults to 1024.
    /// <para>
    /// Below the threshold, connecting costs exactly one round trip, as it always has. At or above it, the engine
    /// answers an unknown peer's handshake with a stateless HMAC token bound to that peer's address and a time
    /// bucket, and allocates nothing until the token comes back. A spoofed source never receives the challenge, so
    /// it can never consume a connection slot, which is the cost that matters, because a full table rejects
    /// legitimate peers no matter how efficiently the connection objects themselves are pooled.
    /// </para>
    /// <para>
    /// The gate is deliberately occupancy-triggered rather than always-on: normal operation pays nothing, and
    /// legitimate clients only ever pay the extra round trip while the engine is actually under pressure. Set to
    /// <see cref="DisabledHandshakeChallengeThreshold"/> (0) to never challenge.
    /// </para>
    /// </summary>
    public uint HandshakeChallengeThreshold = 1024;

    /// <summary>
    /// Number of violations a signature must accumulate within <see cref="ViolationWindowMilliseconds"/> before it is
    /// blacklisted. Defaults to 5.
    /// <para>
    /// A value of 1 restores the old behaviour, where a single datagram was sufficient. That is unsafe over UDP: the
    /// source address is trivially forged, so one spoofed oversized or malformed datagram bearing a victim's address
    /// would ban that victim. Kicking still happens on the first violation; only the ban is deferred.
    /// </para>
    /// </summary>
    public uint ViolationsBeforeBlacklist = 5;

    /// <summary>
    /// Sliding window in milliseconds over which <see cref="ViolationsBeforeBlacklist"/> is counted. Defaults to 10 seconds.
    /// </summary>
    public uint ViolationWindowMilliseconds = 10_000;

    /// <summary>
    /// How long a blacklist entry remains in force, in milliseconds. Defaults to 5 minutes.
    /// Set to <see cref="PermanentBlacklistDuration"/> (0) for entries that never expire.
    /// <para>
    /// An unbounded, never-expiring blacklist is both a permanent lockout for any peer that trips it once and a
    /// remotely-driven memory leak, since a new entry can be minted per source address.
    /// </para>
    /// </summary>
    public uint BlacklistDurationMilliseconds = 300_000;

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

    /// <summary>
    /// Sentinel value: pass as <see cref="BlacklistDurationMilliseconds"/> to make blacklist entries permanent.
    /// </summary>
    public const uint PermanentBlacklistDuration = 0;

    /// <summary>
    /// Sentinel value: pass as <see cref="HandshakeChallengeThreshold"/> to never require a return-routability proof.
    /// </summary>
    public const uint DisabledHandshakeChallengeThreshold = 0;
}
