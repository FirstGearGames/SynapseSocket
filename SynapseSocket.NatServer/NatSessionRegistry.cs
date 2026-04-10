using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace SynapseSocket.NatServer;

/// <summary>
/// Tracks pending NAT rendezvous sessions keyed by a 6-character alphanumeric session ID.
/// Once both peer endpoints are registered the session is removed and the caller sends
/// <c>PeerReady</c> to each side. Sessions that receive no heartbeat within
/// <see cref="SessionTimeoutMilliseconds"/> are evicted automatically.
/// </summary>
internal sealed class NatSessionRegistry
{
    private sealed class Entry
    {
        internal IPEndPoint First;
        internal IPEndPoint? Second;
        internal long LastHeartbeatTicks;

        internal Entry(IPEndPoint first)
        {
            First = first;
            LastHeartbeatTicks = DateTime.UtcNow.Ticks;
        }
    }

    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly long _timeoutTicks;

    internal uint SessionTimeoutMilliseconds { get; }

    internal NatSessionRegistry(uint sessionTimeoutMilliseconds = 30_000)
    {
        SessionTimeoutMilliseconds = sessionTimeoutMilliseconds;
        _timeoutTicks = TimeSpan.FromMilliseconds(sessionTimeoutMilliseconds).Ticks;
    }

    /// <summary>
    /// Registers <paramref name="endpoint"/> for <paramref name="sessionId"/>.
    /// Returns <c>(matched: true, first, second)</c> when a second peer arrives,
    /// <c>(matched: false, null, null)</c> while still waiting, or
    /// <c>(matched: false, null, null)</c> when the session is already full (caller sends SessionFull).
    /// </summary>
    internal (bool matched, bool full, IPEndPoint? first, IPEndPoint? second) Register(string sessionId, IPEndPoint endpoint)
    {
        if (_sessions.TryGetValue(sessionId, out Entry? entry))
        {
            if (entry.Second is not null)
                return (matched: false, full: true, null, null);

            if (entry.First.Equals(endpoint))
            {
                entry.LastHeartbeatTicks = DateTime.UtcNow.Ticks;
                return (matched: false, full: false, null, null);
            }

            entry.Second = endpoint;
            _sessions.Remove(sessionId);
            return (matched: true, full: false, entry.First, endpoint);
        }

        _sessions[sessionId] = new(endpoint);
        return (matched: false, full: false, null, null);
    }

    /// <summary>
    /// Refreshes the heartbeat timestamp for an existing single-peer session.
    /// </summary>
    internal void Heartbeat(string sessionId, IPEndPoint endpoint)
    {
        if (_sessions.TryGetValue(sessionId, out Entry? entry) && entry.First.Equals(endpoint))
            entry.LastHeartbeatTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Removes sessions that have not received a heartbeat within the timeout window.
    /// </summary>
    internal void EvictExpired()
    {
        long cutoff = DateTime.UtcNow.Ticks - _timeoutTicks;
        List<string>? toRemove = null;
        foreach (KeyValuePair<string, Entry> kv in _sessions)
        {
            if (kv.Value.LastHeartbeatTicks < cutoff)
            {
                toRemove ??= new();
                toRemove.Add(kv.Key);
            }
        }
        if (toRemove is null) return;
        foreach (string id in toRemove)
            _sessions.Remove(id);
    }

    /// <summary>
    /// Parses a fixed-length ASCII session ID from raw packet bytes.
    /// Returns null if the slice is too short.
    /// </summary>
    internal static string? ParseSessionId(byte[] data, int offset, int length)
    {
        if (data.Length < offset + length) return null;
        return Encoding.ASCII.GetString(data, offset, length);
    }
}