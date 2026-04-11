using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.NatServer;

/// <summary>
/// Tracks pending NAT rendezvous sessions keyed by a server-assigned alphanumeric session ID.
/// Sessions are created when a host sends <c>NatRequestSession</c> and remain open until the host
/// explicitly closes them via <c>NatCloseSession</c> or the session times out.
/// Multiple joiners may register against the same session ID; each is matched directly to the host.
/// </summary>
internal sealed class NatSessionRegistry
{
    private sealed class Entry
    {
        internal readonly IPEndPoint Host;
        internal readonly List<IPEndPoint> Joiners = new();
        internal long LastHeartbeatTicks;

        internal Entry(IPEndPoint host)
        {
            Host = host;
            LastHeartbeatTicks = DateTime.UtcNow.Ticks;
        }
    }

    private const string AlphaNumericChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int SessionIdLength = ServerNatConfig.SessionIdLength;

    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly long _timeoutTicks;
    private readonly int _maxConcurrentSessions;

    internal uint SessionTimeoutMilliseconds { get; }

    /// <summary>
    /// Initialises the registry.
    /// </summary>
    /// <param name="sessionTimeoutMilliseconds">Milliseconds of heartbeat silence before a session is evicted.</param>
    /// <param name="maxConcurrentSessions">Maximum number of sessions that may be open simultaneously. 0 = unlimited.</param>
    internal NatSessionRegistry(uint sessionTimeoutMilliseconds = 300_000, int maxConcurrentSessions = 0)
    {
        SessionTimeoutMilliseconds = sessionTimeoutMilliseconds;
        _timeoutTicks = TimeSpan.FromMilliseconds(sessionTimeoutMilliseconds).Ticks;
        _maxConcurrentSessions = maxConcurrentSessions;
    }

    /// <summary>
    /// Attempts to create a new session for <paramref name="host"/>.
    /// Returns false if the concurrent session limit has been reached.
    /// On success, <paramref name="sessionId"/> is set to the server-assigned ID.
    /// </summary>
    internal bool TryCreateSession(IPEndPoint host, out string sessionId)
    {
        if (_maxConcurrentSessions > 0 && _sessions.Count >= _maxConcurrentSessions)
        {
            sessionId = string.Empty;
            return false;
        }

        string id;

        do
        {
            id = GenerateId();
        } while (_sessions.ContainsKey(id));

        _sessions[id] = new(host);
        sessionId = id;
        return true;
    }

    /// <summary>
    /// Registers <paramref name="endpoint"/> as a new joiner for an existing session.
    /// Returns <c>(matched: true, host, joiner)</c> when the joiner is accepted,
    /// <c>(matched: false, notFound: true, ...)</c> when the session ID does not exist or has expired, or
    /// <c>(matched: false, notFound: false, ...)</c> when the host re-registers (heartbeat refresh).
    /// </summary>
    internal (bool matched, bool notFound, IPEndPoint? host, IPEndPoint? joiner) Register(string sessionId, IPEndPoint endpoint)
    {
        if (!_sessions.TryGetValue(sessionId, out Entry? entry))
            return (matched: false, notFound: true, null, null);

        if (entry.Host.Equals(endpoint))
        {
            entry.LastHeartbeatTicks = DateTime.UtcNow.Ticks;
            return (matched: false, notFound: false, null, null);
        }

        entry.Joiners.Add(endpoint);
        return (matched: true, notFound: false, entry.Host, endpoint);
    }

    /// <summary>
    /// Refreshes the heartbeat timestamp for the host of an existing session.
    /// </summary>
    internal void Heartbeat(string sessionId, IPEndPoint endpoint)
    {
        if (_sessions.TryGetValue(sessionId, out Entry? entry) && entry.Host.Equals(endpoint))
            entry.LastHeartbeatTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Closes a session, preventing further joiners. Only accepted if <paramref name="endpoint"/> is the session host.
    /// </summary>
    internal bool CloseSession(string sessionId, IPEndPoint endpoint)
    {
        if (!_sessions.TryGetValue(sessionId, out Entry? entry) || !entry.Host.Equals(endpoint))
            return false;

        _sessions.Remove(sessionId);
        return true;
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

        if (toRemove is null)
            return;

        foreach (string id in toRemove)
            _sessions.Remove(id);
    }

    /// <summary>
    /// Parses a fixed-length ASCII session ID from raw packet bytes.
    /// Returns null if the slice is too short.
    /// </summary>
    internal static string? ParseSessionId(byte[] data, int offset, int length)
    {
        if (data.Length < offset + length)
            return null;

        return Encoding.ASCII.GetString(data, offset, length);
    }

    private static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[SessionIdLength];
        RandomNumberGenerator.Fill(bytes);
        StringBuilder stringBuilder = new(SessionIdLength);

        foreach (byte b in bytes)
            stringBuilder.Append(AlphaNumericChars[b % AlphaNumericChars.Length]);

        return stringBuilder.ToString();
    }
}
