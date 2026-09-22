using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using CodeBoost.Performance;

namespace SynapseBeacon.Server;

/// <summary>
/// Tracks pending rendezvous sessions keyed by a server-assigned numeric session ID.
/// Sessions are created when a host sends <see cref="Wire.BeaconPacketType.RequestSession"/> and
/// remain open until the host explicitly closes them via <see cref="Wire.BeaconPacketType.CloseSession"/>
/// or the session times out. Multiple joiners may register against the same session ID; each is
/// matched directly to the host.
/// </summary>
internal sealed class BeaconSessionRegistry
{

    /// <summary>
    /// Attempts to find a free identifier before giving up. The generator previously retried until it succeeded,
    /// which never terminates once the space is saturated, and the space was small enough to saturate cheaply.
    /// </summary>
    private const int MaximumIdentifierAttempts = 64;
    /// <summary>
    /// Active sessions keyed by server-assigned session ID.
    /// </summary>
    private readonly ConcurrentDictionary<uint, Entry> _sessions = new();

    /// <summary>
    /// Heartbeat silence threshold in ticks. Sessions older than this are evicted.
    /// </summary>
    private readonly long _timeoutTicks;

    /// <summary>
    /// Maximum number of sessions allowed simultaneously. Zero means unlimited.
    /// </summary>
    private readonly int _maximumConcurrentSessions;
    /// <summary>
    /// Modulus applied to generated identifiers, or 0 to use the full 32-bit range. Non-zero values exist so a
    /// caller can deliberately constrain the space; production uses the full range.
    /// </summary>
    private readonly uint _identifierSpace;

    /// <summary>
    /// Milliseconds of heartbeat silence before a session is evicted.
    /// </summary>
    internal uint SessionTimeoutMilliseconds { get; }

    /// <summary>
    /// Initialises the registry.
    /// </summary>
    /// <param name="sessionTimeoutMilliseconds">Milliseconds of heartbeat silence before a session is evicted.</param>
    /// <param name="maximumConcurrentSessions">Maximum number of sessions that may be open simultaneously. 0 = unlimited.</param>
    /// <param name="identifierSpace">Modulus for generated identifiers, or 0 for the full 32-bit range.</param>
    internal BeaconSessionRegistry(uint sessionTimeoutMilliseconds, int maximumConcurrentSessions, uint identifierSpace = 0)
    {
        _identifierSpace = identifierSpace;
        SessionTimeoutMilliseconds = sessionTimeoutMilliseconds;
        _timeoutTicks = TimeSpan.FromMilliseconds(sessionTimeoutMilliseconds).Ticks;
        _maximumConcurrentSessions = maximumConcurrentSessions;
    }

    /// <summary>
    /// Attempts to create a new session for <paramref name="host"/>.
    /// Returns false if the concurrent session limit has been reached.
    /// On success, <paramref name="sessionId"/> is set to the server-assigned ID.
    /// </summary>
    internal bool TryCreateSession(IPEndPoint host, out uint sessionId)
    {
        if (_maximumConcurrentSessions > 0 && _sessions.Count >= _maximumConcurrentSessions)
        {
            sessionId = 0;
            return false;
        }

        Entry entry = new(host);

        for (int attempt = 0; attempt < MaximumIdentifierAttempts; attempt++)
        {
            uint candidate = GenerateId();

            if (_sessions.TryAdd(candidate, entry))
            {
                sessionId = candidate;
                return true;
            }
        }

        // Space saturated. Refusing is the only safe answer; retrying forever hangs the receive loop.
        sessionId = 0;
        return false;
    }

    /// <summary>
    /// Registers <paramref name="endpoint"/> as a joiner for an existing session.
    /// Returns <c>(matched: true, host, joiner)</c> when the joiner is accepted,
    /// <c>(matched: false, notFound: true, ...)</c> when the session ID does not exist or has expired, or
    /// <c>(matched: false, notFound: false, ...)</c> when the host re-registers (heartbeat refresh).
    /// </summary>
    internal (bool matched, bool notFound, IPEndPoint? host, IPEndPoint? joiner) Register(uint sessionId, IPEndPoint endpoint)
    {
        if (!_sessions.TryGetValue(sessionId, out Entry? entry))
            return (matched: false, notFound: true, null, null);

        if (entry.Host.Equals(endpoint))
        {
            entry.LastHeartbeatTicks = DateTime.UtcNow.Ticks;
            return (matched: false, notFound: false, null, null);
        }

        return (matched: true, notFound: false, entry.Host, endpoint);
    }

    /// <summary>
    /// Refreshes the heartbeat timestamp for the host of an existing session.
    /// </summary>
    internal void Heartbeat(uint sessionId, IPEndPoint endpoint)
    {
        if (_sessions.TryGetValue(sessionId, out Entry? entry) && entry.Host.Equals(endpoint))
            entry.LastHeartbeatTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Closes a session, preventing further joiners. Only accepted if <paramref name="endpoint"/> is the session host.
    /// </summary>
    internal bool CloseSession(uint sessionId, IPEndPoint endpoint)
    {
        if (!_sessions.TryGetValue(sessionId, out Entry? entry) || !entry.Host.Equals(endpoint))
            return false;

        _sessions.TryRemove(sessionId, out _);
        return true;
    }

    /// <summary>
    /// Removes sessions that have not received a heartbeat within the timeout window.
    /// </summary>
    internal void EvictExpired()
    {
        long cutoff = DateTime.UtcNow.Ticks - _timeoutTicks;
        List<uint>? toRemove = null;

        foreach (KeyValuePair<uint, Entry> kvp in _sessions)
        {
            if (kvp.Value.LastHeartbeatTicks < cutoff)
            {
                toRemove ??= ListPool<uint>.Rent();
                toRemove.Add(kvp.Key);
            }
        }

        if (toRemove is null)
            return;

        foreach (uint id in toRemove)
            _sessions.TryRemove(id, out _);

        ListPool<uint>.Return(toRemove);
    }

    /// <summary>
    /// Generates a session identifier from a cryptographic source across the full 32-bit range.
    /// </summary>
    /// <remarks>
    /// The previous six-digit range held ~900,000 values and came from <see cref="Random"/> seeded off the tick
    /// count. JoinSession discloses the host endpoint for any identifier that hits, so a small predictable space
    /// lets an attacker sweep it end to end and harvest every host the beacon knows.
    /// </remarks>
    private uint GenerateId()
    {
        Span<byte> identifierBytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(identifierBytes);

        uint identifier = BinaryPrimitives.ReadUInt32LittleEndian(identifierBytes);

        if (_identifierSpace != 0)
            identifier %= _identifierSpace;

        // 0 is the failure sentinel on this API, so never hand it out as a live identifier.
        return identifier == 0 && _identifierSpace != 1 ? 1u : identifier;
    }

    /// <summary>
    /// Holds the state for a single active rendezvous session.
    /// </summary>
    private sealed class Entry
    {
        /// <summary>
        /// External endpoint of the host that created this session.
        /// </summary>
        internal readonly IPEndPoint Host;

        /// <summary>
        /// UTC ticks of the last heartbeat received from the host. Used to evict stale sessions.
        /// </summary>
        internal long LastHeartbeatTicks;

        internal Entry(IPEndPoint host)
        {
            Host = host;
            LastHeartbeatTicks = DateTime.UtcNow.Ticks;
        }
    }
}
