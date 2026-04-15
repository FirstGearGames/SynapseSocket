using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
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
    /// Holds the state for a single active rendezvous session.
    /// </summary>
    private sealed class Entry
    {
        /// <summary>External endpoint of the host that created this session.</summary>
        internal readonly IPEndPoint Host;
        /// <summary>UTC ticks of the last heartbeat received from the host. Used to evict stale sessions.</summary>
        internal long LastHeartbeatTicks;

        internal Entry(IPEndPoint host)
        {
            Host = host;
            LastHeartbeatTicks = DateTime.UtcNow.Ticks;
        }
    }

    private readonly ConcurrentDictionary<uint, Entry> _sessions = new();
    private readonly long _timeoutTicks;
    private readonly int _maximumConcurrentSessions;
    [ThreadStatic]
    private static Random? _threadRandom;

    /// <summary>
    /// Lazily initializes and returns a per-thread <see cref="Random"/> instance.
    /// Avoids locking when generating session IDs under concurrent requests.
    /// </summary>
    private static Random ThreadRandom => _threadRandom ??= new Random(unchecked(Environment.TickCount * 397) ^ Environment.CurrentManagedThreadId);

    /// <summary>
    /// Milliseconds of heartbeat silence before a session is evicted.
    /// </summary>
    internal uint SessionTimeoutMilliseconds { get; }

    /// <summary>
    /// Initialises the registry.
    /// </summary>
    /// <param name="sessionTimeoutMilliseconds">Milliseconds of heartbeat silence before a session is evicted.</param>
    /// <param name="maximumConcurrentSessions">Maximum number of sessions that may be open simultaneously. 0 = unlimited.</param>
    internal BeaconSessionRegistry(uint sessionTimeoutMilliseconds = 300_000, int maximumConcurrentSessions = 0)
    {
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

        while (true)
        {
            uint candidate = GenerateId();

            if (_sessions.TryAdd(candidate, entry))
            {
                sessionId = candidate;
                return true;
            }
        }
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
    /// Generates a random session ID in the range [100000, 1000000).
    /// </summary>
    private static uint GenerateId() => (uint)ThreadRandom.Next(100000, 1000000);
}
