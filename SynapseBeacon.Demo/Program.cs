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
/// The Synapse engines are poll-driven: this single-threaded demo pumps every engine with <see cref="PumpUntil"/>,
/// including <em>while awaiting</em> the beacon client's async operations (whose responses are delivered through the
/// engine's socket and therefore only arrive when the engine is polled). The beacon rendezvous server runs its own
/// UDP loop on a background task — it is a plain socket server, not a <see cref="SynapseManager"/>.
/// </para>
/// </summary>
internal static class Program
{
    private const int BeaconPort = 47776;
    private const int HostPort = 47001;
    private const int JoinerBasePort = 47002;
    private const int JoinerCount = 2;

    private static void Main()
    {
        Console.WriteLine("=== SynapseBeacon Demo ===");

        using CancellationTokenSource serverCancellationTokenSource = new();

        /* --- Start the beacon server (a plain UDP rendezvous server on its own background loop) --- */
        using BeaconServer beaconServer = new(BeaconPort, BeaconServer.DefaultSessionTimeoutMilliseconds, BeaconServer.UnlimitedConcurrentSessions, text => Console.WriteLine(text));
        Task beaconServerTask = StartBeaconServerAsync(beaconServer, serverCancellationTokenSource.Token);
        Console.WriteLine($"[beacon] listening on loopback:{BeaconPort}");

        IPEndPoint beaconServerEndPoint = new(IPAddress.Loopback, BeaconPort);

        /* --- Build the host SynapseManager (FullCone NAT for hole-punching) --- */
        SynapseConfig hostSynapseConfig = new()
        {
            BindEndPoints = [new(IPAddress.Loopback, HostPort)],
            NatTraversal = { Mode = NatTraversalMode.FullCone },
            Security = { AllowUnknownPackets = true },
        };

        using SynapseManager hostSynapseManager = new(hostSynapseConfig);

        int acceptedPeerCount = 0;

        hostSynapseManager.ConnectionEstablished += connectionEventArgs =>
        {
            int newAcceptedPeerCount = Interlocked.Increment(ref acceptedPeerCount);
            Console.WriteLine($"[host] peer #{newAcceptedPeerCount} connected: {connectionEventArgs.Connection.RemoteEndPoint}");
        };

        hostSynapseManager.PacketReceived += packetReceivedEventArgs =>
        {
            string payloadText = Encoding.UTF8.GetString(packetReceivedEventArgs.Payload);
            Console.WriteLine($"[host] received from {packetReceivedEventArgs.Connection.RemoteEndPoint}: {payloadText}");
        };

        hostSynapseManager.Start();

        /* --- Create the host beacon client (piggybacks on the host's UDP socket) --- */
        BeaconClientConfig hostBeaconClientConfig = new(beaconServerEndPoint) { HeartbeatIntervalMilliseconds = 5_000 };
        using BeaconClient hostBeaconClient = new(hostSynapseManager, hostBeaconClientConfig);

        /* --- Host requests a session (pump the host while the beacon response is in flight) --- */
        using BeaconHostSession hostBeaconSession = PumpAwait(hostBeaconClient.HostAsync(CancellationToken.None), 5_000, hostSynapseManager);
        Console.WriteLine($"[host] session created: '{hostBeaconSession.SessionId}' — accepting up to {JoinerCount} joiners");

        hostBeaconSession.PeerReady += joinerEndPoint =>
            Console.WriteLine($"[host] beacon matched joiner at {joinerEndPoint}");

        /* --- Spin up joiners --- */
        List<SynapseManager> joinerSynapseManagers = new(JoinerCount);
        List<BeaconClient> joinerBeaconClients = new(JoinerCount);
        List<SynapseConnection> joinerSynapseConnections = new(JoinerCount);

        // Every engine that must be pumped while the demo runs (host + joiners). Rebuilt as joiners are added.
        List<SynapseManager> activeEngines = [hostSynapseManager];

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
                activeEngines.Add(joinerSynapseManager);

                joinerSynapseManager.ConnectionEstablished += connectionEventArgs =>
                    Console.WriteLine($"[joiner {joinerIndex}] connected to host: {connectionEventArgs.Connection.RemoteEndPoint}");

                joinerSynapseManager.Start();

                BeaconClientConfig joinerBeaconClientConfig = new(beaconServerEndPoint) { HeartbeatIntervalMilliseconds = 5_000 };
                BeaconClient joinerBeaconClient = new(joinerSynapseManager, joinerBeaconClientConfig);
                joinerBeaconClients.Add(joinerBeaconClient);

                /* JoinAsync resolves once the beacon server returns the host's endpoint — pump host + joiner so the
                 * beacon response is delivered through the joiner's socket. */
                IPEndPoint hostEndPointFromBeacon = PumpAwait(joinerBeaconClient.JoinAsync(hostBeaconSession.SessionId, CancellationToken.None), 5_000, [.. activeEngines]);
                Console.WriteLine($"[joiner {joinerIndex}] beacon matched host at {hostEndPointFromBeacon}");

                SynapseConnection joinerSynapseConnection = joinerSynapseManager.Connect(hostEndPointFromBeacon);
                joinerSynapseConnections.Add(joinerSynapseConnection);

                byte[] greetingPayload = Encoding.UTF8.GetBytes($"hello from joiner {joinerIndex}");
                joinerSynapseManager.Send(joinerSynapseConnection, greetingPayload, isReliable: true);
            }

            /* --- Pump until the host observes all JoinerCount peer connections (drives hole-punch + delivery) --- */
            SynapseManager[] engines = [.. activeEngines];
            bool allConnected = PumpUntil(() => Volatile.Read(ref acceptedPeerCount) >= JoinerCount, 5_000, engines);

            if (!allConnected)
            {
                Console.WriteLine($"[error] host only accepted {acceptedPeerCount}/{JoinerCount} joiners within 5s — aborting demo.");
                return;
            }

            Console.WriteLine($"[host] reached joiner cap ({JoinerCount}) — closing session so no further joiners can match.");
            PumpAwait(hostBeaconSession.CloseAsync(CancellationToken.None), 5_000, engines);
            Console.WriteLine($"[host] session '{hostBeaconSession.SessionId}' closed.");

            /* Give any in-flight greeting messages a moment to land before teardown. */
            PumpUntil(() => false, 250, engines);

            Console.WriteLine("[demo] SUCCESS — all joiners connected and exchanged a message before session close.");
        }
        finally
        {
            /* --- Teardown joiners --- */
            for (int i = 0; i < joinerSynapseConnections.Count; i++)
                joinerSynapseManagers[i].Disconnect(joinerSynapseConnections[i]);

            foreach (BeaconClient joinerBeaconClient in joinerBeaconClients)
                joinerBeaconClient.Dispose();

            foreach (SynapseManager joinerSynapseManager in joinerSynapseManagers)
                joinerSynapseManager.Dispose();

            serverCancellationTokenSource.Cancel();

            try
            {
                beaconServerTask.Wait(2_000);
            }
            catch (Exception)
            {
                /* expected on shutdown */
            }
        }

        Console.WriteLine("Demo finished.");
    }

    /// <summary>
    /// Pumps every engine until <paramref name="until"/> is true or <paramref name="timeoutMs"/> elapses.
    /// </summary>
    private static bool PumpUntil(Func<bool> until, int timeoutMs, params SynapseManager[] engines)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            for (int i = 0; i < engines.Length; i++)
                engines[i].Poll();

            if (until())
                return true;

            Thread.Sleep(1);
        }
        return until();
    }

    /// <summary>
    /// Pumps the given engines until <paramref name="task"/> completes (its result is delivered through an engine
    /// socket, so the engine must be polled), then returns the result.
    /// </summary>
    private static T PumpAwait<T>(Task<T> task, int timeoutMs, params SynapseManager[] engines)
    {
        PumpUntil(() => task.IsCompleted, timeoutMs, engines);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Pumps the given engines until <paramref name="task"/> completes.
    /// </summary>
    private static void PumpAwait(Task task, int timeoutMs, params SynapseManager[] engines)
    {
        PumpUntil(() => task.IsCompleted, timeoutMs, engines);
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs <paramref name="beaconServer"/>'s receive loop on the thread pool, returning the background task.
    /// </summary>
    private static Task StartBeaconServerAsync(BeaconServer beaconServer, CancellationToken cancellationToken)
    {
        return Task.Run(() => beaconServer.RunAsync(cancellationToken), cancellationToken);
    }
}
