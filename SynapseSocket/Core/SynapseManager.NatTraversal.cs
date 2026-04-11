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
/// <b>FullCone mode:</b> both peers already know each other''s external endpoint.
/// <see cref="ConnectAsync"/> first waits <see cref="FullConeNatConfig.DirectAttemptMilliseconds"/> for a direct connection; if the connection is still pending it falls back to timed probe bursts that open NAT mappings on both sides simultaneously.
/// </para>
/// <para>
/// <b>Server mode:</b> call <see cref="ConnectViaNatServerAsync"/> instead of <see cref="ConnectAsync"/>.
/// Both peers register with the same <see cref="ServerNatConfig.ServerEndPoint"/> using the same <see cref="ServerNatConfig.SessionId"/>; once the server reports the peer''s endpoint the engine falls through to the same probe-burst logic used by FullCone.
/// </para>
/// </summary>
public sealed partial class SynapseManager
{
    /// <summary>
    /// Completion source set before <see cref="ConnectViaNatServerAsync"/> returns and resolved by <see cref="OnNatPeerReady"/>.
    /// </summary>
    private TaskCompletionSource<IPEndPoint>? _natPeerSource;
    /// <summary>
    /// Guards <see cref="_natPeerSource"/> assignment to prevent concurrent callers from racing.
    /// </summary>
    private readonly object _natPeerSourceLock = new();

    // -------------------------------------------------------------------------
    // Public API — Server mode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers with the configured NAT rendezvous server, waits for the peer''s external endpoint, then initiates a hole-punched connection.
    /// Requires <see cref="NatTraversalConfig.Mode"/> == <see cref="NatTraversalMode.Server"/>, <see cref="ServerNatConfig.ServerEndPoint"/> set, and a shared <see cref="ServerNatConfig.SessionId"/>.
    /// </summary>
    public async Task<SynapseConnection> ConnectViaNatServerAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStarted || _sender is null)
            throw new InvalidOperationException("Engine not started.");

        if (Config.NatTraversal.Mode != NatTraversalMode.Server)
            throw new InvalidOperationException("NatTraversal.Mode must be Server to call ConnectViaNatServerAsync.");

        if (Config.NatTraversal.Server.ServerEndPoint is null)
            throw new InvalidOperationException("NatTraversal.Server.ServerEndPoint must be set.");

        lock (_natPeerSourceLock)
        {
            if (_natPeerSource is not null && !_natPeerSource.Task.IsCompleted)
                throw new InvalidOperationException("A NAT rendezvous is already in progress.");

            _natPeerSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Background task: register + send heartbeats until a peer is matched.
        _ = Task.Run(() => NatServerRegistrationAsync(cancellationToken), cancellationToken);

        // Wait for the server to deliver the peer's endpoint (netstandard2.1: no WaitAsync).
        Task<IPEndPoint> peerTask = _natPeerSource.Task;
        Task timeoutTask = Task.Delay((int)Config.NatTraversal.Server.RegistrationTimeoutMilliseconds, cancellationToken);
        Task completedTask = await Task.WhenAny(peerTask, timeoutTask).ConfigureAwait(false);

        if (completedTask != peerTask)
        {
            _natPeerSource?.TrySetCanceled();
            throw new TimeoutException("NAT rendezvous timed out: no peer registered within the allotted window.");
        }

        IPEndPoint peerEndPoint = await peerTask.ConfigureAwait(false);

        // From here the flow is identical to ConnectAsync + FullCone hole-punch.
        ulong signature = Security.ComputeSignature(peerEndPoint, ReadOnlySpan<byte>.Empty);
        SynapseConnection synapseConnection = Connections.GetOrAdd(peerEndPoint, signature, (endPoint, remoteSignature) => new(endPoint, remoteSignature));
        await _sender.SendHandshakeAsync(peerEndPoint, cancellationToken).ConfigureAwait(false);
        _ = Task.Run(() => NatPunchAsync(synapseConnection, peerEndPoint, cancellationToken), cancellationToken);
        return synapseConnection;
    }

    // -------------------------------------------------------------------------
    // Internal callbacks — wired by StartAsync
    // -------------------------------------------------------------------------

    /// <summary>
    /// Completes the pending NAT rendezvous with the peer's external endpoint.
    /// </summary>
    internal void OnNatPeerReady(IPEndPoint peerEndPoint)
    {
        _natPeerSource?.TrySetResult(peerEndPoint);
    }

    /// <summary>
    /// Faults the pending NAT rendezvous because the server reported the session is full.
    /// </summary>
    internal void OnNatSessionFull()
    {
        _natPeerSource?.TrySetException(
            new InvalidOperationException("NAT rendezvous session is already full; both peer slots are taken."));
    }

    // -------------------------------------------------------------------------
    // Background tasks
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers with the NAT rendezvous server and sends periodic heartbeats until the peer is matched or the operation is cancelled.
    /// </summary>
    private async Task NatServerRegistrationAsync(CancellationToken cancellationToken)
    {
        IPEndPoint serverEndPoint = Config.NatTraversal.Server.ServerEndPoint!;
        string sessionId = Config.NatTraversal.Server.SessionId;

        try
        {
            await _sender!.SendNatRegisterAsync(serverEndPoint, sessionId, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested &&
                   _natPeerSource is not null && !_natPeerSource.Task.IsCompleted)
            {
                await Task.Delay((int)Config.NatTraversal.Server.HeartbeatIntervalMilliseconds, cancellationToken).ConfigureAwait(false);

                if (_natPeerSource is not null && !_natPeerSource.Task.IsCompleted)
                    await _sender!.SendNatHeartbeatAsync(serverEndPoint, sessionId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (Exception unexpectedException) { UnhandledException?.Invoke(unexpectedException); }
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
        catch (Exception unexpectedException) { UnhandledException?.Invoke(unexpectedException); }
    }
}