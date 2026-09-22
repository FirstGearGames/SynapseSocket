using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Security;

/// <summary>
/// Handles signature calculation, verification, and blacklisting for the SynapseSocket engine.
/// Also enforces lowest-level mitigation rules such as per-endpoint packet frequency limits.
/// </summary>
public sealed class SecurityProvider
{
    /// <summary>
    /// The signature calculator in use.
    /// </summary>
    public ISignatureProvider SignatureProvider { get; }
    /// <summary>
    /// Blacklisted peer signatures mapped to the UTC tick at which the entry expires, or
    /// <see cref="PermanentExpiryTicks"/> for entries that never expire.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, long> _blacklist = [];
    /// <summary>
    /// Violation tallies per signature, used to defer blacklisting until a peer has misbehaved repeatedly.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, ViolationRecord> _violations = [];
    /// <summary>
    /// Violations a signature must accumulate inside <see cref="_violationWindowTicks"/> before it is blacklisted.
    /// </summary>
    private readonly uint _violationsBeforeBlacklist;
    /// <summary>
    /// Sliding window in ticks over which <see cref="_violationsBeforeBlacklist"/> is counted.
    /// </summary>
    private readonly long _violationWindowTicks;
    /// <summary>
    /// Lifetime in ticks applied to blacklist entries added by <see cref="RegisterViolation"/>, or
    /// <see cref="PermanentExpiryTicks"/> when entries should never expire.
    /// </summary>
    private readonly long _blacklistDurationTicks;
    /// <summary>
    /// Effective per-peer packet rate cap: <see cref="SecurityConfig.MaximumPacketsPerSecond"/> converted from
    /// 0 (disabled) to <see cref="SynapseConfig.EffectiveUnlimitedValueUInt32"/> so the hot-path check is a single
    /// comparison with no zero guard.
    /// </summary>
    private readonly uint _effectiveMaximumPacketsPerSecond;
    /// <summary>
    /// Effective per-peer byte rate cap: <see cref="SecurityConfig.MaximumBytesPerSecond"/> converted from
    /// 0 (disabled) to <see cref="SynapseConfig.EffectiveUnlimitedValueUInt32"/> so the hot-path check is a single
    /// comparison with no zero guard.
    /// </summary>
    private readonly uint _effectiveMaximumBytesPerSecond;
    /// <summary>
    /// Maximum permitted size of a single incoming packet in bytes. Packets exceeding this are rejected.
    /// </summary>
    private readonly uint _maximumPacketSize;
    /// <summary>
    /// When false, all established-connection enforcement is bypassed in <see cref="InspectEstablished"/>.
    /// </summary>
    private readonly bool _isEnabled;
    /// <summary>
    /// Ceiling for <see cref="_violations"/>, so a flood of distinct signatures cannot grow it without bound.
    /// </summary>
    private const int MaximumViolationRecords = 8192;
    /// <summary>
    /// Ceiling for <see cref="_blacklist"/>. A TTL alone does not bound the table: an attacker minting distinct
    /// signatures faster than they expire still grows it to (rate x TTL) entries, and the default signature includes
    /// the source port, so one address can mint 65,535 of them. When the ceiling is reached, lapsed entries are
    /// swept first and the table is only cleared if that recovers nothing.
    /// </summary>
    private const int MaximumBlacklistEntries = 16384;
    /// <summary>
    /// Expiry sentinel marking a blacklist entry that never lapses.
    /// </summary>
    private const long PermanentExpiryTicks = long.MaxValue;
    /// <summary>
    /// The sentinel value returned when a signature cannot be computed.
    /// The engine never blacklists this value.
    /// </summary>
    public const ulong UnsetSignature = 0;

    /// <summary>
    /// Creates a new security provider.
    /// </summary>
    /// <param name="signatureProvider">The signature provider used to identify remote peers.</param>
    /// <param name="maximumPacketsPerSecond">Per-peer packet rate limit. Zero disables packet rate limiting.</param>
    /// <param name="maximumBytesPerSecond">Per-peer byte rate limit. Zero disables byte rate limiting.</param>
    /// <param name="maximumPacketSize">Maximum permitted packet size in bytes.</param>
    /// <param name="isEnabled">When false, <see cref="InspectEstablished"/> skips all enforcement and returns <see cref="FilterResult.Allowed"/>.</param>
    /// <param name="violationsBeforeBlacklist">Violations required inside <paramref name="violationWindowMilliseconds"/> before a signature is blacklisted. Values below 1 are treated as 1.</param>
    /// <param name="violationWindowMilliseconds">Sliding window over which violations are counted.</param>
    /// <param name="blacklistDurationMilliseconds">Lifetime of blacklist entries; 0 makes them permanent.</param>
    public SecurityProvider(ISignatureProvider signatureProvider, uint maximumPacketsPerSecond, uint maximumBytesPerSecond, uint maximumPacketSize, bool isEnabled,
        uint violationsBeforeBlacklist = 1, uint violationWindowMilliseconds = 10_000, uint blacklistDurationMilliseconds = 0)
    {
        SignatureProvider = signatureProvider ?? throw new ArgumentNullException(nameof(signatureProvider));
        _effectiveMaximumPacketsPerSecond = SynapseConfig.ToEffectiveLimit(maximumPacketsPerSecond);
        _effectiveMaximumBytesPerSecond = SynapseConfig.ToEffectiveLimit(maximumBytesPerSecond);
        _maximumPacketSize = maximumPacketSize;
        _isEnabled = isEnabled;
        _violationsBeforeBlacklist = violationsBeforeBlacklist < 1 ? 1 : violationsBeforeBlacklist;
        _violationWindowTicks = violationWindowMilliseconds * TimeSpan.TicksPerMillisecond;
        _blacklistDurationTicks = blacklistDurationMilliseconds == 0
            ? PermanentExpiryTicks
            : blacklistDurationMilliseconds * TimeSpan.TicksPerMillisecond;
    }

    /// <summary>
    /// Computes the signature for an endpoint.
    /// Returns <see cref="UnsetSignature"/> if the provider reports failure.
    /// </summary>
    /// <param name="endPoint">The remote peer's endpoint.</param>
    /// <param name="handshakePayload">The handshake payload bytes, or empty if not yet available.</param>
    /// <returns>The computed 64-bit signature, or <see cref="UnsetSignature"/> on failure.</returns>
    public ulong ComputeSignature(IPEndPoint endPoint, ReadOnlySpan<byte> handshakePayload)
    {
        if (!SignatureProvider.TryCompute(endPoint, handshakePayload, out ulong signature))
            return UnsetSignature;

        return signature;
    }

    /// <summary>
    /// Returns true if the given signature is blacklisted.
    /// </summary>
    /// <param name="signature">The peer signature to check.</param>
    /// <returns>True if the signature is present in the blacklist.</returns>
    public bool IsBlacklisted(ulong signature)
    {
        if (!_blacklist.TryGetValue(signature, out long expiresAtTicks))
            return false;

        if (expiresAtTicks is PermanentExpiryTicks || Clock.Ticks < expiresAtTicks)
            return true;

        // Lapsed. Drop it on read so the table drains without a dedicated sweep.
        _blacklist.TryRemove(signature, out _);

        return false;
    }

    /// <summary>
    /// Adds a signature to the blacklist permanently. Prefer <see cref="RegisterViolation"/> for entries driven by
    /// observed traffic; this overload is for callers that have out-of-band grounds to ban a peer outright.
    /// </summary>
    /// <param name="signature">The peer signature to blacklist.</param>
    public void AddToBlacklist(ulong signature) => _blacklist[signature] = PermanentExpiryTicks;

    /// <summary>
    /// Records one violation against <paramref name="signature"/> and blacklists it once the configured threshold is
    /// reached inside the configured window.
    /// <para>
    /// Deferring the ban is what stops a single forged datagram from banning an arbitrary endpoint: over UDP the
    /// source address is attacker-chosen, so one packet is not evidence. The caller still kicks immediately.
    /// </para>
    /// </summary>
    /// <param name="signature">The peer signature that violated.</param>
    /// <returns>True when this violation pushed the signature over the threshold and it is now blacklisted.</returns>
    public bool RegisterViolation(ulong signature)
    {
        if (signature == UnsetSignature)
            return false;

        long nowTicks = Clock.Ticks;

        // Bounded: a flood of distinct signatures must not grow the tally table without limit.
        if (_violations.Count >= MaximumViolationRecords && !_violations.ContainsKey(signature))
            _violations.Clear();

        ViolationRecord violationRecord = _violations.GetOrAdd(signature, static _ => new ViolationRecord());

        if (nowTicks - violationRecord.WindowStartTicks >= _violationWindowTicks)
        {
            violationRecord.WindowStartTicks = nowTicks;
            violationRecord.Count = 0;
        }

        violationRecord.Count++;

        if (violationRecord.Count < _violationsBeforeBlacklist)
            return false;

        _violations.TryRemove(signature, out _);
        AddToBlacklistBounded(signature, nowTicks);

        return true;
    }

    /// <summary>
    /// Removes a signature from the blacklist.
    /// Returns true if the signature was present and removed.
    /// </summary>
    /// <param name="signature">The peer signature to remove.</param>
    /// <returns>True if the signature was present and has been removed; false if it was not found.</returns>
    public bool RemoveFromBlacklist(ulong signature) => _blacklist.TryRemove(signature, out _);

    /// <summary>
    /// Applies the per-peer packet and byte rate limits to a datagram from an already-established connection.
    /// Returns <see cref="FilterResult.Allowed"/> unchanged when security is disabled.
    /// </summary>
    /// <param name="synapseConnection">The established connection the datagram arrived on.</param>
    /// <param name="packetLength">Size of the datagram in bytes.</param>
    /// <returns>The filter verdict for this datagram.</returns>
    internal FilterResult InspectEstablished(SynapseConnection synapseConnection, int packetLength)
    {
        if (!_isEnabled)
            return FilterResult.Allowed;

        if (packetLength <= 0 || packetLength > _maximumPacketSize)
            return FilterResult.Oversized;

        // Packet-count and byte-count caps run as paired per-receive checks against
        // counters that the maintenance loop resets once per second. They catch two
        // distinct abuse shapes: packet floods (many tiny packets) and bandwidth floods
        // (fewer but larger packets that stay under the pps cap).
        if (!synapseConnection.AllowReceivePacket(_effectiveMaximumPacketsPerSecond))
            return FilterResult.RateLimited;

        if (!synapseConnection.AllowReceiveBytes(packetLength, _effectiveMaximumBytesPerSecond))
            return FilterResult.RateLimited;

        return FilterResult.Allowed;
    }

    /// <summary>
    /// Lowest-level filter for packets from an unknown or not-yet-established sender.
    /// Computes the signature (so violation reports carry the correct peer identity even for rejected packets),
    /// checks the blacklist, and bounds the packet size.
    /// <para>
    /// Deliberately does <b>not</b> rate limit: the per-second counters live on a <see cref="SynapseConnection"/>,
    /// and by definition there is no connection yet. Pre-connection abuse is bounded elsewhere, by the connection
    /// cap, the occupancy-triggered handshake challenge, and the per-poll receive budget.
    /// </para>
    /// </summary>
    /// <param name="endPoint">The remote endpoint the packet arrived from.</param>
    /// <param name="packetLength">Length of the received packet in bytes.</param>
    /// <param name="signature">The computed peer signature, or <see cref="UnsetSignature"/> on failure.</param>
    /// <returns>A <see cref="FilterResult"/> indicating whether the packet should be processed or dropped.</returns>
    public FilterResult InspectNew(IPEndPoint endPoint, int packetLength, out ulong signature)
    {
        // Reject immediately if the signature cannot be computed or resolves to the unset sentinel.
        // Without a valid identity we cannot rate-limit, blacklist, or attribute a violation correctly, so there is nothing useful we can do with the packet.
        if (!SignatureProvider.TryCompute(endPoint, ReadOnlySpan<byte>.Empty, out signature) || signature == UnsetSignature)
        {
            signature = UnsetSignature;
            return FilterResult.SignatureFailure;
        }

        /* IsBlacklisted, not a bare ContainsKey: the entry carries an expiry and only that accessor honours it.
         * Reading the key directly made every ban permanent on the one path that enforces bans, so
         * BlacklistDurationMilliseconds governed nothing an arriving datagram could observe, and an endpoint kicked
         * for a transient violation could never reconnect. It is also what drains lapsed entries, since the table
         * has no sweep of its own. */
        if (IsBlacklisted(signature))
            return FilterResult.Blacklisted;

        if (packetLength <= 0 || packetLength > _maximumPacketSize)
            return FilterResult.Oversized;

        return FilterResult.Allowed;
    }

    /// <summary>
    /// Inserts a blacklist entry, keeping the table within <see cref="MaximumBlacklistEntries"/>.
    /// </summary>
    /// <param name="signature">The peer signature to blacklist.</param>
    /// <param name="nowTicks">Current timestamp in <see cref="DateTime.Ticks"/>.</param>
    private void AddToBlacklistBounded(ulong signature, long nowTicks)
    {
        if (_blacklist.Count >= MaximumBlacklistEntries && !_blacklist.ContainsKey(signature))
        {
            RemoveExpiredBlacklistEntries(nowTicks);

            /* Sweeping lapsed entries is the cheap recovery. If the table is still full every entry is live, which
             * means the ceiling itself is the bound in force. Drop the oldest-by-expiry rather than clearing
             * wholesale, so a flood cannot evict every genuine ban it has driven the engine into issuing. */
            if (_blacklist.Count >= MaximumBlacklistEntries)
                RemoveEarliestBlacklistEntry();
        }

        _blacklist[signature] = _blacklistDurationTicks is PermanentExpiryTicks
            ? PermanentExpiryTicks
            : nowTicks + _blacklistDurationTicks;
    }

    /// <summary>
    /// Drops every blacklist entry whose expiry has passed.
    /// </summary>
    /// <param name="nowTicks">Current timestamp in <see cref="DateTime.Ticks"/>.</param>
    private void RemoveExpiredBlacklistEntries(long nowTicks)
    {
        foreach (KeyValuePair<ulong, long> entry in _blacklist)
            if (entry.Value is not PermanentExpiryTicks && nowTicks >= entry.Value)
                _blacklist.TryRemove(entry.Key, out _);
    }

    /// <summary>
    /// Drops the single blacklist entry that expires soonest, used only when the table is full of live entries.
    /// </summary>
    private void RemoveEarliestBlacklistEntry()
    {
        ulong earliestSignature = 0;
        long earliestExpiry = long.MaxValue;
        bool hasCandidate = false;

        foreach (KeyValuePair<ulong, long> entry in _blacklist)
        {
            if (entry.Value >= earliestExpiry)
                continue;

            earliestSignature = entry.Key;
            earliestExpiry = entry.Value;
            hasCandidate = true;
        }

        if (hasCandidate)
            _blacklist.TryRemove(earliestSignature, out _);
    }

    /// <summary>
    /// Mutable violation tally for one signature.
    /// </summary>
    private sealed class ViolationRecord
    {
        /// <summary>
        /// UTC tick the current counting window opened.
        /// </summary>
        public long WindowStartTicks;
        /// <summary>
        /// Violations observed since <see cref="WindowStartTicks"/>.
        /// </summary>
        public uint Count;
    }
}
