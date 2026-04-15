using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SynapseBeacon.Client;

/// <summary>
/// Represents an active hosted session on a <see cref="Server.BeaconServer"/>.
/// Returned from <see cref="BeaconClient.HostAsync"/>.
/// <para>
/// While the session is alive, the client periodically sends heartbeat packets so the
/// server will not evict the session. The <see cref="PeerReady"/> event fires whenever a
/// joiner registers and the server matches the pair — the caller should then initiate a
/// NAT hole-punch to the joiner's endpoint using its <c>SynapseManager</c>.
/// </para>
/// </summary>
public sealed class BeaconHostSession : IAsyncDisposable, IDisposable
{
    /// <summary>Raised whenever the rendezvous server matches a joiner with this host.</summary>
    public event Action<IPEndPoint>? PeerReady;

    /// <summary>Server-assigned session ID. Share this out-of-band with joiners.</summary>
    public uint SessionId { get; }

    private readonly BeaconClient _client;
    private readonly CancellationTokenSource _heartbeatCts;
    private readonly Task _heartbeatTask;
    private int _isClosed;

    internal BeaconHostSession(BeaconClient client, uint sessionId, int heartbeatIntervalMilliseconds)
    {
        _client = client;
        SessionId = sessionId;
        _heartbeatCts = new();
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(heartbeatIntervalMilliseconds, _heartbeatCts.Token));
    }

    /// <summary>
    /// Invoked by <see cref="BeaconClient"/> when a <c>PeerReady</c> packet arrives for this session.
    /// </summary>
    internal void RaisePeerReady(IPEndPoint joiner)
    {
        PeerReady?.Invoke(joiner);
    }

    /// <summary>
    /// Closes this session on the server and stops sending heartbeats.
    /// Subsequent joiners with the session ID will be rejected with <c>SessionFull</c>.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isClosed, 1) != 0)
            return;

        try
        {
            _heartbeatCts.Cancel();
        }
        catch
        {
            /* ignore */
        }

        try
        {
            await _client.SendCloseSessionAsync(SessionId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            /* best-effort close */
        }

        _client.DetachHostSession(SessionId);

        try
        {
            await _heartbeatTask.ConfigureAwait(false);
        }
        catch
        {
            /* ignore */
        }

        _heartbeatCts.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            CloseAsync().GetAwaiter().GetResult();
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>
    /// Background loop that sends <c>Heartbeat</c> packets at the configured interval until the
    /// session is closed.
    /// </summary>
    private async Task HeartbeatLoopAsync(int intervalMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(intervalMilliseconds, cancellationToken).ConfigureAwait(false);

                try
                {
                    await _client.SendHeartbeatAsync(SessionId, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    /* best-effort heartbeat */
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* normal exit */
        }
    }
}
