using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SynapseBeacon.Wire;
using SynapseSocket.Core;
using SynapseSocket.Security;

namespace SynapseBeacon.Client;

/// <summary>
/// Client-side counterpart to <see cref="Server.BeaconServer"/>. Piggybacks on a
/// <see cref="SynapseManager"/>'s UDP socket via its extension API
/// (<see cref="SynapseManager.EnqueueRaw"/> + <see cref="SynapseManager.UnknownPacketReceived"/>)
/// so that the NAT mapping opened to the beacon server is the same mapping used for
/// peer-to-peer traffic after hole-punching.
/// <para>
/// Typical usage:
/// <list type="bullet">
/// <item><c>BeaconClient client = new(synapse, config);</c></item>
/// <item>Host: <c>using BeaconHostSession session = await client.HostAsync(ct);</c>, then share <c>session.SessionId</c> and listen for <c>PeerReady</c>.</item>
/// <item>Join: <c>IPEndPoint host = await client.JoinAsync(sessionId, ct);</c>, then call <c>synapse.Connect(host)</c> in FullCone mode.</item>
/// </list>
/// </para>
/// </summary>
public sealed class BeaconClient : IDisposable
{
    /// <summary>
    /// The <see cref="SynapseManager"/> whose socket carries all beacon traffic.
    /// </summary>
    private readonly SynapseManager _synapse;

    /// <summary>
    /// Client configuration, including the beacon server endpoint and timeout values.
    /// </summary>
    private readonly BeaconClientConfig _config;

    /// <summary>
    /// Cached beacon server endpoint from <see cref="_config"/> to avoid repeated property reads on the hot path.
    /// </summary>
    private readonly IPEndPoint _serverEndPoint;

    /// <summary>
    /// Pending <see cref="BeaconPacketType.RequestSession"/> call. Completed by the next
    /// <see cref="BeaconPacketType.SessionCreated"/> or <see cref="BeaconPacketType.ServerAtCapacity"/>.
    /// </summary>
    private TaskCompletionSource<uint>? _pendingSessionRequest;

    /// <summary>
    /// Pending <see cref="BeaconPacketType.JoinSession"/> calls keyed by session ID. Completed by
    /// the matching <see cref="BeaconPacketType.PeerReady"/>, or faulted by
    /// <see cref="BeaconPacketType.SessionNotFound"/> if the ID does not exist on the server.
    /// </summary>
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IPEndPoint>> _pendingRegistrations = new();
    /// <summary>
    /// Nonce sent with the in-flight <see cref="BeaconPacketType.RequestSession"/>, echoed by the reply.
    /// </summary>
    private readonly byte[] _sessionRequestNonce = new byte[BeaconWireFormat.NonceBytes];
    /// <summary>
    /// Nonce sent with each in-flight <see cref="BeaconPacketType.JoinSession"/>, keyed by session id.
    /// </summary>
    private readonly ConcurrentDictionary<uint, byte[]> _pendingRegistrationNonces = new();

    /// <summary>
    /// Active host sessions keyed by session ID. Used to route <see cref="BeaconPacketType.PeerReady"/>
    /// packets that arrive on the host side after initial matching.
    /// </summary>
    private readonly ConcurrentDictionary<uint, BeaconHostSession> _hostSessions = new();

    /// <summary>
    /// Non-zero once <see cref="Dispose"/> has been called. Guards against double-dispose.
    /// </summary>
    private int _isDisposed;

    /// <summary>
    /// Creates a new client bound to <paramref name="synapseManager"/>'s UDP socket.
    /// Throws <see cref="InvalidOperationException"/> when <paramref name="synapseManager"/> has
    /// <see cref="SynapseSocket.Core.Configuration.SecurityConfig.AllowUnknownPackets"/> set to false, because every beacon
    /// reply arrives as an unknown packet and the ingress path raises a violation instead of delivering it.
    /// </summary>
    /// <param name="synapseManager">
    /// A running <see cref="SynapseManager"/> whose <see cref="SynapseSocket.Core.Configuration.SecurityConfig.AllowUnknownPackets"/>
    /// is set to true. All beacon traffic is sent and received via this engine's socket.
    /// </param>
    /// <param name="config">Client configuration, including the rendezvous server endpoint.</param>
    public BeaconClient(SynapseManager synapseManager, BeaconClientConfig config)
    {
        _synapse = synapseManager ?? throw new ArgumentNullException(nameof(synapseManager));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _serverEndPoint = config.ServerEndPoint;

        if (!synapseManager.Config.Security.AllowUnknownPackets)
            throw new InvalidOperationException($"{nameof(BeaconClient)} requires {nameof(SynapseSocket.Core.Configuration.SecurityConfig.AllowUnknownPackets)} = true on the {nameof(SynapseManager)}.");

        _synapse.UnknownPacketReceived += OnUnknownPacketReceived;
    }

    /// <summary>
    /// Requests a new session from the beacon server, starts sending heartbeats, and returns a
    /// <see cref="BeaconHostSession"/> whose <see cref="BeaconHostSession.PeerReady"/> event
    /// fires whenever a joiner is matched.
    /// </summary>
    public async Task<BeaconHostSession> HostAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        TaskCompletionSource<uint> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (Interlocked.CompareExchange(ref _pendingSessionRequest, tcs, null) is not null)
            throw new InvalidOperationException("A session request is already in flight.");

        try
        {
            await SendRequestSessionAsync(cancellationToken).ConfigureAwait(false);

            uint sessionId = await AwaitWithTimeoutAsync(tcs.Task, cancellationToken).ConfigureAwait(false);

            BeaconHostSession session = new(this, sessionId, _config.HeartbeatIntervalMilliseconds);
            _hostSessions[sessionId] = session;
            return session;
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingSessionRequest, null, tcs);
        }
    }

    /// <summary>
    /// Registers as a joiner against an existing session and awaits the host's matched endpoint.
    /// Call <c>synapseManager.Connect(returnedEndPoint)</c> afterwards to initiate hole-punching.
    /// Throws <see cref="InvalidOperationException"/> when a registration for <paramref name="sessionId"/> is already in
    /// flight, or when the server reports the session as unknown or expired, and <see cref="TimeoutException"/> when no
    /// reply arrives within <see cref="BeaconClientConfig.ResponseTimeoutMilliseconds"/>.
    /// </summary>
    public async Task<IPEndPoint> JoinAsync(uint sessionId, CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        TaskCompletionSource<IPEndPoint> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRegistrations.TryAdd(sessionId, tcs))
            throw new InvalidOperationException($"A registration for session '{sessionId}' is already in flight.");

        try
        {
            await SendJoinSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return await AwaitWithTimeoutAsync(tcs.Task, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingRegistrations.TryRemove(sessionId, out _);
            _pendingRegistrationNonces.TryRemove(sessionId, out _);
        }
    }

    /// <summary>
    /// Called by <see cref="BeaconHostSession.CloseAsync"/> when a session is closed by the host.
    /// </summary>
    internal void DetachHostSession(uint sessionId)
    {
        _hostSessions.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Called by <see cref="BeaconHostSession"/> to send a heartbeat for its session ID.
    /// </summary>
    internal Task SendHeartbeatAsync(uint sessionId, CancellationToken cancellationToken)
    {
        return SendTypeAndSessionIdAsync(BeaconPacketType.Heartbeat, sessionId, cancellationToken);
    }

    /// <summary>
    /// Called by <see cref="BeaconHostSession"/> to close its session on the server.
    /// </summary>
    internal Task SendCloseSessionAsync(uint sessionId, CancellationToken cancellationToken)
    {
        return SendTypeAndSessionIdAsync(BeaconPacketType.CloseSession, sessionId, cancellationToken);
    }

    /// <summary>
    /// Routes an inbound unknown packet from <see cref="SynapseManager.UnknownPacketReceived"/>
    /// if it originated from the configured beacon server. Returns <see cref="FilterResult.Allowed"/>
    /// unconditionally. The beacon protocol does not police other unknown-packet sources.
    /// </summary>
    private FilterResult OnUnknownPacketReceived(IPEndPoint fromEndPoint, ArraySegment<byte> packet)
    {
        if (!fromEndPoint.Equals(_serverEndPoint))
            return FilterResult.Allowed;

        if (packet.Count < 1 || packet.Array is null)
            return FilterResult.Allowed;

        BeaconPacketType type = (BeaconPacketType)packet.Array[packet.Offset];
        ReadOnlySpan<byte> payload = new(packet.Array, packet.Offset + 1, packet.Count - 1);

        switch (type)
        {
            case BeaconPacketType.SessionCreated:
                HandleSessionCreated(payload);
                break;

            case BeaconPacketType.ServerAtCapacity:
                HandleServerAtCapacity();
                break;

            case BeaconPacketType.PeerReady:
                HandlePeerReady(payload);
                break;

            case BeaconPacketType.SessionNotFound:
                HandleSessionNotFound();
                break;

            case BeaconPacketType.HeartbeatAck:
                /* no-op, keep-alive acknowledgement */
                break;
        }

        return FilterResult.Allowed;
    }

    /// <summary>
    /// Completes the pending session request with the server-assigned session ID.
    /// </summary>
    private void HandleSessionCreated(ReadOnlySpan<byte> payload)
    {
        if (!BeaconWireFormat.TryReadSessionId(payload, out uint sessionId))
            return;

        /* Accepted only if it echoes the nonce we sent. The source address alone is not evidence: it is forgeable,
         * and this handler runs on packets the Synapse layer deliberately does not authenticate. */
        if (!EchoesNonce(payload[BeaconWireFormat.SessionIdBytes..], _sessionRequestNonce))
            return;

        TaskCompletionSource<uint>? tcs = Volatile.Read(ref _pendingSessionRequest);
        tcs?.TrySetResult(sessionId);
    }

    /// <summary>
    /// Fails the pending session request when the server's concurrent session cap has been reached.
    /// </summary>
    private void HandleServerAtCapacity()
    {
        TaskCompletionSource<uint>? tcs = Volatile.Read(ref _pendingSessionRequest);
        tcs?.TrySetException(new InvalidOperationException("Beacon server rejected session request: server at capacity."));
    }

    /// <summary>
    /// Fails all pending registrations whose session ID was not found or has expired on the server.
    /// </summary>
    private void HandleSessionNotFound()
    {
        foreach (KeyValuePair<uint, TaskCompletionSource<IPEndPoint>> kvp in _pendingRegistrations)
            kvp.Value.TrySetException(new InvalidOperationException($"Beacon server rejected registration: session '{kvp.Key}' not found or has expired."));
    }

    /// <summary>
    /// Routes a <c>PeerReady</c> packet. On the joiner side it completes the pending registration;
    /// on the host side it raises the session's <see cref="BeaconHostSession.PeerReady"/> event.
    /// Because the host does not know the joiner's session ID from the packet alone, any active
    /// host session is a valid target, only a joiner's registration carries a session ID we can
    /// match against. The server only sends <c>PeerReady</c> to the host in response to a specific
    /// joiner's <c>JoinSession</c>, so we dispatch to all host sessions; in practice at most one will
    /// be active per joiner endpoint.
    /// </summary>
    private void HandlePeerReady(ReadOnlySpan<byte> payload)
    {
        /* A forged PeerReady would otherwise point the joiner's hole-punch and subsequent Connect at whatever
         * endpoint the attacker names. The joiner's copy carries its own nonce back, so one that does not echo it
         * cannot complete a pending registration. */
        IPEndPoint? peer = BeaconWireFormat.TryReadPeerEndPoint(payload);
        if (peer is null)
            return;

        int endPointLength = BeaconWireFormat.MeasurePeerEndPoint(payload);

        /* Joiner path: complete a pending registration only when the reply echoes that registration's nonce. */
        foreach (KeyValuePair<uint, TaskCompletionSource<IPEndPoint>> kvp in _pendingRegistrations)
        {
            if (!RegistrationNonceMatches(kvp.Key, payload, endPointLength))
                continue;

            if (kvp.Value.TrySetResult(peer))
                return;
        }

        /* Host path: notify every active host session. Hosts expect multiple joiners. */
        foreach (KeyValuePair<uint, BeaconHostSession> kvp in _hostSessions)
        {
            kvp.Value.RaisePeerReady(peer);
        }
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.RequestSession"/> packet (single type byte, no payload).
    /// </summary>
    private Task SendRequestSessionAsync(CancellationToken cancellationToken)
    {
        RandomNumberGenerator.Fill(_sessionRequestNonce);

        int size = 1 + BeaconWireFormat.NonceBytes;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        buffer[0] = (byte)BeaconPacketType.RequestSession;
        _sessionRequestNonce.CopyTo(buffer, 1);

        return SendAndReturnAsync(buffer, size, cancellationToken);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.JoinSession"/> packet carrying the target session ID.
    /// </summary>
    private Task SendJoinSessionAsync(uint sessionId, CancellationToken cancellationToken)
    {
        byte[] nonce = new byte[BeaconWireFormat.NonceBytes];
        RandomNumberGenerator.Fill(nonce);
        _pendingRegistrationNonces[sessionId] = nonce;

        int size = 1 + BeaconWireFormat.SessionIdBytes + BeaconWireFormat.NonceBytes;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);

        BeaconWireFormat.WriteTypeAndSessionId(buffer.AsSpan(), BeaconPacketType.JoinSession, sessionId);
        nonce.CopyTo(buffer, 1 + BeaconWireFormat.SessionIdBytes);

        return SendAndReturnAsync(buffer, size, cancellationToken);
    }

    /// <summary>
    /// True when <paramref name="payload"/> ends with the nonce that was sent for <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="sessionId">Session whose pending registration nonce is checked.</param>
    /// <param name="payload">Received payload, beginning with the encoded peer endpoint.</param>
    /// <param name="endPointLength">Byte length of the encoded endpoint that precedes the nonce.</param>
    private bool RegistrationNonceMatches(uint sessionId, ReadOnlySpan<byte> payload, int endPointLength)
    {
        if (!_pendingRegistrationNonces.TryGetValue(sessionId, out byte[]? nonce))
            return false;

        return EchoesNonce(payload[endPointLength..], nonce);
    }

    /// <summary>
    /// True when <paramref name="trailing"/> begins with <paramref name="expected"/>. Used to confirm that a reply
    /// echoed back the nonce this client sent, which is what binds the reply to the request that asked for it.
    /// </summary>
    /// <param name="trailing">Bytes following the encoded endpoint in the received payload.</param>
    /// <param name="expected">The nonce that was sent with the request.</param>
    private static bool EchoesNonce(ReadOnlySpan<byte> trailing, ReadOnlySpan<byte> expected)
    {
        return trailing.Length >= expected.Length && trailing[..expected.Length].SequenceEqual(expected);
    }

    /// <summary>
    /// Sends a type byte followed by a 4-byte big-endian session ID payload.
    /// </summary>
    private Task SendTypeAndSessionIdAsync(BeaconPacketType type, uint sessionId, CancellationToken cancellationToken)
    {
        const int Size = 1 + BeaconWireFormat.SessionIdBytes;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Size);
        BeaconWireFormat.WriteTypeAndSessionId(buffer.AsSpan(), type, sessionId);
        return SendAndReturnAsync(buffer, Size, cancellationToken);
    }

    /// <summary>
    /// Sends a rented buffer to the beacon server and returns it to the pool once the send completes.
    /// </summary>
    private Task SendAndReturnAsync(byte[] buffer, int length, CancellationToken cancellationToken)
    {
        try
        {
            // SynapseManager.SendRaw is synchronous (the engine is poll-driven); the surrounding beacon API stays
            // async because it awaits server responses elsewhere.
            // Queued rather than sent inline: this runs on a heartbeat timer thread, and the engine send path keeps
            // unsynchronised per-engine state that only the poll thread may touch.
            _synapse.EnqueueRaw(_serverEndPoint, new(buffer, 0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Awaits <paramref name="task"/> with a timeout driven by
    /// <see cref="BeaconClientConfig.ResponseTimeoutMilliseconds"/>.
    /// </summary>
    private async Task<TResult> AwaitWithTimeoutAsync<TResult>(Task<TResult> task, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task timeoutTask = Task.Delay(_config.ResponseTimeoutMilliseconds, linkedCts.Token);
        Task completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);

        if (completed == task)
        {
            linkedCts.Cancel();
            return await task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"Beacon server did not respond within {_config.ResponseTimeoutMilliseconds} ms.");
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if <see cref="Dispose"/> has been called.
    /// </summary>
    private void EnsureNotDisposed()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            throw new ObjectDisposedException(nameof(BeaconClient));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        _synapse.UnknownPacketReceived -= OnUnknownPacketReceived;

        foreach (KeyValuePair<uint, BeaconHostSession> kvp in _hostSessions)
        {
            try
            {
                kvp.Value.Dispose();
            }
            catch
            {
                /* ignore */
            }
        }

        _hostSessions.Clear();

        foreach (KeyValuePair<uint, TaskCompletionSource<IPEndPoint>> kvp in _pendingRegistrations)
            kvp.Value.TrySetCanceled();

        _pendingRegistrations.Clear();
        _pendingRegistrationNonces.Clear();

        TaskCompletionSource<uint>? pendingRequest = Interlocked.Exchange(ref _pendingSessionRequest, null);
        pendingRequest?.TrySetCanceled();
    }
}
