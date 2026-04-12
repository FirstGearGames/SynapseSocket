using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Core;

/// <summary>
/// NAT traversal support for <see cref="SynapseManager"/>.
/// <para>
/// <b>FullCone mode:</b> both peers already know each other's external endpoint.
/// <see cref="ConnectAsync"/> first waits <see cref="FullConeNatConfig.DirectAttemptMilliseconds"/> for a direct
/// connection; if the connection is still pending it falls back to timed probe bursts that open NAT mappings
/// on both sides simultaneously.
/// </para>
/// <para>
/// <b>Server mode (host):</b> call <see cref="HostViaNatServerAsync"/> to request a server-assigned session ID.
/// A <see cref="NatHostSession"/> is returned immediately after the server assigns the ID.
/// Share <see cref="NatHostSession.SessionId"/> with peers out-of-band; each peer that calls
/// <see cref="JoinViaNatServerAsync"/> triggers a hole-punch, with the resulting connection surfacing
/// through <see cref="ConnectionEstablished"/>. Any number of peers may join the same session.
/// Call <see cref="NatHostSession.CloseAsync"/> when done accepting.
/// </para>
/// <para>
/// <b>Server mode (join):</b> call <see cref="JoinViaNatServerAsync"/> with the session ID shared by the host.
/// The server responds with the host's external endpoint and hole-punching proceeds automatically.
/// </para>
/// </summary>
public sealed partial class SynapseManager
{
    /// <summary>
    /// In host mode: invoked for every incoming <see cref="OnNatPeerReady"/> call, once per joining peer.
    /// Null when not hosting.
    /// </summary>
    private Action<IPEndPoint>? _natHostPeerHandler;
    /// <summary>
    /// In join mode: resolved once when <see cref="OnNatPeerReady"/> fires.
    /// Null when not joining.
    /// </summary>
    private TaskCompletionSource<IPEndPoint>? _natJoinSource;
    /// <summary>
    /// Resolved by <see cref="OnNatSessionCreated"/> during <see cref="HostViaNatServerAsync"/>.
    /// </summary>
    private TaskCompletionSource<uint>? _natSessionSource;
    /// <summary>
    /// Guards <see cref="_natHostPeerHandler"/> and <see cref="_natJoinSource"/> assignment.
    /// </summary>
    private readonly object _natRendezvousLock = new();

    // -------------------------------------------------------------------------
    // Public API — Server mode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Requests a session ID from the configured NAT rendezvous server and returns a
    /// <see cref="NatHostSession"/> that accepts any number of joining peers.
    /// <para>
    /// Share <see cref="NatHostSession.SessionId"/> out-of-band with peers. Each peer that calls
    /// <see cref="JoinViaNatServerAsync"/> is matched and their connection surfaces through
    /// <see cref="ConnectionEstablished"/>. Call <see cref="NatHostSession.CloseAsync"/> when done accepting.
    /// </para>
    /// Requires <see cref="NatTraversalConfig.Mode"/> == <see cref="NatTraversalMode.Server"/> and
    /// <see cref="ServerNatConfig.ServerEndPoint"/> set.
    /// </summary>
    public async Task<NatHostSession> HostViaNatServerAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStarted || _sender is null)
            throw new InvalidOperationException("Engine not started.");

        if (Config.NatTraversal.Mode != NatTraversalMode.Server)
            throw new InvalidOperationException("NatTraversal.Mode must be Server to call HostViaNatServerAsync.");

        if (Config.NatTraversal.Server.ServerEndPoint is null)
            throw new InvalidOperationException("NatTraversal.Server.ServerEndPoint must be set.");

        IPEndPoint serverEndPoint = Config.NatTraversal.Server.ServerEndPoint;
        TaskCompletionSource<uint> sessionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource?.Token ?? CancellationToken.None);

        lock (_natRendezvousLock)
        {
            if (_natHostPeerHandler is not null || _natJoinSource is not null || _natSessionSource is not null)
                throw new InvalidOperationException("A NAT rendezvous is already in progress.");

            _natSessionSource = sessionSource;
        }

        try
        {
            await _sender.SendNatRequestSessionAsync(serverEndPoint, heartbeatCts.Token).ConfigureAwait(false);

            Task<uint> sessionTask = sessionSource.Task;
            Task timeoutTask = Task.Delay((int)Config.NatTraversal.Server.RegistrationTimeoutMilliseconds, heartbeatCts.Token);

            if (await Task.WhenAny(sessionTask, timeoutTask).ConfigureAwait(false) != sessionTask)
            {
                lock (_natRendezvousLock)
                    _natSessionSource = null;

                heartbeatCts.Dispose();
                throw new TimeoutException("NAT rendezvous timed out: server did not assign a session ID.");
            }

            uint sessionId = await sessionTask.ConfigureAwait(false);
            CancellationToken heartbeatToken = heartbeatCts.Token;

            // Assign the peer handler only after we own a valid session. NatPeerReady cannot arrive
            // before the session ID is received (the server will not match joiners against a session
            // that does not yet exist), so pre-assignment is unnecessary and would leave the lambda
            // holding a heartbeatCts that could be disposed on an error path.
            lock (_natRendezvousLock)
                _natHostPeerHandler = peerEndPoint => _ = Task.Run(() => ConnectAfterRendezvousAsync(peerEndPoint, heartbeatToken));

            NatHostSession session = new(sessionId, this, heartbeatCts);
            _ = Task.Run(() => NatServerHeartbeatAsync(sessionId, heartbeatToken));
            return session;
        }
        catch
        {
            lock (_natRendezvousLock)
            {
                _natSessionSource = null;
                _natHostPeerHandler = null;
            }

            heartbeatCts.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Registers with the configured NAT rendezvous server using the session ID provided by the host,
    /// waits for the host's external endpoint, then initiates a hole-punched connection.
    /// Requires <see cref="NatTraversalConfig.Mode"/> == <see cref="NatTraversalMode.Server"/> and
    /// <see cref="ServerNatConfig.ServerEndPoint"/> set.
    /// </summary>
    public async Task<SynapseConnection> JoinViaNatServerAsync(uint sessionId, CancellationToken cancellationToken = default)
    {
        if (!_isStarted || _sender is null)
            throw new InvalidOperationException("Engine not started.");

        if (Config.NatTraversal.Mode != NatTraversalMode.Server)
            throw new InvalidOperationException("NatTraversal.Mode must be Server to call JoinViaNatServerAsync.");

        if (Config.NatTraversal.Server.ServerEndPoint is null)
            throw new InvalidOperationException("NatTraversal.Server.ServerEndPoint must be set.");

        TaskCompletionSource<IPEndPoint> joinSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_natRendezvousLock)
        {
            if (_natHostPeerHandler is not null || _natJoinSource is not null)
                throw new InvalidOperationException("A NAT rendezvous is already in progress.");

            _natJoinSource = joinSource;
        }

        _ = Task.Run(() => NatServerJoinAsync(sessionId, cancellationToken), cancellationToken);

        Task<IPEndPoint> peerTask = joinSource.Task;
        Task timeoutTask = Task.Delay((int)Config.NatTraversal.Server.RegistrationTimeoutMilliseconds, cancellationToken);

        if (await Task.WhenAny(peerTask, timeoutTask).ConfigureAwait(false) != peerTask)
        {
            lock (_natRendezvousLock)
                _natJoinSource = null;

            throw new TimeoutException("NAT rendezvous timed out: no peer registered within the allotted window.");
        }

        IPEndPoint peerEndPoint = await peerTask.ConfigureAwait(false);
        return await ConnectAfterRendezvousAsync(peerEndPoint, cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Internal API — called by NatHostSession
    // -------------------------------------------------------------------------

    internal async Task CloseNatHostSessionAsync(uint sessionId, CancellationToken cancellationToken)
    {
        lock (_natRendezvousLock)
            _natHostPeerHandler = null;

        if (_sender is not null && Config.NatTraversal.Server.ServerEndPoint is not null)
            await _sender.SendNatCloseSessionAsync(Config.NatTraversal.Server.ServerEndPoint, sessionId, cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Internal callbacks — wired by StartAsync
    // -------------------------------------------------------------------------

    /// <summary>
    /// In host mode: triggers a punch-and-connect to the new joining peer.
    /// In join mode: resolves the awaited peer task and clears the join source.
    /// </summary>
    internal void OnNatPeerReady(IPEndPoint peerEndPoint)
    {
        Action<IPEndPoint>? hostHandler;
        TaskCompletionSource<IPEndPoint>? joinSource;

        lock (_natRendezvousLock)
        {
            hostHandler = _natHostPeerHandler;
            joinSource = _natJoinSource;
            _natJoinSource = null;
        }

        hostHandler?.Invoke(peerEndPoint);
        joinSource?.TrySetResult(peerEndPoint);
    }

    /// <summary>
    /// Faults the active join rendezvous because the server reported the session is full or was not found.
    /// </summary>
    internal void OnNatSessionFull()
    {
        TaskCompletionSource<IPEndPoint>? joinSource;

        lock (_natRendezvousLock)
        {
            joinSource = _natJoinSource;
            _natJoinSource = null;
        }

        joinSource?.TrySetException(new InvalidOperationException("NAT rendezvous failed: session is full or the session ID was not found."));
    }

    /// <summary>
    /// Resolves the pending session-creation wait with the server-assigned session ID.
    /// </summary>
    internal void OnNatSessionCreated(uint sessionId)
    {
        TaskCompletionSource<uint>? source;

        lock (_natRendezvousLock)
        {
            source = _natSessionSource;
            _natSessionSource = null;
        }

        source?.TrySetResult(sessionId);
    }

    /// <summary>
    /// Faults the pending session-creation wait because the server has no available session slots.
    /// </summary>
    internal void OnNatSessionUnavailable()
    {
        TaskCompletionSource<uint>? source;

        lock (_natRendezvousLock)
        {
            source = _natSessionSource;
            _natSessionSource = null;
        }

        source?.TrySetException(new InvalidOperationException("NAT server has no available session slots."));
    }

    // -------------------------------------------------------------------------
    // Background tasks
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends periodic heartbeats to the rendezvous server to keep the host session alive until closed.
    /// </summary>
    private async Task NatServerHeartbeatAsync(uint sessionId, CancellationToken cancellationToken)
    {
        IPEndPoint serverEndPoint = Config.NatTraversal.Server.ServerEndPoint!;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay((int)Config.NatTraversal.Server.HeartbeatIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                await _sender!.SendNatHeartbeatAsync(serverEndPoint, sessionId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (Exception unexpectedException)
        {
            UnhandledException?.Invoke(unexpectedException);
        }
    }

    /// <summary>
    /// Registers with the rendezvous server as the joining peer and sends periodic heartbeats until the host is matched.
    /// </summary>
    private async Task NatServerJoinAsync(uint sessionId, CancellationToken cancellationToken)
    {
        IPEndPoint serverEndPoint = Config.NatTraversal.Server.ServerEndPoint!;

        try
        {
            await _sender!.SendNatRegisterAsync(serverEndPoint, sessionId, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested && _natJoinSource is not null)
            {
                await Task.Delay((int)Config.NatTraversal.Server.HeartbeatIntervalMilliseconds, cancellationToken).ConfigureAwait(false);

                if (_natJoinSource is not null)
                    await _sender!.SendNatHeartbeatAsync(serverEndPoint, sessionId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (Exception unexpectedException)
        {
            UnhandledException?.Invoke(unexpectedException);
        }
    }

    /// <summary>
    /// Shared post-rendezvous logic: creates the connection record, sends an initial handshake,
    /// and starts the NAT punch task.
    /// </summary>
    private async Task<SynapseConnection> ConnectAfterRendezvousAsync(IPEndPoint peerEndPoint, CancellationToken cancellationToken)
    {
        ulong signature = Security.ComputeSignature(peerEndPoint, ReadOnlySpan<byte>.Empty);
        SynapseConnection synapseConnection = Connections.GetOrAdd(peerEndPoint, signature, (endPoint, remoteSignature) => new(endPoint, remoteSignature));
        await _sender!.SendHandshakeAsync(peerEndPoint, cancellationToken).ConfigureAwait(false);
        _ = Task.Run(() => NatPunchAsync(synapseConnection, peerEndPoint, cancellationToken), cancellationToken);
        return synapseConnection;
    }

    /// <summary>
    /// Sends timed probe bursts to open NAT mappings, followed by a handshake on each attempt.
    /// Raises <see cref="ConnectionFailed"/> with <see cref="ConnectionRejectedReason.NatTraversalFailed"/>
    /// if all attempts are exhausted without establishing a connection.
    /// </summary>
    private async Task NatPunchAsync(SynapseConnection synapseConnection, IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        try
        {
            // FullCone only: give the direct handshake a head start.
            if (Config.NatTraversal.Mode == NatTraversalMode.FullCone)
            {
                await Task.Delay((int)Config.NatTraversal.FullCone.DirectAttemptMilliseconds, cancellationToken).ConfigureAwait(false);

                if (synapseConnection.State != ConnectionState.Pending)
                    return;
            }

            for (uint attempt = 0; attempt < Config.NatTraversal.MaximumAttempts; attempt++)
            {
                if (synapseConnection.State != ConnectionState.Pending)
                    return;

                for (uint probe = 0; probe < Config.NatTraversal.ProbeCount; probe++)
                    await _sender!.SendNatProbeAsync(endPoint, cancellationToken).ConfigureAwait(false);

                await _sender!.SendHandshakeAsync(endPoint, cancellationToken).ConfigureAwait(false);

                if (attempt + 1 < Config.NatTraversal.MaximumAttempts)
                    await Task.Delay((int)Config.NatTraversal.IntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            if (synapseConnection.State != ConnectionState.Connected)
                RaiseConnectionFailed(endPoint, ConnectionRejectedReason.NatTraversalFailed, null);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (Exception unexpectedException)
        {
            UnhandledException?.Invoke(unexpectedException);
        }
    }
}