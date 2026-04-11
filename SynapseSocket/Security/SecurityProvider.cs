using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;

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
    /// Set of blacklisted peer signatures. Values are unused placeholders.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, byte> _blacklist = [];
    /// <summary>
    /// Per-signature sliding-window rate buckets, keyed by peer signature.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, RateBucket> _rateBuckets = [];
    /// <summary>
    /// Maximum number of packets a single peer may send per second. Zero disables packet rate limiting.
    /// </summary>
    private readonly uint _maximumPacketsPerSecond;
    /// <summary>
    /// Maximum number of bytes a single peer may send per second. Zero disables byte rate limiting.
    /// </summary>
    private readonly uint _maximumBytesPerSecond;
    /// <summary>
    /// Maximum permitted size of a single incoming packet in bytes. Packets exceeding this are rejected.
    /// </summary>
    private readonly uint _maximumPacketSize;
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
    public SecurityProvider(ISignatureProvider signatureProvider, uint maximumPacketsPerSecond, uint maximumBytesPerSecond, uint maximumPacketSize)
    {
        SignatureProvider = signatureProvider ?? throw new ArgumentNullException(nameof(signatureProvider));
        _maximumPacketsPerSecond = maximumPacketsPerSecond;
        _maximumBytesPerSecond = maximumBytesPerSecond;
        _maximumPacketSize = maximumPacketSize;
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
    public bool IsBlacklisted(ulong signature) => _blacklist.ContainsKey(signature);

    /// <summary>
    /// Adds a signature to the blacklist.
    /// </summary>
    /// <param name="signature">The peer signature to blacklist.</param>
    public void AddToBlacklist(ulong signature) => _blacklist[signature] = 1;

    /// <summary>
    /// Removes a signature from the blacklist.
    /// Returns true if the signature was present and removed.
    /// </summary>
    /// <param name="signature">The peer signature to remove.</param>
    /// <returns>True if the signature was present and has been removed; false if it was not found.</returns>
    public bool RemoveFromBlacklist(ulong signature) => _blacklist.TryRemove(signature, out _);

    /// <summary>
    /// Lowest-level filter for packets from an already-established connection.
    /// Checks packet size and the per-endpoint rate limit using the connection's cached signature.
    /// Signature computation and blacklist lookup are intentionally omitted here: they apply only during the initial handshake path (<see cref="InspectNew"/>).
    /// Once a connection is established, a kick+blacklist action removes it from the connection table, so the next packet from that peer falls through to <see cref="InspectNew"/> automatically.
    /// </summary>
    /// <param name="packetLength">Length of the received packet in bytes.</param>
    /// <param name="cachedSignature">The pre-computed signature stored on the connection.</param>
    /// <returns>A <see cref="FilterResult"/> indicating whether the packet should be processed or dropped.</returns>
    public FilterResult InspectEstablished(int packetLength, ulong cachedSignature)
    {
        if (packetLength <= 0 || (uint)packetLength > _maximumPacketSize)
            return FilterResult.Oversized;

        if (_maximumPacketsPerSecond == 0 && _maximumBytesPerSecond == 0)
            return FilterResult.Allowed;

        RateBucket rateBucket = _rateBuckets.GetOrAdd(cachedSignature, static _ => new());
        return rateBucket.Allow(packetLength, _maximumPacketsPerSecond, _maximumBytesPerSecond) ? FilterResult.Allowed : FilterResult.RateLimited;
    }

    /// <summary>
    /// Lowest-level filter for packets from an unknown or not-yet-established sender.
    /// Computes the signature (so violation reports carry the correct peer identity even for rejected packets), checks the blacklist, then delegates to <see cref="InspectEstablished"/> for size and rate-limit enforcement.
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

        if (_blacklist.ContainsKey(signature))
            return FilterResult.Blacklisted;

        return InspectEstablished(packetLength, signature);
    }

    /// <summary>
    /// Removes rate buckets that have not seen traffic in longer than <paramref name="expiryTicks"/>.
    /// Call from the maintenance loop to bound <c>_rateBuckets</c> growth.
    /// </summary>
    /// <param name="nowTicks">The current UTC tick count.</param>
    /// <param name="expiryTicks">The number of ticks of inactivity after which a bucket is removed.</param>
    public void RemoveExpiredRateBuckets(long nowTicks, long expiryTicks)
    {
        foreach (KeyValuePair<ulong, RateBucket> entry in _rateBuckets)
        {
            if (nowTicks - entry.Value.LastAccessTicks > expiryTicks)
                _rateBuckets.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>
    /// Sliding-window rate limiter for a single endpoint signature.
    /// Resets the packet counter once the current one-second window elapses.
    /// </summary>
    private sealed class RateBucket
    {
        /// <summary>
        /// Tick timestamp of the most recent access, used for stale-entry eviction.
        /// </summary>
        public long LastAccessTicks;
        /// <summary>
        /// Tick timestamp marking the start of the current one-second rate window.
        /// </summary>
        private long _windowStartTicks;
        /// <summary>
        /// Number of packets admitted in the current rate window.
        /// </summary>
        private uint _packetCount;
        /// <summary>
        /// Number of bytes admitted in the current rate window.
        /// </summary>
        private uint _byteCount;
        /// <summary>
        /// Synchronisation guard for the sliding-window state.
        /// </summary>
        private readonly object _lock = new();

        /// <summary>
        /// Returns true if the endpoint may send this packet within the current window, incrementing both counters; returns false when either limit has been reached.
        /// </summary>
        /// <param name="packetLength">Size of the incoming packet in bytes.</param>
        /// <param name="maximumPacketsPerSecond">Maximum packets per second; zero disables packet rate limiting.</param>
        /// <param name="maximumBytesPerSecond">Maximum bytes per second; zero disables byte rate limiting.</param>
        /// <returns>True if the packet is within both rate limits; false if either limit has been exceeded.</returns>
        public bool Allow(int packetLength, uint maximumPacketsPerSecond, uint maximumBytesPerSecond)
        {
            lock (_lock)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                LastAccessTicks = nowTicks;

                if (nowTicks - _windowStartTicks >= TimeSpan.TicksPerSecond)
                {
                    _windowStartTicks = nowTicks;
                    _packetCount = 0;
                    _byteCount = 0;
                }

                if (maximumPacketsPerSecond > 0 && _packetCount >= maximumPacketsPerSecond)
                    return false;

                if (maximumBytesPerSecond > 0 && _byteCount + (uint)packetLength > maximumBytesPerSecond)
                    return false;

                _packetCount++;
                _byteCount += (uint)packetLength;
                return true;
            }
        }
    }
}