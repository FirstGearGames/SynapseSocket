using System;
using System.Threading;
using System.Threading.Tasks;

namespace SynapseSocket.Core;

/// <summary>
/// Represents an active NAT rendezvous session created by the host via <see cref="SynapseManager.HostViaNatServerAsync"/>.
/// <para>
/// The session remains open on the server until <see cref="CloseAsync"/> is called or the session times out.
/// While open, any peer that calls <see cref="SynapseManager.JoinViaNatServerAsync"/> with <see cref="SessionId"/>
/// will be matched to this host and a hole-punched connection will be initiated automatically.
/// Successful connections surface through <see cref="SynapseManager.ConnectionEstablished"/> as usual.
/// </para>
/// </summary>
public sealed class NatHostSession : IAsyncDisposable
{
    private readonly SynapseManager _manager;
    private readonly CancellationTokenSource _heartbeatCts;
    private int _closed;

    /// <summary>
    /// The session ID assigned by the rendezvous server.
    /// Share this out-of-band with peers so they can call <see cref="SynapseManager.JoinViaNatServerAsync"/>.
    /// </summary>
    public string SessionId { get; }

    internal NatHostSession(string sessionId, SynapseManager manager, CancellationTokenSource heartbeatCts)
    {
        SessionId = sessionId;
        _manager = manager;
        _heartbeatCts = heartbeatCts;
    }

    /// <summary>
    /// Closes the session on the rendezvous server, preventing any further peers from joining.
    /// Peers that are already connected are not affected.
    /// Safe to call multiple times.
    /// </summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return Task.CompletedTask;

        _heartbeatCts.Cancel();
        return _manager.CloseNatHostSessionAsync(SessionId, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(CloseAsync());
}
