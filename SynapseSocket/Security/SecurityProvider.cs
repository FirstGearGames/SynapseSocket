using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<ulong, byte> _blacklist = new();
    private readonly ConcurrentDictionary<ulong, RateBucket> _rateBuckets = new();
    private readonly uint _maximumPacketsPerSecond;
    private readonly uint _maximumPacketSize;
    /// <summary>
    /// The sentinel value returned when a signature cannot be computed.
    /// The engine never blacklists this value.
    /// </summary>
    public const ulong UnsetSignature = 0;

    /// <summary>
    /// Creates a new security provider.
    /// </summary>
    public SecurityProvider(ISignatureProvider signatureProvider, uint maximumPacketsPerSecond, uint maximumPacketSize)
    {
        SignatureProvider = signatureProvider ?? throw new ArgumentNullException(nameof(signatureProvider));
        _maximumPacketsPerSecond = maximumPacketsPerSecond;
        _maximumPacketSize = maximumPacketSize;
    }

    /// <summary>
    /// Computes the signature for an endpoint.
    /// Returns <see cref="UnsetSignature"/> if the provider reports failure.
    /// </summary>
    public ulong ComputeSignature(IPEndPoint endPoint, ReadOnlySpan<byte> handshakePayload)
    {
        if (!SignatureProvider.TryCompute(endPoint, handshakePayload, out ulong signature))
            return UnsetSignature;
        
        return signature;
    }

    /// <summary>
    /// Returns true if the given signature is blacklisted.
    /// </summary>
    public bool IsBlacklisted(ulong signature) => _blacklist.ContainsKey(signature);

    /// <summary>
    /// Adds a signature to the blacklist.
    /// </summary>
    public void AddToBlacklist(ulong signature) => _blacklist[signature] = 1;

    /// <summary>
    /// Removes a signature from the blacklist.
    /// Returns true if the signature was present and removed.
    /// </summary>
    public bool RemoveFromBlacklist(ulong signature) => _blacklist.TryRemove(signature, out _);

    /// <summary>
    /// Lowest-level filter for packets from an already-established connection.
    /// Checks packet size and the per-endpoint rate limit using the connection's cached signature.
    /// Signature computation and blacklist lookup are intentionally omitted here: they apply only
    /// during the initial handshake path (<see cref="InspectNew"/>). Once a connection is established,
    /// a kick+blacklist action removes it from the connection table, so the next packet from that
    /// peer falls through to <see cref="InspectNew"/> automatically.
    /// </summary>
    public FilterResult InspectEstablished(int packetLength, ulong cachedSignature)
    {
        if (packetLength <= 0 || (uint)packetLength > _maximumPacketSize)
            return FilterResult.Oversized;

        if (_maximumPacketsPerSecond == 0)
            return FilterResult.Allowed;

        RateBucket rateBucket = _rateBuckets.GetOrAdd(cachedSignature, static _ => new());
        return rateBucket.Allow(_maximumPacketsPerSecond) ? FilterResult.Allowed : FilterResult.RateLimited;
    }

    /// <summary>
    /// Lowest-level filter for packets from an unknown or not-yet-established sender.
    /// Computes the signature (so violation reports carry the correct peer identity even for
    /// rejected packets), checks the blacklist, then delegates to <see cref="InspectEstablished"/>
    /// for size and rate-limit enforcement.
    /// </summary>
    public FilterResult InspectNew(IPEndPoint endPoint, int packetLength, out ulong signature)
    {
        // Reject immediately if the signature cannot be computed or resolves to the unset sentinel.
        // Without a valid identity we cannot rate-limit, blacklist, or attribute a violation correctly,
        // so there is nothing useful we can do with the packet.
        if (!SignatureProvider.TryCompute(endPoint, ReadOnlySpan<byte>.Empty, out signature) || signature == UnsetSignature)
        {
            signature = UnsetSignature;
            return FilterResult.SignatureFailure;
        }

        if (_blacklist.ContainsKey(signature))
            return FilterResult.Blacklisted;

        return InspectEstablished(packetLength, signature);
    }

    private sealed class RateBucket
    {
        private long _windowStartTicks;
        private uint _packetCount;
        private readonly object _lock = new();

        public bool Allow(uint maximumPacketsPerSecond)
        {
            lock (_lock)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                long windowTicks = TimeSpan.TicksPerSecond;
                if (nowTicks - _windowStartTicks >= windowTicks)
                {
                    _windowStartTicks = nowTicks;
                    _packetCount = 0;
                }
                if (_packetCount >= maximumPacketsPerSecond)
                    return false;
                _packetCount++;
                return true;
            }
        }
    }
}