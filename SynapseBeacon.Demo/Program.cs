using System;
using System.Collections.Generic;
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
/// End-to-end demo of the SynapseBeacon rendezvous service with multiple joiners.
/// <para>
/// The demo runs inside a single console:
/// <list type="number">
/// <item>A <see cref="BeaconServer"/> on loopback that matches host/joiner pairs.</item>
/// <item>A host <see cref="SynapseManager"/> whose <see cref="BeaconClient"/> creates a session and accepts multiple joiners.</item>
/// <item>Two joiner <see cref="SynapseManager"/> instances that join the same session ID via <see cref="BeaconClient"/> and connect to the host.</item>
/// </list>
/// Once the host has accepted <see cref="JoinerCount"/> peers it closes the beacon session, so
/// any further joiners that attempt to join with that session ID will be silently dropped by the server.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// UDP port the beacon rendezvous server listens on for this demo.
    /// </summary>
    private const int BeaconPort = 47776;

    /// <summary>
    /// UDP port the host <see cref="SynapseSocket.Core.SynapseManager"/> binds to.
    /// </summary>
    private const int HostPort = 47001;

    /// <summary>
    /// First UDP port assigned to joiner instances. Each joiner gets <c>JoinerBasePort + index</c>.
    /// </summary>
    private const int JoinerBasePort = 47002;

    /// <summary>
    /// Number of joiner clients to spin up. The host closes its session once all have connected.
    /// </summary>
    private const int JoinerCount = 2;

    private static async Task Main()
    {
        Console.WriteLine("=== SynapseBeacon Demo ===");

        using CancellationTokenSource serverCancellationTokenSource = new();

        /* --- Start the beacon server --- */
        /* The BeaconServer is a lightweight UDP rendezvous server. Hosts create sessions here
         * and share the session ID out-of-band with joiners. Each joiner sends the session ID
         * back to the server, which then responds to both sides with each other's external
         * endpoint so NAT hole-punching can proceed directly between the two peers. */
        using BeaconServer beaconServer = new(BeaconPort, BeaconServer.DefaultSessionTimeoutMilliseconds, BeaconServer.UnlimitedConcurrentSessions, text => Console.WriteLine(text));

        /* Hand the disposable through a helper method so the Task.Run closure does not capture a
         * `using` local in this scope (which would trip the "captured variable is disposed in
         * outer scope" analyser warning). */
        Task beaconServerTask = StartBeaconServerAsync(beaconServer, serverCancellationTokenSource.Token);
        Console.WriteLine($"[beacon] listening on loopback:{BeaconPort}");

        IPEndPoint beaconServerEndPoint = new(IPAddress.Loopback, BeaconPort);

        /* --- Build the host SynapseManager --- */
        /* FullCone NAT mode is required for hole-punching: the socket must accept packets from
         * any remote endpoint, not only peers it has already sent to. */
        SynapseConfig hostSynapseConfig = new()
        {
            BindEndPoints = [new(IPAddress.Loopback, HostPort)],
            NatTraversal = { Mode = NatTraversalMode.FullCone },
            Security = { AllowUnknownPackets = true },
        };

        await using SynapseManager hostSynapseManager = new(hostSynapseConfig);

        int acceptedPeerCount = 0;

        /* RunContinuationsAsynchronously ensures the TrySetResult callback does not execute
         * synchronously on the thread pool thread that increments the count, avoiding
         * re-entrancy into the connection event handler. */
        TaskCompletionSource allJoinersConnectedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        hostSynapseManager.ConnectionEstablished += connectionEventArgs =>
        {
            int newAcceptedPeerCount = Interlocked.Increment(ref acceptedPeerCount);
            Console.WriteLine($"[host] peer #{newAcceptedPeerCount} connected: {connectionEventArgs.Connection.RemoteEndPoint}");

            if (newAcceptedPeerCount >= JoinerCount)
                allJoinersConnectedSource.TrySetResult();
        };

        hostSynapseManager.PacketReceived += packetReceivedEventArgs =>
        {
            string payloadText = Encoding.UTF8.GetString(packetReceivedEventArgs.Payload);
            Console.WriteLine($"[host] received from {packetReceivedEventArgs.Connection.RemoteEndPoint}: {payloadText}");
        };

        await hostSynapseManager.StartAsync(CancellationToken.None).ConfigureAwait(false);

        /* --- Create the host beacon client --- */
        /* The BeaconClient piggybacks on the host's SynapseManager UDP socket so the same NAT
         * mapping that was opened toward the beacon server is reused for peer-to-peer traffic
         * after hole-punching — no extra ports or firewall rules needed. */
        BeaconClientConfig hostBeaconClientConfig = new(beaconServerEndPoint) { HeartbeatIntervalMilliseconds = 5_000 };
        using BeaconClient hostBeaconClient = new(hostSynapseManager, hostBeaconClientConfig);

        /* --- Host requests a session --- */
        /* HostAsync sends a RequestSession packet to the beacon server and awaits a SessionCreated
         * response containing the server-assigned session ID. The host shares that ID out-of-band
         * (here, inline) with each joiner. The PeerReady event fires whenever the server has
         * matched an incoming joiner and endpoint exchange is complete. */
        using BeaconHostSession hostBeaconSession = await hostBeaconClient.HostAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"[host] session created: '{hostBeaconSession.SessionId}' — accepting up to {JoinerCount} joiners");

        hostBeaconSession.PeerReady += joinerEndPoint =>
            Console.WriteLine($"[host] beacon matched joiner at {joinerEndPoint}");

        /* --- Spin up joiners --- */
        List<SynapseManager> joinerSynapseManagers = new(JoinerCount);
        List<BeaconClient> joinerBeaconClients = new(JoinerCount);
        List<SynapseConnection> joinerSynapseConnections = new(JoinerCount);

        try
        {
            for (int i = 0; i < JoinerCount; i++)
            {
                int joinerIndex = i + 1;
                int joinerPort = JoinerBasePort + i;

                SynapseConfig joinerSynapseConfig = new()
                {
                    BindEndPoints = [new(IPAddress.Loopback, joinerPort)],
                    NatTraversal = { Mode = NatTraversalMode.FullCone },
                    Security = { AllowUnknownPackets = true },
                };

                SynapseManager joinerSynapseManager = new(joinerSynapseConfig);
                joinerSynapseManagers.Add(joinerSynapseManager);

                joinerSynapseManager.ConnectionEstablished += connectionEventArgs =>
                    Console.WriteLine($"[joiner {joinerIndex}] connected to host: {connectionEventArgs.Connection.RemoteEndPoint}");

                await joinerSynapseManager.StartAsync(CancellationToken.None).ConfigureAwait(false);

                /* Each joiner also piggybacks its BeaconClient on its own SynapseManager socket
                 * for the same NAT-mapping reuse benefit described above for the host. */
                BeaconClientConfig joinerBeaconClientConfig = new(beaconServerEndPoint) { HeartbeatIntervalMilliseconds = 5_000 };
                BeaconClient joinerBeaconClient = new(joinerSynapseManager, joinerBeaconClientConfig);
                joinerBeaconClients.Add(joinerBeaconClient);

                /* JoinAsync sends the shared session ID to the beacon server and blocks until the
                 * server responds with the host's external endpoint, at which point ConnectAsync
                 * can initiate hole-punching directly to that endpoint. */
                IPEndPoint hostEndPointFromBeacon = await joinerBeaconClient.JoinAsync(hostBeaconSession.SessionId, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"[joiner {joinerIndex}] beacon matched host at {hostEndPointFromBeacon}");

                SynapseConnection joinerSynapseConnection = await joinerSynapseManager.ConnectAsync(hostEndPointFromBeacon, CancellationToken.None).ConfigureAwait(false);
                joinerSynapseConnections.Add(joinerSynapseConnection);

                byte[] greetingPayload = Encoding.UTF8.GetBytes($"hello from joiner {joinerIndex}");
                await joinerSynapseManager.SendAsync(joinerSynapseConnection, greetingPayload, isReliable: true, CancellationToken.None).ConfigureAwait(false);
            }

            /* --- Wait for the host to observe all JoinerCount peer connections --- */
            Task completedTask = await Task.WhenAny(allJoinersConnectedSource.Task, Task.Delay(5_000)).ConfigureAwait(false);

            if (completedTask != allJoinersConnectedSource.Task)
            {
                Console.WriteLine($"[error] host only accepted {acceptedPeerCount}/{JoinerCount} joiners within 5s — aborting demo.");
                return;
            }

            Console.WriteLine($"[host] reached joiner cap ({JoinerCount}) — closing session so no further joiners can match.");
            await hostBeaconSession.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"[host] session '{hostBeaconSession.SessionId}' closed. Later JoinAsync calls with this ID will be silently dropped by the beacon server.");

            /* Give any in-flight greeting messages a moment to land before teardown. */
            await Task.Delay(250).ConfigureAwait(false);

            Console.WriteLine("[demo] SUCCESS — all joiners connected and exchanged a message before session close.");
        }
        finally
        {
            /* --- Teardown joiners --- */
            for (int i = 0; i < joinerSynapseConnections.Count; i++)
                await joinerSynapseManagers[i].DisconnectAsync(joinerSynapseConnections[i], CancellationToken.None).ConfigureAwait(false);

            foreach (BeaconClient joinerBeaconClient in joinerBeaconClients)
                joinerBeaconClient.Dispose();

            foreach (SynapseManager joinerSynapseManager in joinerSynapseManagers)
                await joinerSynapseManager.DisposeAsync().ConfigureAwait(false);

            serverCancellationTokenSource.Cancel();

            try
            {
                await beaconServerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* expected on shutdown */
            }
        }

        Console.WriteLine("Demo finished.");
    }

    /// <summary>
    /// Runs <paramref name="beaconServer"/>'s receive loop on the thread pool, returning the
    /// background <see cref="Task"/> so the caller can await it during clean shutdown.
    /// Taking the server as a parameter breaks the closure capture of the caller's <c>using</c>
    /// local, avoiding the "captured variable is disposed in outer scope" analyser warning.
    /// </summary>
    private static Task StartBeaconServerAsync(BeaconServer beaconServer, CancellationToken cancellationToken)
    {
        return Task.Run(() => beaconServer.RunAsync(cancellationToken), cancellationToken);
    }
}
