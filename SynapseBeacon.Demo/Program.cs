using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SynapseBeacon.Client;
using SynapseBeacon.Server;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;

namespace SynapseBeacon.Demo;

/// <summary>
/// End-to-end demo of the SynapseBeacon rendezvous service.
/// <para>
/// The demo runs three processes inside a single console:
/// <list type="number">
/// <item>A <see cref="BeaconServer"/> on loopback that matches host/joiner pairs.</item>
/// <item>A host <see cref="SynapseManager"/> that requests a session and awaits a matched peer.</item>
/// <item>A joiner <see cref="SynapseManager"/> that registers with the session ID and connects to the host.</item>
/// </list>
/// On loopback there is no real NAT, so the hole-punch probe step is essentially a no-op — the
/// demo's purpose is to prove the rendezvous flow end-to-end: session creation, registration,
/// <c>PeerReady</c> dispatch on both sides, and a subsequent SynapseSocket handshake + reliable
/// message exchange.
/// </para>
/// </summary>
internal static class Program
{
    private const int BeaconPort = 47776;
    private const int HostPort = 47001;
    private const int JoinerPort = 47002;

    private static async Task Main()
    {
        Console.WriteLine("=== SynapseBeacon Demo ===");

        using CancellationTokenSource serverCts = new();

        /* --- Start the beacon server --- */
        using BeaconServer beaconServer = new(BeaconPort, log: text => Console.WriteLine(text));
        /* Hand the disposable through a helper method so the Task.Run closure does not capture a
         * `using` local in this scope (which would trip the "captured variable is disposed in
         * outer scope" analyser warning). */
        Task beaconServerTask = StartBeaconServerAsync(beaconServer, serverCts.Token);
        Console.WriteLine($"[beacon] listening on loopback:{BeaconPort}");

        IPEndPoint beaconEndPoint = new(IPAddress.Loopback, BeaconPort);

        /* --- Build host and joiner SynapseManager instances with FullCone NAT traversal --- */
        SynapseConfig hostConfig = new()
        {
            BindEndPoints = [new(IPAddress.Loopback, HostPort)],
            NatTraversal = { Mode = NatTraversalMode.FullCone },
        };

        SynapseConfig joinerConfig = new()
        {
            BindEndPoints = [new(IPAddress.Loopback, JoinerPort)],
            NatTraversal = { Mode = NatTraversalMode.FullCone },
        };

        await using SynapseManager host = new(hostConfig);
        await using SynapseManager joiner = new(joinerConfig);

        TaskCompletionSource<SynapseConnection> hostAcceptedPeer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> hostReceivedMessage = new(TaskCreationOptions.RunContinuationsAsynchronously);

        host.ConnectionEstablished += (connectionEventArgs) =>
        {
            Console.WriteLine($"[host] peer connected: {connectionEventArgs.Connection.RemoteEndPoint}");
            hostAcceptedPeer.TrySetResult(connectionEventArgs.Connection);
        };
        host.PacketReceived += (packetReceivedEventArgs) =>
        {
            string text = Encoding.UTF8.GetString(packetReceivedEventArgs.Payload);
            Console.WriteLine($"[host] received: {text}");
            hostReceivedMessage.TrySetResult(text);
        };

        joiner.ConnectionEstablished += (connectionEventArgs) =>
        {
            Console.WriteLine($"[joiner] connected to host: {connectionEventArgs.Connection.RemoteEndPoint}");
        };

        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await joiner.StartAsync(CancellationToken.None).ConfigureAwait(false);

        /* --- Create BeaconClients that piggyback on each SynapseManager's socket --- */
        BeaconClientConfig hostBeaconConfig = new(beaconEndPoint) { HeartbeatIntervalMilliseconds = 5_000 };
        BeaconClientConfig joinerBeaconConfig = new(beaconEndPoint) { HeartbeatIntervalMilliseconds = 5_000 };

        using BeaconClient hostBeacon = new(host, hostBeaconConfig);
        using BeaconClient joinerBeacon = new(joiner, joinerBeaconConfig);

        /* --- Host requests a session --- */
        using BeaconHostSession hostSession = await hostBeacon.HostAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"[host] session created: '{hostSession.SessionId}'");

        TaskCompletionSource<IPEndPoint> hostGotPeerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        hostSession.PeerReady += joinerEndPoint =>
        {
            Console.WriteLine($"[host] beacon matched joiner at {joinerEndPoint}");
            hostGotPeerReady.TrySetResult(joinerEndPoint);
        };

        /* --- Joiner registers with the shared session ID --- */
        IPEndPoint hostEndPointFromBeacon = await joinerBeacon.JoinAsync(hostSession.SessionId, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"[joiner] beacon matched host at {hostEndPointFromBeacon}");

        /* Wait for the host side to observe the match too. Only the joiner actually initiates the
         * outbound ConnectAsync, so the host's matched endpoint is discarded — the interesting
         * assertion here is simply that the host received a PeerReady at all. */
        _ = await hostGotPeerReady.Task.ConfigureAwait(false);

        /* --- Joiner initiates a SynapseSocket connection to the host (FullCone will punch) --- */
        SynapseConnection joinerConnection = await joiner.ConnectAsync(hostEndPointFromBeacon, CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAny(hostAcceptedPeer.Task, Task.Delay(2_000, CancellationToken.None))
            .ConfigureAwait(false);

        if (!hostAcceptedPeer.Task.IsCompletedSuccessfully)
        {
            Console.WriteLine("[error] host did not accept the joiner within 2 s — aborting demo.");
            return;
        }

        /* --- Exchange a reliable message to prove the punched channel works --- */
        byte[] payload = Encoding.UTF8.GetBytes("hello from joiner via beacon-assisted rendezvous");
        await joiner.SendAsync(joinerConnection, payload, isReliable: true, CancellationToken.None).ConfigureAwait(false);

        await Task.WhenAny(hostReceivedMessage.Task, Task.Delay(2_000)).ConfigureAwait(false);

        if (hostReceivedMessage.Task.IsCompletedSuccessfully)
            Console.WriteLine($"[demo] SUCCESS — host received: '{hostReceivedMessage.Task.Result}'");
        else
            Console.WriteLine("[demo] FAIL — host did not receive the joiner's message in time.");

        /* --- Teardown --- */
        await joiner.DisconnectAsync(joinerConnection, CancellationToken.None).ConfigureAwait(false);
        await hostSession.CloseAsync().ConfigureAwait(false);
        serverCts.Cancel();

        try
        {
            await beaconServerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* expected on shutdown */
        }

        Console.WriteLine("Demo finished.");
    }

    /// <summary>
    /// Runs <paramref name="server"/>'s receive loop on the thread pool. Taking the server as a
    /// parameter breaks the closure capture of the caller's <c>using</c> local, avoiding the
    /// "captured variable is disposed in outer scope" warning.
    /// </summary>
    private static Task StartBeaconServerAsync(BeaconServer server, CancellationToken cancellationToken)
    {
        return Task.Run(() => server.RunAsync(cancellationToken), cancellationToken);
    }
}
