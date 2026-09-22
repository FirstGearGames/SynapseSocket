using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SynapseBeacon.Client;
using SynapseBeacon.Server;
using SynapseBeacon.Wire;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;
using SynapseSocket.Diagnostics;
using SynapseSocket.Packets;
using SynapseSocket.Transport;
using Xunit;

namespace SynapseSocket.Tests.Security;

/// <summary>
/// Empirical verification of the findings recorded in <c>docs/ROBUSTNESS_SWEEP.md</c>.
/// <para>
/// Every test here asserts the <b>correct</b> (post-fix) behaviour. A failing test therefore means the
/// corresponding finding is real and still present; a passing test means the finding was wrong and should be
/// struck from the document. Test names carry the finding ID so results map straight back to the report.
/// </para>
/// <para>
/// All wire-involved cases run over real loopback UDP sockets against a real <see cref="SynapseManager"/>.
/// The "attacker" is an ordinary <see cref="Socket"/> from <see cref="TestHarness.CreateRawSocket"/> sending
/// hand-built datagrams. The same thing a hostile peer on the network does. No reflection, no test-only
/// production code, no injected internal state.
/// </para>
/// </summary>
public sealed class SweepFindingTests
{
    /// <summary>
    /// Builds a wire-format handshake datagram: type byte plus the 8-byte nonce the protocol expects.
    /// </summary>
    private static byte[] BuildHandshake()
    {
        byte[] datagram = new byte[1 + 8];
        datagram[0] = (byte)PacketType.Handshake;
        RandomNumberGenerator.Fill(datagram.AsSpan(1, 8));
        return datagram;
    }

    /// <summary>
    /// Builds a wire-format reliable datagram carrying one payload byte at the given sequence.
    /// </summary>
    private static byte[] BuildReliable(ushort sequence, byte payload)
    {
        return [(byte)PacketType.Reliable, (byte)(sequence & 0xFF), (byte)((sequence >> 8) & 0xFF), payload];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C1: ConnectionManager swap-remove
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// C1: after two removals the endpoint table and the list the maintenance sweep walks must still agree.
    /// Uses only the public <see cref="ConnectionManager"/> surface.
    /// </summary>
    [Fact]
    public void C1_TwoRemovals_LeaveEndpointTableAndListConsistent()
    {
        ConnectionManager connections = new();

        IPEndPoint[] endPoints =
        [
            new(IPAddress.Loopback, 40001),
            new(IPAddress.Loopback, 40002),
            new(IPAddress.Loopback, 40003),
            new(IPAddress.Loopback, 40004)
        ];

        foreach (IPEndPoint endPoint in endPoints)
            connections.GetOrAdd(endPoint, (ulong)endPoint.Port, out _);

        Assert.Equal(4, connections.Connections.Count);

        // Remove a middle entry, then another middle entry.
        connections.Remove(endPoints[1], out _);
        connections.Remove(endPoints[2], out _);

        // Every connection still resolvable by endpoint must also be in the list RunMaintenance iterates,
        // otherwise it silently stops receiving keep-alives, timeouts and rate-counter resets.
        foreach (KeyValuePair<IPEndPoint, SynapseConnection> entry in connections.ConnectionsByEndPoint)
        {
            Assert.True(
                Contains(connections.Connections, entry.Value),
                $"Connection {entry.Key} resolves by endpoint but is absent from Connections. It will never be maintained again.");
        }

        // And nothing may linger in the list after being removed from the table.
        foreach (SynapseConnection connection in connections.Connections)
        {
            Assert.True(
                connections.ConnectionsByEndPoint.ContainsKey(connection.RemoteEndPoint),
                $"Connection {connection.RemoteEndPoint} is in Connections but was removed from the endpoint table.");
        }

        Assert.Equal(2, connections.Connections.Count);
    }

    /// <summary>
    /// C1: the cached <see cref="SynapseConnection.ConnectionsIndex"/> must keep matching the real list position,
    /// because <see cref="ConnectionManager.Remove"/> uses it to decide which slot to delete.
    /// </summary>
    [Fact]
    public void C1_ConnectionsIndex_MatchesListPositionAfterRemoval()
    {
        ConnectionManager connections = new();

        for (int port = 41001; port <= 41004; port++)
            connections.GetOrAdd(new(IPAddress.Loopback, port), (ulong)port, out _);

        connections.Remove(new(IPAddress.Loopback, 41002), out _);

        for (int i = 0; i < connections.Connections.Count; i++)
        {
            SynapseConnection connection = connections.Connections[i];
            Assert.True(
                connection.ConnectionsIndex == i,
                $"{connection.RemoteEndPoint} sits at list position {i} but reports ConnectionsIndex {connection.ConnectionsIndex}.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C2: zero-length datagram
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// C2: a zero-length UDP datagram must not stop the engine receiving. Live loopback sockets.
    /// </summary>
    [Fact]
    public void C2_ZeroLengthDatagram_DoesNotWedgeTheReceiveDrain()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        // Baseline: normal traffic flows.
        client.Send(connection, new([1, 2, 3]), isReliable: false);
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.PacketsReceived >= 1, 3000, server, client),
            "baseline payload never arrived");

        int baseline = serverEvents.PacketsReceived;

        // The attack: one zero-byte datagram.
        using Socket attacker = TestHarness.CreateRawSocket();
        attacker.SendTo(Array.Empty<byte>(), new IPEndPoint(IPAddress.Loopback, port));

        TestHarness.PumpFor(200, server, client);

        // The engine must still receive afterwards.
        client.Send(connection, new([4, 5, 6]), isReliable: false);
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.PacketsReceived > baseline, 3000, server, client),
            "engine stopped receiving after a single zero-length datagram. Receive drain is wedged");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C4: unauthenticated connection creation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// C4: the shipped default must bound how much connection state unauthenticated handshakes can allocate.
    /// A default of 0 (unlimited) means one datagram per fresh source endpoint grows the table forever.
    /// </summary>
    [Fact]
    public void C4_DefaultConfiguration_BoundsConcurrentConnections()
    {
        SynapseConfig defaultConfig = new();

        Assert.True(
            defaultConfig.MaximumConcurrentConnections != SynapseConfig.DisabledMaximumConcurrentConnections,
            "MaximumConcurrentConnections defaults to unlimited, so unauthenticated handshakes can allocate " +
            "connection state without bound");
    }

    /// <summary>
    /// C4: the cap must actually be enforced against unauthenticated handshakes arriving from fresh source
    /// endpoints. The exact shape of a spoofed-source flood.
    /// </summary>
    [Fact]
    public void C4_UnauthenticatedHandshakes_CannotExceedTheConnectionCap()
    {
        const int AttackerCount = 40;
        const uint ConnectionCap = 8;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.MaximumConcurrentConnections = ConnectionCap));
        server.Start();

        List<Socket> attackers = [];

        try
        {
            IPEndPoint target = new(IPAddress.Loopback, port);

            for (int i = 0; i < AttackerCount; i++)
            {
                Socket attacker = TestHarness.CreateRawSocket();
                attackers.Add(attacker);
                attacker.SendTo(BuildHandshake(), target);
            }

            TestHarness.PumpFor(600, server);

            Assert.True(
                server.Connections.Count <= ConnectionCap,
                $"{server.Connections.Count} connections were created from {AttackerCount} unauthenticated handshakes " +
                $"despite a cap of {ConnectionCap}");
        }
        finally
        {
            foreach (Socket attacker in attackers)
                attacker.Dispose();
        }
    }

    /// <summary>
    /// C4: above the occupancy threshold, a source that never answers the return-routability challenge must not
    /// consume a connection slot, while a real client, which does answer, still connects.
    /// </summary>
    [Fact]
    public void C4_AboveChallengeThreshold_UnprovenSourceGetsNoSlot_RealClientStillConnects()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.Security.HandshakeChallengeThreshold = 1));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);

        // Below the threshold, connecting is unchanged: one handshake, one round trip, one connection.
        using Socket firstPeer = TestHarness.CreateRawSocket();
        firstPeer.SendTo(BuildHandshake(), target);
        TestHarness.PumpFor(300, server);
        Assert.Equal(1, server.Connections.Count);

        // At the threshold, a source that ignores the challenge gets nothing, however many handshakes it sends.
        using Socket silentPeer = TestHarness.CreateRawSocket();

        for (int i = 0; i < 20; i++)
            silentPeer.SendTo(BuildHandshake(), target);

        TestHarness.PumpFor(400, server);

        Assert.True(
            server.Connections.Count == 1,
            $"an endpoint that never answered the challenge consumed a slot ({server.Connections.Count} connections)");

        // A real client answers the challenge and connects normally.
        using SynapseManager client = new(TestHarness.ClientConfig());
        client.Start();
        client.Connect(target);

        Assert.True(
            TestHarness.PumpUntil(() => server.Connections.Count == 2, 3000, server, client),
            "a legitimate client could not complete the challenged handshake");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C3: handshake against an already-connected endpoint
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// C3: a handshake arriving from an endpoint the engine already considers Connected must not both reset the
    /// session and echo a fresh handshake back. A peer running the same code would answer in kind, so a reply here
    /// is the step that sustains an endless mutual reset loop.
    /// </summary>
    [Fact]
    public void C3_HandshakeFromConnectedEndpoint_DoesNotResetAndEchoBack()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);

        using Socket peer = TestHarness.CreateRawSocket();
        peer.ReceiveTimeout = 500;

        // Establish: the engine now holds this endpoint as Connected and answers with a handshake-ack.
        peer.SendTo(BuildHandshake(), target);
        TestHarness.PumpFor(300, server);

        Assert.Equal(1, serverEvents.ConnectionsEstablished);

        byte[] receiveBuffer = new byte[64];
        DrainSocket(peer, receiveBuffer);

        int closesBefore = serverEvents.ConnectionsClosed;

        // A second handshake from the same, already-Connected endpoint.
        peer.SendTo(BuildHandshake(), target);
        TestHarness.PumpFor(300, server);

        bool echoedHandshake = false;

        while (TryReceive(peer, receiveBuffer, out int received))
        {
            if (received > 0 && receiveBuffer[0] == (byte)PacketType.Handshake)
                echoedHandshake = true;
        }

        bool resetSession = serverEvents.ConnectionsClosed > closesBefore;

        Assert.False(
            resetSession && echoedHandshake,
            "a handshake from an already-Connected endpoint both reset the live session and echoed a handshake back, " +
            "a peer running this same code answers in kind, sustaining an endless mutual reset loop");
    }

    /// <summary>
    /// Reads and discards everything currently queued on <paramref name="socket"/>.
    /// </summary>
    private static void DrainSocket(Socket socket, byte[] buffer)
    {
        while (TryReceive(socket, buffer, out _))
        {
        }
    }

    /// <summary>
    /// Non-blocking-ish single receive; returns false once nothing more is queued.
    /// </summary>
    private static bool TryReceive(Socket socket, byte[] buffer, out int received)
    {
        received = 0;

        if (socket.Available == 0)
            return false;

        try
        {
            received = socket.Receive(buffer);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H7 / M1: teardown paths must release what they hold
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H7: a remote <c>Disconnect</c> must release the connection's pooled buffers and helpers, exactly as a local
    /// disconnect does. Live sockets: a real peer establishes, sends segmented traffic to force a reassembler and a
    /// reorder entry into existence, then disconnects.
    /// </summary>
    [Fact]
    public void H7_RemoteDisconnect_ReleasesPooledResources()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection clientToServer = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        SynapseConnection serverSide = server.Connections.Connections[0];

        // Segmented traffic forces the server to rent a reassembler for this connection.
        byte[] payload = new byte[50_000];
        Random.Shared.NextBytes(payload);
        client.Send(clientToServer, new(payload), isReliable: false);

        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.PacketsReceived >= 1, 3000, server, client),
            "segmented payload never arrived");
        Assert.NotNull(serverSide.Reassembler);

        // The peer disconnects. The server's teardown runs entirely on the ingress path.
        client.Disconnect(clientToServer);

        Assert.True(
            TestHarness.PumpUntil(() => server.Connections.Count == 0, 3000, server, client),
            "server never observed the remote disconnect");

        Assert.True(serverSide.Reassembler is null, "reassembler was not returned on remote disconnect");
        Assert.True(serverSide.Splitter is null, "splitter was not returned on remote disconnect");
        Assert.Empty(serverSide.ReorderBuffer);
        Assert.Empty(serverSide.PendingReliableQueue);
    }

    /// <summary>
    /// M1: the timeout teardown must release the same set. Previously it returned the reorder buffer and pending
    /// reliables but left the splitter and reassembler (and their in-flight segment buffers) stranded.
    /// </summary>
    [Fact]
    public void M1_TimeoutTeardown_ReleasesSplitterAndReassembler()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            config.Connection.TimeoutMilliseconds = 1000;
            config.Connection.KeepAliveIntervalMilliseconds = 300;
        }));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection clientToServer = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        SynapseConnection serverSide = server.Connections.Connections[0];

        byte[] payload = new byte[50_000];
        Random.Shared.NextBytes(payload);
        client.Send(clientToServer, new(payload), isReliable: false);

        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.PacketsReceived >= 1, 3000, server, client),
            "segmented payload never arrived");
        Assert.NotNull(serverSide.Reassembler);

        // Client goes silent; the server must time it out and release everything.
        client.Stop();

        Assert.True(
            TestHarness.PumpUntil(() => server.Connections.Count == 0, 6000, server),
            "server never timed the silent peer out");

        Assert.True(serverSide.Reassembler is null, "reassembler was not returned on timeout");
        Assert.True(serverSide.Splitter is null, "splitter was not returned on timeout");
        Assert.Empty(serverSide.ReorderBuffer);
        Assert.Empty(serverSide.PendingReliableQueue);
    }

    /// <summary>
    /// M3: teardown must actually return the connection to the pool. <c>OnReturn</c> nulls
    /// <see cref="SynapseConnection.RemoteEndPoint"/>, so observing that proves the return ran.
    /// </summary>
    [Fact]
    public void M3_Teardown_ReturnsTheConnectionToThePool()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection clientToServer = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        SynapseConnection serverSide = server.Connections.Connections[0];
        Assert.NotNull(serverSide.RemoteEndPoint);

        client.Disconnect(clientToServer);

        Assert.True(
            TestHarness.PumpUntil(() => server.Connections.Count == 0, 3000, server, client),
            "server never observed the remote disconnect");

        // The return is deferred to the end of Poll, so give it one more.
        server.Poll();

        Assert.True(
            serverSide.RemoteEndPoint is null,
            "the torn-down connection was never returned to the pool. OnReturn did not run");
    }

    /// <summary>
    /// M3: a user handler disconnecting re-entrantly from inside <c>PacketReceived</c> must not corrupt the engine.
    /// This is the case the deferred pool return exists for: the ingress loop still holds the connection in a local
    /// while the handler tears it down, so returning it immediately would recycle an object mid-use.
    /// </summary>
    [Fact]
    public void M3_DisconnectFromInsidePacketReceived_DoesNotCorruptTheEngine()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        using TestHarness.FailureObserver failures = TestHarness.ObserveFailures(server, client);

        int established = 0;
        server.ConnectionEstablished += _ => Interlocked.Increment(ref established);

        // Tear the connection down from inside the receive callback, while the ingress loop still holds it.
        server.PacketReceived += args => server.Disconnect(args.Connection);

        server.Start();
        client.Start();

        SynapseConnection clientToServer = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => Volatile.Read(ref established) == 1, 3000, server, client),
            "handshake did not complete");

        // Several payloads back to back, so the handler fires while more are queued behind it.
        for (int i = 0; i < 8; i++)
            client.Send(clientToServer, new([(byte)i]), isReliable: false);

        Assert.True(
            TestHarness.PumpUntil(() => server.Connections.Count == 0, 3000, server, client),
            "re-entrant disconnect never removed the connection");

        // Keep polling: a recycled-mid-use connection surfaces here as an unhandled exception out of the drain.
        TestHarness.PumpFor(300, server, client);

        failures.AssertNoFailures();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H5 / L1 / L8: receive drain has no per-poll budget
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H5: the shipped default must bound how many datagrams one Poll will process. Without a bound a flood
    /// arriving faster than the engine drains it keeps Available above zero and Poll never returns.
    /// </summary>
    [Fact]
    public void H5_DefaultConfiguration_BoundsReceivesPerPoll()
    {
        SynapseConfig defaultConfig = new();

        Assert.True(
            defaultConfig.MaximumReceivesPerPoll != SynapseConfig.DisabledMaximumReceivesPerPoll,
            "MaximumReceivesPerPoll defaults to unlimited, so a sustained flood livelocks Poll()");
    }

    /// <summary>
    /// H5: a single Poll must stop at the configured budget rather than draining whatever has piled up.
    /// </summary>
    [Fact]
    public void H5_SinglePoll_StopsAtTheReceiveBudget()
    {
        const int Budget = 64;
        const int FloodCount = 600;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            config.EnableTelemetry = true;
            config.MaximumReceivesPerPoll = Budget;
        }));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        byte[] datagram = [(byte)PacketType.None, 1, 2, 3];

        using Socket flooder = TestHarness.CreateRawSocket();

        for (int i = 0; i < FloodCount; i++)
            flooder.SendTo(datagram, target);

        // Give the kernel a moment to queue them, then take exactly one poll.
        Thread.Sleep(200);
        server.Poll();

        // PacketsIn counts each datagram the filter admitted. (An unknown-sender datagram also increments
        // PacketsDroppedIn afterwards, so the two must not be summed.)
        long processed = server.Telemetry.PacketsIn;

        Assert.True(processed > 0, "the flood never reached the engine at all");
        Assert.True(
            processed <= Budget,
            $"one Poll processed {processed} datagrams against a budget of {Budget}. The drain has no per-poll bound");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M14: a throwing PacketReceived subscriber must not abort the delivery loop
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M14: when the reorder buffer drains several payloads at once and the first handler invocation throws, the
    /// remaining payloads must still be delivered. Aborting the loop drops them and strands their pooled buffers.
    /// </summary>
    [Fact]
    public void M14_ThrowingSubscriber_DoesNotDropTheRestOfTheDrainedBatch()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        server.Start();

        int delivered = 0;
        server.PacketReceived += _ =>
        {
            if (Interlocked.Increment(ref delivered) == 1)
                throw new InvalidOperationException("subscriber blew up on the first payload");
        };

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();

        peer.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 3000, server), "handshake did not land");

        // Sequences 1 and 2 arrive first and park in the reorder buffer; 0 then releases all three in one loop.
        peer.SendTo(BuildReliable(1, 0xA1), target);
        peer.SendTo(BuildReliable(2, 0xA2), target);
        TestHarness.PumpFor(200, server);

        peer.SendTo(BuildReliable(0, 0xA0), target);
        TestHarness.PumpFor(400, server);

        Assert.True(
            Volatile.Read(ref delivered) == 3,
            $"only {Volatile.Read(ref delivered)} of 3 payloads were delivered, a throwing handler aborted the " +
            "drain loop, dropping the rest of the batch and their pooled buffers");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M12 / M15: misrouted replies and latency-simulator double delivery
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M12: binding two endpoints of the same address family leaves one shared TransmissionEngine pointing at
    /// whichever socket was bound last, so every reply leaves through the wrong socket. Reject it loudly.
    /// </summary>
    [Fact]
    public void M12_TwoBindEndpointsOfTheSameFamily_AreRejected()
    {
        SynapseConfig config = new();
        config.BindEndPoints.Add(new(IPAddress.Loopback, TestHarness.GetFreePort()));
        config.BindEndPoints.Add(new(IPAddress.Loopback, TestHarness.GetFreePort()));

        using SynapseManager manager = new(config);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(manager.Start);
        Assert.Contains("address family", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// M15: if a send throws part-way through the latency simulator drain, the queue must still be compacted.
    /// Otherwise already-sent entries stay queued and are sent a second time from buffers already returned to
    /// the pool, duplicate delivery plus a double return.
    /// </summary>
    [Fact]
    public void M15_LatencySimulatorFlush_DoesNotResendAfterASendThrows()
    {
        LatencySimulatorConfig config = new() { Enabled = true, BaseLatencyMilliseconds = 50 };
        LatencySimulator simulator = new(config);
        IPEndPoint target = new(IPAddress.Loopback, 9999);

        long nowTicks = 1_000_000_000L;

        for (byte i = 0; i < 3; i++)
            simulator.Process(new([(byte)PacketType.None, i]), target, nowTicks, (_, _) => { });

        long dueTicks = nowTicks + 100 * TimeSpan.TicksPerMillisecond;

        // First drain: the second send blows up.
        List<byte> sent = [];
        int calls = 0;

        try
        {
            simulator.Flush(dueTicks, (segment, _) =>
            {
                calls++;
                sent.Add(segment.Array![segment.Offset + 1]);
                if (calls == 2)
                    throw new InvalidOperationException("send failed");
            });
        }
        catch (InvalidOperationException)
        {
            // A throwing send is the scenario under test; the simulator must survive it.
        }

        // Second drain with a healthy sender: nothing already delivered may go out again.
        simulator.Flush(dueTicks, (segment, _) => sent.Add(segment.Array![segment.Offset + 1]));

        Assert.True(
            sent.Count == sent.Distinct().Count(),
            $"a packet was delivered twice after a send threw mid-drain: [{string.Join(", ", sent)}]");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M5 / M13: remote-controlled memory bounds
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M5: the reorder-buffer cap is a bound on memory a remote peer controls, so it must hold regardless of the
    /// Security.Enabled switch. With it off the half-space check admits sequences up to 32,767 ahead, letting one
    /// peer pin that many pooled payload buffers simply by never sending the gap.
    /// </summary>
    [Fact]
    public void M5_ReorderBufferCap_HoldsEvenWithSecurityDisabled()
    {
        const uint Cap = 8;
        const int OutOfOrderCount = 60;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            config.Security.Enabled = false;
            config.Security.MaximumOutOfOrderReliablePackets = Cap;
        }));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();

        peer.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 3000, server), "handshake did not land");

        SynapseConnection serverSide = server.Connections.Connections[0];

        // Sequence 0 is never sent, so every one of these parks in the reorder buffer forever.
        for (ushort sequence = 1; sequence <= OutOfOrderCount; sequence++)
            peer.SendTo(BuildReliable(sequence, 0x5A), target);

        TestHarness.PumpFor(500, server);

        Assert.True(
            serverSide.ReorderBuffer.Count <= Cap,
            $"reorder buffer holds {serverSide.ReorderBuffer.Count} entries against a cap of {Cap}. The cap is " +
            "skipped entirely when Security.Enabled is false");
    }

    /// <summary>
    /// M13: a segment whose payload exceeds the MTU must be rejected. The assembly precheck bounds the total using
    /// the MTU, so accepting larger segments lets a peer declare a small segment count and still blow past the
    /// configured reassembled-size cap.
    /// </summary>
    [Fact]
    public void M13_SegmentLargerThanTheMtu_IsRejected()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            config.MaximumTransmissionUnit = 600;
            config.MaximumPacketSize = 1400;
        }));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();

        peer.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 3000, server), "handshake did not land");

        SynapseConnection serverSide = server.Connections.Connections[0];

        // Segment 0 of 2, but carrying 1000 payload bytes against a 600-byte MTU.
        byte[] oversizedSegment = new byte[1 + 4 + 1000];
        oversizedSegment[0] = (byte)PacketType.Segmented;
        oversizedSegment[1] = 7;   // segmentId low
        oversizedSegment[2] = 0;   // segmentId high
        oversizedSegment[3] = 0;   // segmentIndex
        oversizedSegment[4] = 2;   // segmentCount

        peer.SendTo(oversizedSegment, target);
        TestHarness.PumpFor(400, server);

        Assert.True(
            serverSide.Reassembler is null || !serverSide.Reassembler.HasAssemblies,
            "a segment carrying more than the MTU was accepted into reassembly, so the MTU-based size precheck " +
            "does not actually bound the assembled payload");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H12: malformed headers must not cost a managed exception each
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H12: parsing a truncated header must not throw. A managed throw/catch costs an allocation, a message string
    /// and two stack walks (orders of magnitude more than the parse it replaces) on a path an attacker can flood
    /// from rotating spoofed sources for the price of a 2-byte datagram.
    /// </summary>
    [Fact]
    public void H12_MalformedHeaders_DoNotAllocatePerPacket()
    {
        const int MalformedCount = 500;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            // Keep every datagram reaching the parser: no banning, no teardown.
            config.Security.ViolationsBeforeBlacklist = uint.MaxValue;
        }));

        server.ViolationDetected += _ => ViolationAction.Ignore;
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();

        // PacketType.Reliable declares a 2-byte sequence the datagram does not carry.
        byte[] truncated = [(byte)PacketType.Reliable, 0x01];

        // Warm the path so first-call JIT costs are not attributed to the measurement.
        peer.SendTo(truncated, target);
        TestHarness.PumpFor(150, server);

        for (int i = 0; i < MalformedCount; i++)
            peer.SendTo(truncated, target);

        Thread.Sleep(250);

        long before = GC.GetAllocatedBytesForCurrentThread();
        server.Poll();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        long perPacket = allocated / MalformedCount;

        /* A managed exception costs roughly 250-400 bytes. What legitimately remains on this path is the fresh
         * IPEndPoint the NET8 receive materialises for an unknown sender (~72 bytes), a separate gc-perf finding
         * about attribution, not about the parser. 150 cleanly separates the two. */
        Assert.True(
            perPacket < 150,
            $"parsing {MalformedCount} truncated headers allocated {allocated} bytes ({perPacket} per packet), " +
            "the header parser signals failure by throwing");
    }

    /// <summary>
    /// H12, isolated: the parser itself must allocate nothing when it rejects a header, for every truncated shape.
    /// </summary>
    [Fact]
    public void H12_HeaderParser_AllocatesNothingWhenRejecting()
    {
        byte[][] truncated =
        [
            [],                                             // no type byte
            [(byte)PacketType.Reliable],                    // sequence missing
            [(byte)PacketType.Reliable, 0x01],              // sequence half present
            [(byte)PacketType.Ack, 0x01],                   // sequence half present
            [(byte)PacketType.Segmented, 0x01, 0x02],       // segment fields short
            [(byte)PacketType.ReliableSegmented, 1, 2, 3]   // segment fields short
        ];

        // Warm the JIT before measuring.
        foreach (byte[] candidate in truncated)
            PacketHeader.TryRead(candidate, out _, out _, out _, out _, out _, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 2000; i++)
        {
            foreach (byte[] candidate in truncated)
            {
                Assert.False(
                    PacketHeader.TryRead(candidate, out _, out _, out _, out _, out _, out _),
                    "a truncated header parsed as valid");
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M17: the initial handshake must be retried
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M17: Connect sends exactly one handshake. A single dropped datagram then means the attempt never completes
    /// and never reports failure. It matters more now that the return-routability gate can require a second
    /// exchange, since losing either leg strands the connection in Pending.
    /// </summary>
    [Fact]
    public void M17_HandshakeIsRetried_WhenTheFirstAttemptIsLost()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager client = new(TestHarness.ClientConfig());
        client.Start();

        // The peer is not listening yet, so this first handshake goes nowhere at all.
        client.Connect(new(IPAddress.Loopback, port));
        TestHarness.PumpFor(400, client);

        using SynapseManager server = new(TestHarness.ServerConfig(port));
        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        server.Start();

        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 5000, server, client),
            "the connection never established after the first handshake was lost. Connect does not retry");
    }

    /// <summary>
    /// M17: a handshake nobody ever answers must eventually give up and report it, rather than sitting in
    /// Pending until the idle timeout quietly removes it.
    /// </summary>
    /// <remarks>
    /// Retrying and giving up are two jobs with two knobs. <see cref="ConnectionConfig.HandshakeMaximumAttempts"/>
    /// only bounds the retransmissions; ending the attempt is
    /// <see cref="ConnectionConfig.HandshakeTimeoutMilliseconds"/>'s, which closes the connection and raises a
    /// <see cref="ViolationReason.Timeout"/> violation exactly as an idle timeout does. Asserting on the close is
    /// what keeps this test honest about which mechanism actually ends the attempt.
    /// </remarks>
    [Fact]
    public void M17_UnansweredHandshake_EventuallyReportsConnectionFailed()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager client = new(TestHarness.ClientConfig(config =>
        {
            config.Connection.HandshakeRetryIntervalMilliseconds = 100;
            config.Connection.HandshakeMaximumAttempts = 3;
            config.Connection.HandshakeTimeoutMilliseconds = 500;
        }));

        TestHarness.EventRecorder clientEvents = new();
        clientEvents.Attach(client);
        client.Start();

        // Nothing is bound on that port, so the handshake is never answered.
        client.Connect(new(IPAddress.Loopback, port));

        Assert.True(
            TestHarness.PumpUntil(() => clientEvents.ViolationReasons.Contains(ViolationReason.Timeout), 5000, client),
            "an unanswered handshake was never given up on");

        Assert.Equal(1, clientEvents.ConnectionsClosed);
        Assert.Equal(0, client.Connections.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H6 / M22 / M23: the handshake replay cache
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H6: the replay key mixes the attacker-supplied handshake payload, so every distinct nonce mints a distinct
    /// entry. One peer sending handshakes at line rate must not be able to grow the cache without bound.
    /// </summary>
    [Fact]
    public void H6_ReplayCache_StaysBoundedUnderAHandshakeFlood()
    {
        const int FloodCount = 12000;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            // Keep every handshake on the replay path rather than diverting it to a challenge.
            config.Security.HandshakeChallengeThreshold = SecurityConfig.DisabledHandshakeChallengeThreshold;
            config.MaximumReceivesPerPoll = SynapseConfig.DisabledMaximumReceivesPerPoll;
            // Per-connection rate limiting would otherwise shed most of the flood before it reached the cache;
            // this test is about the cache bound, not the rate limiter.
            config.Security.MaximumPacketsPerSecond = SecurityConfig.DisabledMaximumPacketsPerSecond;
            config.Security.MaximumBytesPerSecond = SecurityConfig.DisabledMaximumBytesPerSecond;
        }));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();

        for (int i = 0; i < FloodCount; i++)
        {
            peer.SendTo(BuildHandshake(), target);

            if ((i & 0x7F) == 0)
                TestHarness.PumpFor(15, server);
        }

        TestHarness.PumpFor(600, server);

        Assert.True(
            server.ReplayCacheCount <= SynapseManager.MaximumReplayCacheEntries,
            $"replay cache holds {server.ReplayCacheCount} entries after {FloodCount} handshakes. It has no bound");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M16: ACK batching must actually coalesce
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M16: with batching on, a burst of reliable packets must be acknowledged in far fewer datagrams than it
    /// contained. Deferring the acks without merging them adds latency for nothing, and under a reliable flood it
    /// emits one outbound datagram per inbound one.
    /// </summary>
    [Fact]
    public void M16_AckBatching_CoalescesIntoFewerDatagrams()
    {
        const int ReliableCount = 40;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.EnableTelemetry = true));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        TestHarness.PumpFor(200, server, client);
        long sentBefore = server.Telemetry.PacketsOut;

        for (int i = 0; i < ReliableCount; i++)
            client.Send(connection, new([(byte)i]), isReliable: true);

        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.PacketsReceived >= ReliableCount, 5000, server, client),
            "reliable burst never fully arrived");

        TestHarness.PumpFor(300, server, client);

        long ackDatagrams = server.Telemetry.PacketsOut - sentBefore;

        Assert.True(
            ackDatagrams < ReliableCount / 2,
            $"acknowledging {ReliableCount} reliable packets took {ackDatagrams} outbound datagrams, batching " +
            "defers the acks but never merges them");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H11: beacon session identifiers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H11: session ids are drawn from [100000, 1000000) with System.Random. 900,000 values is trivially
    /// enumerable, and JoinSession discloses the host endpoint for any id that hits, so the space must be wide.
    /// </summary>
    [Fact]
    public void H11_BeaconSessionIds_SpanMoreThanTheSixDigitRange()
    {
        BeaconSessionRegistry registry = new(sessionTimeoutMilliseconds: 300_000, maximumConcurrentSessions: 0);

        bool sawIdOutsideSixDigits = false;

        for (int i = 0; i < 300; i++)
        {
            Assert.True(registry.TryCreateSession(new(IPAddress.Loopback, 20000 + i), out uint sessionId));

            if (sessionId >= 1_000_000)
            {
                sawIdOutsideSixDigits = true;
                break;
            }
        }

        Assert.True(
            sawIdOutsideSixDigits,
            "every session id landed inside the six-digit range, so the id space is ~900,000 values and can be " +
            "swept end to end to harvest every host endpoint the beacon knows");
    }

    /// <summary>
    /// H11: creating sessions must terminate even when the id space is saturated. The generator retries until
    /// TryAdd succeeds, so a full table makes it spin forever inside the receive loop.
    /// </summary>
    [Fact]
    public void H11_BeaconSessionCreation_TerminatesWhenTheIdSpaceIsSaturated()
    {
        // A one-slot space: the first session takes it, and the next attempt can never find a free id.
        BeaconSessionRegistry registry = new(sessionTimeoutMilliseconds: 300_000, maximumConcurrentSessions: 0, identifierSpace: 1);

        Assert.True(registry.TryCreateSession(new(IPAddress.Loopback, 20001), out _));

        Task<bool> secondAttempt = Task.Run(() => registry.TryCreateSession(new IPEndPoint(IPAddress.Loopback, 20002), out _));

        Assert.True(
            secondAttempt.Wait(TimeSpan.FromSeconds(5)),
            "TryCreateSession never returned with the id space saturated. The retry loop is unbounded");
        Assert.False(secondAttempt.Result, "a session was created despite no free identifier");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M4: unreliable segmented sends must return their pooled list
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M4: PacketSplitter.Split rents its segment list from ListPool. The reliable branch hands ownership to
    /// PendingReliable, which returns it; the unreliable branch returns only the backing buffer. A list that is
    /// never returned means the pool is always empty, so every large unreliable send allocates a fresh one.
    /// </summary>
    [Fact]
    public void M4_UnreliableSegmentedSend_DoesNotAllocateAListEveryTime()
    {
        const int SendCount = 30;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        // ~200 segments per send, so a leaked list is a large, unmistakable allocation.
        byte[] payload = new byte[240_000];

        // Warm the array pool and JIT so only the steady-state cost is measured.
        for (int i = 0; i < 3; i++)
            client.Send(connection, new(payload), isReliable: false);

        TestHarness.PumpFor(200, server, client);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < SendCount; i++)
            client.Send(connection, new(payload), isReliable: false);

        long perSend = (GC.GetAllocatedBytesForCurrentThread() - before) / SendCount;

        Assert.True(
            perSend < 512,
            $"each large unreliable send allocated {perSend} bytes. The pooled segment list is never returned");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H9: off-thread sends must not touch the engine send path
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H9: the beacon heartbeat runs on a timer thread and used to call SendRaw inline, racing the poll thread on
    /// the engine's unsynchronised serialized-target cache. EnqueueRaw is the thread-safe hand-off: nothing may
    /// leave the socket until Poll runs on the engine thread.
    /// </summary>
    /// <remarks>
    /// A data race is not deterministically testable, so this asserts the structural property that removes it:
    /// that an off-thread send is deferred to the poll thread rather than executed where it was requested.
    /// </remarks>
    [Fact]
    public void H9_EnqueueRawFromAnotherThread_IsDeferredToPoll()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.EnableTelemetry = true));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, TestHarness.GetFreePort());
        byte[] payload = [0x40, 1, 2, 3];

        long before = server.Telemetry.PacketsOut;

        // Hand the send over from a thread that is not the one calling Poll.
        Task.Run(() => server.EnqueueRaw(target, new(payload))).Wait(TimeSpan.FromSeconds(2));

        Assert.True(
            server.Telemetry.PacketsOut == before,
            "an off-thread send reached the socket without going through Poll. It ran on the caller's thread");

        server.Poll();

        Assert.True(
            server.Telemetry.PacketsOut > before,
            "the queued send was never flushed by Poll");
    }

    /// <summary>
    /// H9: many threads enqueueing at once must all be delivered exactly once, with no lost or duplicated sends.
    /// </summary>
    [Fact]
    public void H9_ConcurrentEnqueueRaw_DeliversEverySendExactlyOnce()
    {
        const int ThreadCount = 8;
        const int PerThread = 50;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.EnableTelemetry = true));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, TestHarness.GetFreePort());
        long before = server.Telemetry.PacketsOut;

        Task[] writers = new Task[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            writers[t] = Task.Run(() =>
            {
                byte[] payload = [0x40, 0, 0, 0];

                for (int i = 0; i < PerThread; i++)
                    server.EnqueueRaw(target, new(payload));
            });
        }

        Assert.True(Task.WaitAll(writers, TimeSpan.FromSeconds(10)), "writer threads did not finish");

        server.Poll();

        Assert.Equal(ThreadCount * PerThread, server.Telemetry.PacketsOut - before);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H10: beacon replies must be bound to the request that asked for them
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H10: a PeerReady is accepted on the strength of its source address, which is forgeable. A forged one
    /// redirects the joiner's hole-punch and subsequent Connect at an endpoint the attacker names. The reply must
    /// echo the nonce the joiner sent, which an off-path attacker never sees.
    /// </summary>
    [Fact]
    public void H10_ForgedPeerReady_DoesNotCompleteAJoin()
    {
        const uint SessionId = 4242;

        int beaconPort = TestHarness.GetFreePort();
        IPEndPoint beaconEndPoint = new(IPAddress.Loopback, beaconPort);

        // Stands in for the beacon server: receives the join, then answers with a forged, unbound PeerReady.
        using Socket beaconSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        beaconSocket.Bind(beaconEndPoint);

        using SynapseManager synapse = new(TestHarness.ClientConfig(config => config.Security.AllowUnknownPackets = true));
        synapse.Start();

        using BeaconClient beaconClient = new(synapse, new BeaconClientConfig(beaconEndPoint));

        Task<IPEndPoint> join = beaconClient.JoinAsync(SessionId, CancellationToken.None);

        // Let the JoinSession leave and land.
        TestHarness.PumpFor(300, synapse);

        byte[] received = new byte[64];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        beaconSocket.ReceiveTimeout = 2000;
        int joinLength = beaconSocket.ReceiveFrom(received, ref from);
        Assert.True(joinLength > 0, "the beacon never received a JoinSession");

        // Forge PeerReady naming an attacker endpoint, with no valid nonce echo.
        byte[] forged = new byte[1 + 1 + 4 + 2];
        forged[0] = (byte)BeaconPacketType.PeerReady;
        forged[1] = 4;                       // IPv4
        forged[2] = 203; forged[3] = 0; forged[4] = 113; forged[5] = 7;   // 203.0.113.7
        forged[6] = 0x35; forged[7] = 0x082 & 0xFF;                        // port

        beaconSocket.SendTo(forged, from);

        TestHarness.PumpFor(600, synapse);

        Assert.False(
            join.IsCompletedSuccessfully,
            "a forged PeerReady completed the join. The joiner would now hole-punch and Connect to an endpoint " +
            "chosen by whoever forged the source address");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H13: steady-state receive allocation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H13, bounded: on the NET8 build a datagram from an <b>established</b> peer must cost no allocation. The
    /// SocketAddress receive fills a reusable instance and the sender resolves to its connection's stable endpoint,
    /// so nothing per-datagram should be created.
    /// </summary>
    /// <remarks>
    /// This does not cover the netstandard2.1 (Unity/Mono) build, where ReceiveFrom materialises a SocketAddress,
    /// an IPEndPoint and an IPAddress per datagram and the runtime offers no allocation-free any-sender receive.
    /// That part of H13 is a platform limit; the mitigation is ConnectedSocketEnabled, which is allocation-free on
    /// every runtime, or accepting the cost on Unity servers.
    /// </remarks>
    [Fact]
    public void H13_EstablishedPeerReceive_IsAllocationFreeOnTheModernBuild()
    {
        const int PacketCount = 300;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        int received = 0;
        server.PacketReceived += _ => received++;

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 3000, server, client), "handshake failed");

        byte[] payload = new byte[64];

        // Warm the array pool, the JIT and the serialized-target cache.
        for (int i = 0; i < 20; i++)
            client.Send(connection, new(payload), isReliable: false);

        TestHarness.PumpFor(300, server, client);

        for (int i = 0; i < PacketCount; i++)
            client.Send(connection, new(payload), isReliable: false);

        Thread.Sleep(200);

        int before = received;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        server.Poll();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        int drained = received - before;
        Assert.True(drained > 50, $"only {drained} datagrams were drained; the measurement needs a real batch");

        long perDatagram = allocated / drained;

        Assert.True(
            perDatagram < 16,
            $"receiving from an established peer allocated {allocated} bytes across {drained} datagrams " +
            $"({perDatagram} per datagram). The steady-state receive path is not allocation-free");
    }

    /// <summary>
    /// H13, measured directly: what the netstandard2.1 receive overload actually costs per datagram, compared
    /// against the NET8 SocketAddress overload on the same runtime. Measures the BCL calls in isolation, with no
    /// engine involved, so the number is attributable to the socket API and nothing else.
    /// </summary>
    [Fact]
    public void H13_MeasureReceiveOverloadAllocation()
    {
        const int Count = 300;

        using Socket receiver = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.ReceiveTimeout = 2000;

        IPEndPoint receiverEndPoint = (IPEndPoint)receiver.LocalEndPoint!;
        using Socket sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        byte[] datagram = [1, 2, 3, 4];
        byte[] buffer = new byte[2048];

        // ---- netstandard2.1 shape: ReceiveFrom(byte[], ..., ref EndPoint) ----
        EndPoint anyEndPoint = new IPEndPoint(IPAddress.Any, 0);

        for (int i = 0; i < 20; i++) { sender.SendTo(datagram, receiverEndPoint); receiver.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref anyEndPoint); }

        for (int i = 0; i < Count; i++)
            sender.SendTo(datagram, receiverEndPoint);

        Thread.Sleep(150);

        long refBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Count; i++)
            receiver.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref anyEndPoint);
        long refPerDatagram = (GC.GetAllocatedBytesForCurrentThread() - refBefore) / Count;

        // ---- NET8 shape: ReceiveFrom(Span, SocketFlags, SocketAddress) ----
        SocketAddress reusable = new(AddressFamily.InterNetwork);

        for (int i = 0; i < 20; i++) { sender.SendTo(datagram, receiverEndPoint); receiver.ReceiveFrom(buffer.AsSpan(), SocketFlags.None, reusable); }

        for (int i = 0; i < Count; i++)
            sender.SendTo(datagram, receiverEndPoint);

        Thread.Sleep(150);

        long addrBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Count; i++)
            receiver.ReceiveFrom(buffer.AsSpan(), SocketFlags.None, reusable);
        long addrPerDatagram = (GC.GetAllocatedBytesForCurrentThread() - addrBefore) / Count;

        /* The NET8 overload fills a caller-owned SocketAddress and allocates nothing. The netstandard2.1 overload
         * writes a freshly materialised IPEndPoint through the ref parameter, so the object escapes the call and
         * cannot be elided by any downstream compiler. The gap is the whole of what H13 is about. */
        Assert.True(
            addrPerDatagram == 0,
            $"the SocketAddress receive overload allocated {addrPerDatagram} B/datagram; it is the allocation-free path");

        Assert.True(
            refPerDatagram > addrPerDatagram,
            $"measured ref-EndPoint {refPerDatagram} B/datagram vs SocketAddress {addrPerDatagram} B/datagram");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M18: selective segment retransmission
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M18: a reliable segmented message shares one sequence, and the receiver only acknowledged on completion, so
    /// one lost segment resent the entire message every resend interval. With selective acknowledgement the sender
    /// learns which segments landed and resends only the gap.
    /// </summary>
    [Fact]
    public void M18_OneLostSegment_DoesNotRetransmitTheWholeMessage()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.EnableTelemetry = true));
        using SynapseManager client = new(TestHarness.ClientConfig(config =>
        {
            config.EnableTelemetry = true;
            config.Reliable.ResendMilliseconds = 200;
            // Drop a small fraction so a segment or two goes missing from the first burst.
            config.LatencySimulator.Enabled = true;
            config.LatencySimulator.PacketLossChance = 0.03;
        }));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        TestHarness.PumpFor(200, server, client);

        // ~170 segments at the default MTU.
        byte[] payload = new byte[200_000];
        Random.Shared.NextBytes(payload);

        long sentBefore = client.Telemetry.PacketsOut;
        client.Send(connection, new(payload), isReliable: true);

        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.PacketsReceived >= 1, 8000, server, client),
            "the segmented message never completed");

        TestHarness.PumpFor(400, server, client);

        long datagramsSent = client.Telemetry.PacketsOut - sentBefore;

        /* One full pass is ~170 datagrams. Without selective retransmission a single loss costs another full pass
         * every resend interval; with it, only the missing segments go again. Allow generous headroom for the
         * first pass plus acks and a modest number of repairs. */
        Assert.True(
            datagramsSent < 250,
            $"delivering one 200 KB message under 3% loss took {datagramsSent} datagrams, a lost segment is " +
            "retransmitting the whole message rather than just the gap");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H2: one datagram, permanent blacklist
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H2: a single oversized datagram from an endpoint must not permanently bar that endpoint from connecting.
    /// </summary>
    [Fact]
    public void H2_SingleOversizedDatagram_DoesNotPermanentlyBlacklistTheSender()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);

        using Socket attacker = TestHarness.CreateRawSocket();

        // One datagram over MaximumPacketSize (1400 by default).
        attacker.SendTo(new byte[1401], target);
        TestHarness.PumpFor(200, server);

        // The very same endpoint now tries to connect legitimately.
        attacker.SendTo(BuildHandshake(), target);
        TestHarness.PumpFor(400, server);

        Assert.True(
            server.Connections.Count == 1,
            "endpoint was permanently blacklisted by a single oversized datagram and can no longer connect");
    }

    /// <summary>
    /// H3: a blacklist entry must expire. The original had no TTL and no bound, so every address that ever
    /// misbehaved was barred for the life of the process and the table only ever grew.
    /// </summary>
    /// <remarks>
    /// Driven through the real ingress path rather than the <see cref="SynapseSocket.Security.SecurityProvider"/>
    /// API, so what is asserted is that a barred peer can genuinely connect again once its entry ages out.
    /// </remarks>
    [Fact]
    public void H3_BlacklistEntries_ExpireInsteadOfBarringAnEndpointForever()
    {
        const uint ViolationsBeforeBlacklist = 2;
        const uint BlacklistDurationMilliseconds = 600;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            config.Security.ViolationsBeforeBlacklist = ViolationsBeforeBlacklist;
            config.Security.BlacklistDurationMilliseconds = BlacklistDurationMilliseconds;
        }));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket attacker = TestHarness.CreateRawSocket();

        // Enough oversized datagrams to exhaust the violation allowance and earn a blacklist entry.
        for (int i = 0; i < ViolationsBeforeBlacklist + 1; i++)
        {
            attacker.SendTo(new byte[1401], target);
            TestHarness.PumpFor(60, server);
        }

        attacker.SendTo(BuildHandshake(), target);
        TestHarness.PumpFor(300, server);

        Assert.True(server.Connections.Count == 0, "the endpoint was never blacklisted, so this proves nothing about expiry");

        /* Retry the handshake on an interval rather than sending one and waiting. A raw socket does not
         * retransmit, and a banned endpoint that keeps sending pushes its own entry out, so a single shot timed
         * against the nominal expiry is a race the test would lose intermittently. A real client retries on
         * ConnectionConfig.HandshakeRetryIntervalMilliseconds for exactly this reason. */
        /* Nothing here may read the blacklist through SecurityProvider. IsBlacklisted drops a lapsed entry as a
         * side effect of the read, so a test that polls it performs the very expiry it is meant to be checking,
         * and would pass against a receive path that ignores expiry entirely. Only the wire is consulted. */
        bool reconnected = false;
        long deadlineMilliseconds = Environment.TickCount64 + 5000;

        while (Environment.TickCount64 < deadlineMilliseconds)
        {
            attacker.SendTo(BuildHandshake(), target);
            TestHarness.PumpFor(150, server);

            if (server.Connections.Count == 1)
            {
                reconnected = true;
                break;
            }
        }

        Assert.True(
            reconnected,
            $"a blacklisted endpoint was still barred after its entry should have expired. failures: [{string.Join(", ", serverEvents.FailureReasons)}]");
    }

    /// <summary>
    /// M2: connecting again to an endpoint that already has a session must reclaim the replaced connection rather
    /// than dropping it on the floor, which orphaned its pooled buffers, its splitter and its reassembler.
    /// </summary>
    [Fact]
    public void M2_ReconnectingToTheSameEndpoint_ReclaimsTheReplacedConnection()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);

        SynapseConnection first = client.Connect(target);
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "the first handshake did not complete");

        // Connect again to the same endpoint. The first instance must be torn down, not silently discarded.
        SynapseConnection second = client.Connect(target);
        TestHarness.PumpFor(300, server, client);

        Assert.False(ReferenceEquals(first, second), "the second Connect reused the first connection instance");
        Assert.True(client.Connections.Count == 1, $"reconnecting left [{client.Connections.Count}] client connections for one endpoint");
        Assert.True(first.RemoteEndPoint is null, "the replaced connection was discarded without being reset and returned to the pool");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H4: legitimate traffic tripping the rate limit
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H4a: the configured byte allowance must actually be reachable. <c>MaximumBytesPerSecond</c> documents 2 MiB/s
    /// of "comfortable legitimate headroom", but MTU-sized traffic hits the 500 pps cap at ~0.57 MiB/s, so the byte
    /// cap is unreachable by ~3.5x and the pps cap silently sets the real bandwidth ceiling.
    /// </summary>
    [Fact]
    public void H4a_TrafficWellUnderTheDocumentedByteAllowance_IsNotRateLimited()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        // 1 MiB in one second: half the documented 2 MiB/s allowance, sent as MTU-sized unreliable packets.
        byte[] payload = new byte[1000];
        Random.Shared.NextBytes(payload);

        for (int i = 0; i < 1048; i++)
            client.Send(connection, new(payload), isReliable: false);

        TestHarness.PumpFor(1200, server, client);

        Assert.False(
            HasRateLimitViolation(serverEvents),
            "traffic at ~1 MiB/s (half the documented MaximumBytesPerSecond allowance) was rate limited, " +
            "because the 500 pps cap caps bandwidth at ~0.57 MiB/s and makes the byte cap unreachable");
    }

    /// <summary>
    /// H4b: the realistic case. One reliable segmented send of a documented-legal size, plus ordinary packet loss,
    /// triggers a whole-message retransmit storm (M18) that trips the receiver's pps cap, so the sender is kicked
    /// and permanently blacklisted without the application doing anything unusual at all.
    /// </summary>
    [Fact]
    public void H4b_OneLargeReliableSendUnderPacketLoss_DoesNotSelfInflictABlacklist()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(config =>
        {
            config.LatencySimulator.Enabled = true;
            config.LatencySimulator.PacketLossChance = 0.02;
        }));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        // 200 KB, well inside the ~304 KB the segmenter accepts in a single call. ~168 segments.
        byte[] payload = new byte[200_000];
        Random.Shared.NextBytes(payload);

        client.Send(connection, new(payload), isReliable: true);

        TestHarness.PumpFor(3000, server, client);

        Assert.False(
            HasRateLimitViolation(serverEvents),
            "a single reliable send of a documented-legal size, under 2% packet loss, retransmitted the whole " +
            "message often enough to trip the receiver's rate limiter. The sender bans itself during normal use");
    }

    /// <summary>
    /// True when the recorder saw a rate-limit violation, whose default action is KickAndBlacklist.
    /// </summary>
    private static bool HasRateLimitViolation(TestHarness.EventRecorder events)
    {
        foreach (ViolationReason reason in events.ViolationReasons)
        {
            if (reason == ViolationReason.RateLimitExceeded)
                return true;
        }

        return false;
    }

    /// <summary>
    /// H4c: two maximum-size segmented sends inside one second. Kept for the record, but note that ~600 KB/s per
    /// peer is genuinely heavy for a realtime transport. The defensible complaint is the disproportionate
    /// response (permanent blacklist, no TTL), not that the rate itself must be allowed.
    /// </summary>
    [Fact]
    public void H4c_TwoLargeSendsInOneSecond_DoNotBlacklistALegitimatePeer()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 3000, server, client),
            "handshake did not complete");

        // 300 KB each: ~252 segments per send, so two sends exceed the 500 pps default.
        byte[] payload = new byte[300_000];
        Random.Shared.NextBytes(payload);

        client.Send(connection, new(payload), isReliable: false);
        client.Send(connection, new(payload), isReliable: false);

        TestHarness.PumpFor(1200, server, client);

        Assert.False(
            HasRateLimitViolation(serverEvents),
            "two large sends in one second tripped the rate limiter, whose default action is KickAndBlacklist");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H1: Pending connection that never times out
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H1: a connection whose handshake reply never arrives must eventually time out, even while the far side
    /// keeps sending data packets. Models a peer that considers itself connected while we are still Pending.
    /// </summary>
    [Fact]
    public void H1_PendingConnection_TimesOutEvenWhileInboundDataArrives()
    {
        using Socket fakePeer = TestHarness.CreateRawSocket();
        IPEndPoint fakePeerEndPoint = (IPEndPoint)fakePeer.LocalEndPoint!;

        // Bind to a known port so the fake peer can address the engine directly.
        int clientPort = TestHarness.GetFreePort();
        IPEndPoint clientEndPoint = new(IPAddress.Loopback, clientPort);

        using SynapseManager client = new(TestHarness.ServerConfig(clientPort, config =>
        {
            config.Connection.TimeoutMilliseconds = 1000;
            config.Connection.KeepAliveIntervalMilliseconds = 300;
        }));

        client.Start();

        // The fake peer never answers the handshake, so this connection stays Pending forever.
        client.Connect(fakePeerEndPoint);
        Assert.Equal(1, client.Connections.Count);

        // But it does send unreliable data, exactly as a peer that believes the session is up would.
        byte[] dataPacket = [(byte)PacketType.None, 0xAA];

        long deadline = Environment.TickCount64 + 5000;
        long nextSend = 0;

        while (Environment.TickCount64 < deadline && client.Connections.Count > 0)
        {
            if (Environment.TickCount64 >= nextSend)
            {
                fakePeer.SendTo(dataPacket, clientEndPoint);
                nextSend = Environment.TickCount64 + 200;
            }

            client.Poll();
            System.Threading.Thread.Sleep(1);
        }

        Assert.True(
            client.Connections.Count == 0,
            "a Pending connection was kept alive indefinitely by inbound data and never timed out (timeout was 1000 ms, waited 5000 ms)");
    }

    /// <summary>
    /// The native receive path resolves an established peer by hashing the raw <c>sockaddr</c> the kernel filled,
    /// while <see cref="SynapseSocket.Connections.ConnectionManager"/> registers that peer under the key computed
    /// from its managed <see cref="IPEndPoint"/>. The two overloads are separate hand-written FNV-1a loops, so if
    /// they ever disagree the lookup silently misses and every datagram from an established peer takes the slow
    /// unknown-sender path instead. Nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public void ComputeAddressKey_AgreesBetweenRawSockAddrAndManagedEndPoint()
    {
        IPEndPoint[] endPoints =
        [
            new(IPAddress.Loopback, 1),
            new(IPAddress.Loopback, 7777),
            new(IPAddress.Loopback, 65535),
            new(IPAddress.Parse("192.168.1.50"), 30000),
            new(IPAddress.Parse("255.254.253.252"), 4321),
            new(IPAddress.IPv6Loopback, 7777),
            new(IPAddress.Parse("fe80::1"), 9999),
            new(IPAddress.Parse("2001:db8::dead:beef"), 443),
        ];

        byte[] sockAddr = new byte[NativeSocket.SockAddrSize];

        foreach (IPEndPoint endPoint in endPoints)
        {
            Assert.True(NativeSocket.TryBuildSockAddr(endPoint, sockAddr, out int sockAddrLength), $"could not build a sockaddr for {endPoint}");

            ulong rawKey = NativeSocket.ComputeAddressKey(sockAddr, sockAddrLength);
            ulong managedKey = NativeSocket.ComputeAddressKey(endPoint);

            Assert.True(
                rawKey == managedKey,
                $"the two ComputeAddressKey overloads disagree for {endPoint}: raw sockaddr gave {rawKey}, managed endpoint gave {managedKey}. " +
                "The native receive path would fail to resolve this peer.");
        }
    }

    /// <summary>
    /// Distinct endpoints must not collide on the key the native receive path resolves them by. A collision would
    /// hand one peer's datagrams to another connection, which is a correctness failure rather than a slow path.
    /// </summary>
    [Fact]
    public void ComputeAddressKey_SeparatesPortsAndAddresses()
    {
        IPEndPoint[] endPoints =
        [
            new(IPAddress.Loopback, 7777),
            new(IPAddress.Loopback, 7778),
            new(IPAddress.Parse("127.0.0.2"), 7777),
            new(IPAddress.Parse("10.0.0.1"), 7777),
            new(IPAddress.IPv6Loopback, 7777),
            new(IPAddress.Parse("fe80::1"), 7777),
        ];

        Dictionary<ulong, IPEndPoint> keys = [];

        foreach (IPEndPoint endPoint in endPoints)
        {
            ulong key = NativeSocket.ComputeAddressKey(endPoint);

            Assert.False(
                keys.TryGetValue(key, out IPEndPoint? existing),
                $"{endPoint} collides with {existing} on address key {key}.");

            keys[key] = endPoint;
        }
    }

    /// <summary>
    /// Reference-equality membership check over the public connection list.
    /// </summary>
    private static bool Contains(IReadOnlyList<SynapseConnection> connections, SynapseConnection target)
    {
        for (int i = 0; i < connections.Count; i++)
        {
            if (ReferenceEquals(connections[i], target))
                return true;
        }

        return false;
    }
}
