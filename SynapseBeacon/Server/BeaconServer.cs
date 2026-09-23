using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SynapseBeacon.Wire;

namespace SynapseBeacon.Server;

/// <summary>
/// Lightweight UDP rendezvous server that matches SynapseBeacon peers for NAT hole-punching.
/// <para>
/// The host sends <see cref="BeaconPacketType.RequestSession"/> to obtain a server-assigned session ID.
/// The server responds with <see cref="BeaconPacketType.SessionCreated"/> containing the ID.
/// The host shares the ID out-of-band with any number of joiners.
/// Each joiner sends <see cref="BeaconPacketType.JoinSession"/> with the shared ID; the server responds
/// to that joiner with the host's external endpoint and to the host with the joiner's endpoint,
/// after which hole-punching proceeds directly between each pair using the caller's
/// <c>SynapseSocket</c> engine.
/// The host closes the session with <see cref="BeaconPacketType.CloseSession"/> when done accepting.
/// Sessions that receive no heartbeat within <see cref="BeaconSessionRegistry.SessionTimeoutMilliseconds"/>
/// are evicted automatically.
/// </para>
/// </summary>
public sealed class BeaconServer : IDisposable
{
    /// <summary>
    /// Session timeout used when the caller does not supply an explicit value.
    /// </summary>
    public const uint DefaultSessionTimeoutMilliseconds = 300_000;

    /// <summary>
    /// Sentinel for <c>maximumConcurrentSessions</c> indicating no cap is enforced.
    /// </summary>
    public const int UnlimitedConcurrentSessions = 0;

    /// <summary>
    /// UDP socket bound to the configured port. Shared for all sends and receives.
    /// </summary>
    private readonly UdpClient _socket;

    /// <summary>
    /// In-memory store of active rendezvous sessions.
    /// </summary>
    private readonly BeaconSessionRegistry _registry;

    /// <summary>
    /// Periodic timer that drives session eviction on a fixed cadence.
    /// </summary>
    private readonly Timer _evictionTimer;

    /// <summary>
    /// Optional log sink supplied by the caller. Null when logging is disabled.
    /// </summary>
    private readonly Action<string>? _log;

    /// <summary>
    /// Keyed MAC signing join cookies. Generated once at construction and never transmitted, so a cookie can only
    /// be produced by this server and only checked by it.
    /// </summary>
    private readonly HMACSHA256 _cookieHmac;

    /// <summary>
    /// Per-source request tallies. Swept on the same timer that evicts sessions, because a table keyed by an
    /// attacker-chosen address is itself a memory-flooding vector if it only ever grows.
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, RequestRate> _requestRates = new();

    /// <summary>
    /// Immutable single-byte payload for <see cref="BeaconPacketType.HeartbeatAck"/>. Shared across all sends.
    /// </summary>
    private static readonly byte[] HeartbeatAckPacket = [(byte)BeaconPacketType.HeartbeatAck];

    /// <summary>
    /// Immutable single-byte payload for <see cref="BeaconPacketType.ServerAtCapacity"/>. Shared across all sends.
    /// </summary>
    private static readonly byte[] ServerAtCapacityPacket = [(byte)BeaconPacketType.ServerAtCapacity];

    /// <summary>
    /// Immutable single-byte payload for <see cref="BeaconPacketType.SessionNotFound"/>. Shared across all sends.
    /// </summary>
    private static readonly byte[] SessionNotFoundPacket = [(byte)BeaconPacketType.SessionNotFound];

    /// <summary>
    /// Duration of a cookie time bucket. A cookie is accepted for the current bucket and the previous one, giving
    /// a joiner roughly 30 to 60 seconds to answer a challenge and leaving retries under packet loss valid.
    /// </summary>
    private static readonly long CookieTimeBucketTicks = 30 * TimeSpan.TicksPerSecond;

    /// <summary>
    /// Window over which <see cref="MaximumRequestsPerWindow"/> is counted.
    /// </summary>
    private static readonly long RateLimitWindowTicks = TimeSpan.TicksPerSecond;

    /// <summary>
    /// Requests each source address may make per <see cref="RateLimitWindowTicks"/> before the rest are dropped.
    /// Sized for a legitimate client, which sends one session request or two join requests and then heartbeats
    /// every 30 seconds; many players behind one carrier NAT still sit far below it.
    /// </summary>
    private const int MaximumRequestsPerWindow = 20;

    /// <summary>
    /// Initialises the server bound to <paramref name="port"/> on all interfaces.
    /// </summary>
    /// <param name="port">UDP port to listen on.</param>
    /// <param name="sessionTimeoutMilliseconds">Milliseconds of heartbeat silence before a session is evicted.</param>
    /// <param name="maximumConcurrentSessions">Maximum number of sessions open simultaneously. 0 = unlimited.</param>
    /// <param name="log">Optional log sink. When null, logging is silent.</param>
    public BeaconServer(int port, uint sessionTimeoutMilliseconds, int maximumConcurrentSessions, Action<string>? log)
    {
        _socket = new(port);
        _registry = new(sessionTimeoutMilliseconds, maximumConcurrentSessions);
        _evictionTimer = new(_ => RunMaintenance(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _log = log;

        Span<byte> cookieSecret = stackalloc byte[32];
        RandomNumberGenerator.Fill(cookieSecret);
        _cookieHmac = new(cookieSecret.ToArray());
    }

    /// <summary>
    /// Evicts expired sessions and stale rate-limit entries. Driven by the eviction timer rather than the receive
    /// loop, so neither sweep sits on a path an attacker can time.
    /// </summary>
    private void RunMaintenance()
    {
        _registry.EvictExpired();

        long cutoffTicks = DateTime.UtcNow.Ticks - RateLimitWindowTicks;

        foreach (KeyValuePair<IPAddress, RequestRate> entry in _requestRates)
            if (entry.Value.WindowStartTicks < cutoffTicks)
                _requestRates.TryRemove(entry.Key, out _);
    }

    /// <summary>
    /// Returns false when <paramref name="from"/> has exceeded <see cref="MaximumRequestsPerWindow"/> inside the
    /// current window, in which case the datagram is dropped without a reply.
    /// </summary>
    /// <param name="from">Source address of the datagram.</param>
    private bool IsWithinRateLimit(IPEndPoint from)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        RequestRate requestRate = _requestRates.GetOrAdd(from.Address, static _ => new RequestRate());

        lock (requestRate)
        {
            if (nowTicks - requestRate.WindowStartTicks >= RateLimitWindowTicks)
            {
                requestRate.WindowStartTicks = nowTicks;
                requestRate.Count = 0;
            }

            requestRate.Count++;
            return requestRate.Count <= MaximumRequestsPerWindow;
        }
    }

    /// <summary>
    /// Computes the cookie bound to <paramref name="endPoint"/> and <paramref name="timeBucket"/>, writing exactly
    /// <see cref="BeaconWireFormat.CookieBytes"/> bytes into <paramref name="destination"/>.
    /// </summary>
    /// <param name="endPoint">Endpoint the cookie is bound to.</param>
    /// <param name="timeBucket">Coarse time bucket the cookie is bound to.</param>
    /// <param name="destination">Receives the truncated MAC.</param>
    private void ComputeCookie(IPEndPoint endPoint, long timeBucket, Span<byte> destination)
    {
        Span<byte> addressBytes = stackalloc byte[16];
        endPoint.Address.TryWriteBytes(addressBytes, out int addressLength);

        Span<byte> input = stackalloc byte[addressLength + 2 + 8];
        addressBytes[..addressLength].CopyTo(input);

        int offset = addressLength;
        input[offset++] = (byte)(endPoint.Port & 0xFF);
        input[offset++] = (byte)((endPoint.Port >> 8) & 0xFF);

        for (int i = 0; i < 8; i++)
            input[offset++] = (byte)((timeBucket >> (i * 8)) & 0xFF);

        Span<byte> hashBuffer = stackalloc byte[32];
        _cookieHmac.TryComputeHash(input, hashBuffer, out _);
        hashBuffer[..BeaconWireFormat.CookieBytes].CopyTo(destination);
    }

    /// <summary>
    /// Returns true when <paramref name="cookie"/> is one this server issued to <paramref name="endPoint"/> in the
    /// current or previous time bucket.
    /// </summary>
    /// <param name="endPoint">Endpoint the cookie should be bound to.</param>
    /// <param name="cookie">The cookie the joiner presented.</param>
    private bool VerifyCookie(IPEndPoint endPoint, ReadOnlySpan<byte> cookie)
    {
        long bucket = DateTime.UtcNow.Ticks / CookieTimeBucketTicks;
        Span<byte> expected = stackalloc byte[BeaconWireFormat.CookieBytes];

        /* Fixed-time compare: the cookie is a secret the joiner must reproduce, so an early-exit comparison leaks
         * how many leading bytes were right. */
        ComputeCookie(endPoint, bucket, expected);
        if (CryptographicOperations.FixedTimeEquals(cookie, expected))
            return true;

        ComputeCookie(endPoint, bucket - 1, expected);

        return CryptographicOperations.FixedTimeEquals(cookie, expected);
    }

    /// <summary>
    /// Runs the receive loop until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _log?.Invoke($"[BeaconServer] listening (session timeout: {_registry.SessionTimeoutMilliseconds} ms)");

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;

            try
            {
                #if NET8_0_OR_GREATER
                result = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                #else
                result = await _socket.ReceiveAsync().ConfigureAwait(false);
                #endif
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _log?.Invoke($"[BeaconServer] socket error: {ex.Message}");
                continue;
            }

            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    /// <summary>
    /// Routes an inbound datagram to the appropriate handler based on its <see cref="BeaconPacketType"/> byte.
    /// </summary>
    private void HandleDatagram(byte[] data, IPEndPoint from)
    {
        // Layout: [BeaconPacketType (1 byte)] [payload]
        if (data.Length < 1)
            return;

        /* Checked before the type is even read. Every request below either allocates state or draws a reply, so
         * the limit has to sit ahead of all of them rather than inside any one handler. */
        if (!IsWithinRateLimit(from))
            return;

        BeaconPacketType type = (BeaconPacketType)data[0];

        switch (type)
        {
            case BeaconPacketType.RequestSession:
                HandleRequestSession(data, from);
                break;

            case BeaconPacketType.JoinSession:
                HandleRegister(data, from);
                break;

            case BeaconPacketType.Heartbeat:
                HandleHeartbeat(data, from);
                break;

            case BeaconPacketType.CloseSession:
                HandleCloseSession(data, from);
                break;
        }
    }

    /// <summary>
    /// Creates a new session for the requesting host and sends back a <see cref="BeaconPacketType.SessionCreated"/> response,
    /// or <see cref="BeaconPacketType.ServerAtCapacity"/> if the concurrent session limit has been reached.
    /// </summary>
    private void HandleRequestSession(byte[] data, IPEndPoint from)
    {
        if (!_registry.TryCreateSession(from, out uint sessionId))
        {
            _log?.Invoke($"[BeaconServer] session limit reached. Rejecting request from {from}.");
            SendServerAtCapacity(from);
            return;
        }

        _log?.Invoke($"[BeaconServer] created session '{sessionId}' for host {from}.");
        SendSessionCreated(from, sessionId, ReadNonce(data));
    }

    /// <summary>
    /// Registers a joining peer against an existing session. Sends <see cref="BeaconPacketType.PeerReady"/> to both
    /// the joiner and the host on success, and <see cref="BeaconPacketType.SessionNotFound"/> when the id is unknown.
    /// The joiner's copy echoes the nonce it sent, so a reply forged from the server's address cannot be accepted.
    /// </summary>
    private void HandleRegister(byte[] data, IPEndPoint from)
    {
        if (!BeaconWireFormat.TryReadSessionId(data.AsSpan(1), out uint sessionId))
            return;

        /* A first-hand join is answered with a challenge and nothing else. No session lookup runs, so the reply is
         * identical whatever ID was named and the exchange leaks nothing at this stage. A source that cannot
         * receive at the address it claimed never gets the cookie, and so never reaches the disclosure below:
         * that is what stops a forged join from aiming a host's hole-punch burst at a third party. */
        if (data.Length < BeaconWireFormat.ProvenJoinBytes)
        {
            SendJoinChallenge(from, ReadNonce(data, BeaconWireFormat.SessionIdBytes));
            return;
        }

        if (!VerifyCookie(from, data.AsSpan(BeaconWireFormat.UnprovenJoinBytes, BeaconWireFormat.CookieBytes)))
            return;

        (bool matched, bool notFound, IPEndPoint? host, IPEndPoint? joiner) = _registry.Register(sessionId, from);

        if (notFound)
        {
            /* Answered only now, behind the cookie. The reply does distinguish a live ID from a dead one, but only
             * for a joiner that has already proven it receives at its own address and is inside the rate limit, so
             * sweeping the 32-bit space costs a round trip per guess from an address that can be blocked. Keeping
             * it is what lets a mistyped code fail immediately instead of hanging until the client's timeout. */
            _log?.Invoke($"[BeaconServer] session '{sessionId}' not found, notifying {from}.");
            SendSessionNotFound(from, ReadNonce(data, BeaconWireFormat.SessionIdBytes));
            return;
        }

        if (!matched)
            return;

        _log?.Invoke($"[BeaconServer] matched session '{sessionId}': host {host} <-> joiner {joiner}");
        // The joiner's copy carries its nonce back so it can tell this reply from a forged one.
        SendPeerReady(joiner!, host!, ReadNonce(data, BeaconWireFormat.SessionIdBytes));
        SendPeerReady(host!, joiner!, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Refreshes the heartbeat timestamp for an existing session and acknowledges with <see cref="BeaconPacketType.HeartbeatAck"/>.
    /// </summary>
    private void HandleHeartbeat(byte[] data, IPEndPoint from)
    {
        if (!BeaconWireFormat.TryReadSessionId(data.AsSpan(1), out uint sessionId))
            return;

        /* Acknowledged only when the refresh actually applied, which means the sender is that session's host. The
         * registry already ignored everyone else; the acknowledgement did not, leaving the server an
         * unauthenticated reflector that answered any address naming any number. */
        if (_registry.Heartbeat(sessionId, from))
            SendHeartbeatAck(from);
    }

    /// <summary>
    /// Closes an existing session, preventing any further joiners. Only honoured when the request comes from the session host.
    /// </summary>
    private void HandleCloseSession(byte[] data, IPEndPoint from)
    {
        if (!BeaconWireFormat.TryReadSessionId(data.AsSpan(1), out uint sessionId))
            return;

        if (_registry.CloseSession(sessionId, from))
            _log?.Invoke($"[BeaconServer] session '{sessionId}' closed by host {from}.");
    }

    // -------------------------------------------------------------------------
    // Send helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.SessionCreated"/> packet carrying the server-assigned session ID.
    /// </summary>
    private void SendSessionCreated(IPEndPoint to, uint sessionId, ReadOnlySpan<byte> nonce)
    {
        int size = 1 + BeaconWireFormat.SessionIdBytes + nonce.Length;
        byte[] packet = ArrayPool<byte>.Shared.Rent(size);

        BeaconWireFormat.WriteTypeAndSessionId(packet.AsSpan(), BeaconPacketType.SessionCreated, sessionId);
        nonce.CopyTo(packet.AsSpan(1 + BeaconWireFormat.SessionIdBytes));

        _ = SendAndReturnAsync(packet, size, to);
    }

    /// <summary>
    /// Extracts the client nonce that follows the fixed part of a request, or an empty span when absent.
    /// </summary>
    private static ReadOnlySpan<byte> ReadNonce(byte[] data, int fixedPayloadBytes = 0)
    {
        int offset = 1 + fixedPayloadBytes;

        if (data.Length < offset + BeaconWireFormat.NonceBytes)
            return ReadOnlySpan<byte>.Empty;

        return data.AsSpan(offset, BeaconWireFormat.NonceBytes);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.PeerReady"/> packet to <paramref name="to"/> carrying <paramref name="peer"/>'s external endpoint.
    /// </summary>
    private void SendPeerReady(IPEndPoint to, IPEndPoint peer, ReadOnlySpan<byte> nonce)
    {
        int bufferSize = 1 + BeaconWireFormat.MaxPeerEndPointBytes + nonce.Length;
        byte[] packet = ArrayPool<byte>.Shared.Rent(bufferSize);

        packet[0] = (byte)BeaconPacketType.PeerReady;
        int payloadLength = BeaconWireFormat.WritePeerEndPoint(packet.AsSpan(1), peer);

        if (payloadLength == 0)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        nonce.CopyTo(packet.AsSpan(1 + payloadLength));

        _ = SendAndReturnAsync(packet, 1 + payloadLength + nonce.Length, to);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.JoinChallenge"/> carrying a cookie bound to <paramref name="to"/>,
    /// followed by the joiner's own nonce so it can tie the challenge to the join it sent.
    /// </summary>
    /// <param name="to">Joiner being challenged.</param>
    /// <param name="nonce">The nonce that joiner sent, echoed back.</param>
    private void SendJoinChallenge(IPEndPoint to, ReadOnlySpan<byte> nonce)
    {
        int size = 1 + BeaconWireFormat.CookieBytes + nonce.Length;
        byte[] packet = ArrayPool<byte>.Shared.Rent(size);

        packet[0] = (byte)BeaconPacketType.JoinChallenge;
        ComputeCookie(to, DateTime.UtcNow.Ticks / CookieTimeBucketTicks, packet.AsSpan(1, BeaconWireFormat.CookieBytes));
        nonce.CopyTo(packet.AsSpan(1 + BeaconWireFormat.CookieBytes));

        _ = SendAndReturnAsync(packet, size, to);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.HeartbeatAck"/> packet using the shared immutable buffer.
    /// </summary>
    private void SendHeartbeatAck(IPEndPoint to)
    {
        _ = _socket.SendAsync(HeartbeatAckPacket, HeartbeatAckPacket.Length, to);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.ServerAtCapacity"/> packet indicating the server's concurrent session limit has been reached.
    /// </summary>
    private void SendServerAtCapacity(IPEndPoint to)
    {
        _ = _socket.SendAsync(ServerAtCapacityPacket, ServerAtCapacityPacket.Length, to);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.SessionNotFound"/> packet indicating the requested session ID does not exist or has expired.
    /// </summary>
    private void SendSessionNotFound(IPEndPoint to, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length == 0)
        {
            _ = _socket.SendAsync(SessionNotFoundPacket, SessionNotFoundPacket.Length, to);
            return;
        }

        /* Carries the joiner's nonce back for the same reason PeerReady does: without it a rejection forged from
         * the server's address fails every join the client has in flight, not merely the one it names. */
        int size = 1 + nonce.Length;
        byte[] packet = ArrayPool<byte>.Shared.Rent(size);

        packet[0] = (byte)BeaconPacketType.SessionNotFound;
        nonce.CopyTo(packet.AsSpan(1));

        _ = SendAndReturnAsync(packet, size, to);
    }

    /// <summary>
    /// Sends a rented buffer to the target and returns it to <see cref="ArrayPool{T}.Shared"/> once the send completes.
    /// </summary>
    private async Task SendAndReturnAsync(byte[] packet, int length, IPEndPoint to)
    {
        try
        {
            await _socket.SendAsync(packet, length, to).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _evictionTimer.Dispose();
        _socket.Dispose();
        _cookieHmac.Dispose();
    }

    /// <summary>
    /// Request tally for one source address inside the current rate-limit window.
    /// </summary>
    private sealed class RequestRate
    {
        /// <summary>
        /// UTC ticks the current window began.
        /// </summary>
        internal long WindowStartTicks = DateTime.UtcNow.Ticks;

        /// <summary>
        /// Requests counted inside the current window.
        /// </summary>
        internal int Count;
    }
}
