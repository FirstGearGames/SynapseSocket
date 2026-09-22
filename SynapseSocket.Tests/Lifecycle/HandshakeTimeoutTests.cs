using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Xunit;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;
using SynapseSocket.Packets;

namespace SynapseSocket.Tests.Lifecycle;

/// <summary>
/// Live-socket coverage for <see cref="ConnectionConfig.HandshakeTimeoutMilliseconds"/>: a connection still waiting on its
/// handshake is timed out on the handshake timeout, measured from when the handshake began, while a connection whose
/// handshake was answered answers only to the idle timeout.
/// </summary>
public class HandshakeTimeoutTests
{
    /// <summary>
    /// The handshake timeout the tests configure, short enough to observe and far below <see cref="IdleTimeoutMilliseconds"/>.
    /// </summary>
    private const int HandshakeTimeoutMilliseconds = 300;
    /// <summary>
    /// The idle timeout the tests configure alongside <see cref="HandshakeTimeoutMilliseconds"/>, long enough that a close
    /// observed within <see cref="CloseWaitMilliseconds"/> can only have come from the handshake timeout.
    /// </summary>
    private const int IdleTimeoutMilliseconds = 30_000;
    /// <summary>
    /// How long a test waits for a pending connection to close before failing.
    /// </summary>
    private const int CloseWaitMilliseconds = 5000;
    /// <summary>
    /// Allowance for the engine's wall clock and the test's stopwatch disagreeing slightly on elapsed time.
    /// </summary>
    private const int ClockToleranceMilliseconds = 20;
    /// <summary>
    /// Interval at which the silent peer in <see cref="Pending_Connection_Receiving_Traffic_Still_Times_Out_At_The_Handshake_Timeout"/>
    /// sends keep-alives, well inside the default packet rate limit.
    /// </summary>
    private const int PeerKeepAliveIntervalMilliseconds = 25;

    /// <summary>
    /// A client connecting to a port nobody listens on is closed once the handshake timeout elapses, not after the idle
    /// timeout, and the close is reported exactly as an idle timeout is: one <see cref="SynapseManager.ConnectionClosed"/>
    /// and a <see cref="ViolationReason.Timeout"/> violation, with no <see cref="SynapseManager.ConnectionEstablished"/>.
    /// </summary>
    [Fact]
    public void Unanswered_Handshake_Closes_At_The_Handshake_Timeout_Not_The_Idle_Timeout()
    {
        // Nothing listens on this port: GetFreePort binds it only long enough to learn a free number.
        int port = TestHarness.GetFreePort();
        using SynapseManager client = new(TestHarness.ClientConfig(config =>
        {
            config.Connection.TimeoutMilliseconds = IdleTimeoutMilliseconds;
            config.Connection.HandshakeTimeoutMilliseconds = HandshakeTimeoutMilliseconds;
        }));

        TestHarness.EventRecorder clientEventRecorder = new();
        clientEventRecorder.Attach(client);
        client.Start();

        Stopwatch stopwatch = Stopwatch.StartNew();
        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));

        Assert.True(TestHarness.PumpUntil(() => clientEventRecorder.ConnectionsClosed >= 1, CloseWaitMilliseconds, client), "The unanswered handshake was not closed before the idle timeout could have fired.");

        long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        Assert.True(elapsedMilliseconds >= HandshakeTimeoutMilliseconds - ClockToleranceMilliseconds, $"The unanswered handshake closed after [{elapsedMilliseconds}] ms, before its [{HandshakeTimeoutMilliseconds}] ms timeout.");
        Assert.Equal(ConnectionState.Disconnected, synapseConnection.State);
        Assert.Equal(0, client.Connections.Count);
        Assert.Equal(1, clientEventRecorder.ConnectionsClosed);
        Assert.Equal(0, clientEventRecorder.ConnectionsEstablished);
        Assert.Contains(ViolationReason.Timeout, clientEventRecorder.ViolationReasons);
    }

    /// <summary>
    /// A handshake the server answers promotes the connection out of the handshake timeout's reach. Both engines configure
    /// the short handshake timeout and run for several multiples of it, and neither side closes or raises a timeout.
    /// </summary>
    [Fact]
    public void Answered_Handshake_Is_Unaffected_By_The_Handshake_Timeout()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, ConfigureAnsweredHandshake));
        using SynapseManager client = new(TestHarness.ClientConfig(ConfigureAnsweredHandshake));

        TestHarness.EventRecorder serverEventRecorder = new();
        TestHarness.EventRecorder clientEventRecorder = new();
        serverEventRecorder.Attach(server);
        clientEventRecorder.Attach(client);

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => synapseConnection.State is ConnectionState.Connected && server.Connections.Count == 1, 2000, server, client), "The handshake never completed on both sides.");

        TestHarness.PumpFor(HandshakeTimeoutMilliseconds * 4, server, client);

        Assert.Equal(ConnectionState.Connected, synapseConnection.State);
        Assert.Equal(ConnectionState.Connected, server.Connections.Connections[0].State);
        Assert.Equal(0, clientEventRecorder.ConnectionsClosed);
        Assert.Equal(0, serverEventRecorder.ConnectionsClosed);
        Assert.DoesNotContain(ViolationReason.Timeout, clientEventRecorder.ViolationReasons);
        Assert.DoesNotContain(ViolationReason.Timeout, serverEventRecorder.ViolationReasons);
    }

    /// <summary>
    /// A peer that never answers the handshake but keeps sending other traffic, as a server does when its handshake reply
    /// was lost and it already considers the session connected, refreshes the pending connection's last-received time
    /// throughout. The handshake timeout is measured from when the handshake began, so the connection still closes on time
    /// rather than waiting on its handshake forever.
    /// </summary>
    /// <remarks>
    /// The silent peer is a raw socket, because a live engine always answers a handshake it receives and so cannot hold a
    /// client in this state on demand. <see cref="Unanswered_Handshake_Closes_At_The_Handshake_Timeout_Not_The_Idle_Timeout"/>
    /// is the engine-to-network case; this one pins down what the timeout is measured from.
    /// </remarks>
    [Fact]
    public void Pending_Connection_Receiving_Traffic_Still_Times_Out_At_The_Handshake_Timeout()
    {
        using Socket peerSocket = TestHarness.CreateRawSocket();
        peerSocket.ReceiveTimeout = 2000;

        using SynapseManager client = new(TestHarness.ClientConfig(config =>
        {
            config.Connection.TimeoutMilliseconds = IdleTimeoutMilliseconds;
            config.Connection.HandshakeTimeoutMilliseconds = HandshakeTimeoutMilliseconds;
        }));

        TestHarness.EventRecorder clientEventRecorder = new();
        clientEventRecorder.Attach(client);
        client.Start();

        Stopwatch stopwatch = Stopwatch.StartNew();
        SynapseConnection synapseConnection = client.Connect((IPEndPoint)peerSocket.LocalEndPoint!);
        long handshakeReceivedTicks = synapseConnection.LastReceivedTicks;

        // Learn the client's endpoint from the handshake it just sent, then never answer it.
        byte[] receiveBuffer = new byte[64];
        EndPoint clientEndPoint = new IPEndPoint(IPAddress.Any, 0);
        peerSocket.ReceiveFrom(receiveBuffer, ref clientEndPoint);
        Assert.Equal((byte)PacketType.Handshake, receiveBuffer[0]);

        byte[] keepAlivePacket = [(byte)PacketType.KeepAlive];
        long deadlineMilliseconds = Environment.TickCount64 + CloseWaitMilliseconds;
        long nextKeepAliveMilliseconds = 0;

        while (clientEventRecorder.ConnectionsClosed == 0 && Environment.TickCount64 < deadlineMilliseconds)
        {
            if (Environment.TickCount64 >= nextKeepAliveMilliseconds)
            {
                peerSocket.SendTo(keepAlivePacket, clientEndPoint);
                nextKeepAliveMilliseconds = Environment.TickCount64 + PeerKeepAliveIntervalMilliseconds;
            }

            client.Poll();
            Thread.Sleep(1);
        }

        long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        Assert.True(clientEventRecorder.ConnectionsClosed == 1, "The pending connection was held open by the peer's traffic instead of timing out on its handshake.");
        Assert.True(elapsedMilliseconds >= HandshakeTimeoutMilliseconds - ClockToleranceMilliseconds, $"The pending connection closed after [{elapsedMilliseconds}] ms, before its [{HandshakeTimeoutMilliseconds}] ms timeout.");
        // The peer's traffic reached the pending connection, so an idle clock would have been reset throughout.
        Assert.True(synapseConnection.LastReceivedTicks > handshakeReceivedTicks, "The peer's keep-alives never reached the pending connection.");
        Assert.Equal(0, clientEventRecorder.ConnectionsEstablished);
        Assert.Contains(ViolationReason.Timeout, clientEventRecorder.ViolationReasons);
    }

    /// <summary>
    /// A default configuration leaves the handshake timeout unset, and an unset handshake timeout holds a pending handshake
    /// to <see cref="ConnectionConfig.TimeoutMilliseconds"/> rather than closing it immediately.
    /// </summary>
    [Fact]
    public void Unset_Handshake_Timeout_Falls_Back_To_The_Idle_Timeout()
    {
        const int FallbackIdleTimeoutMilliseconds = 400;

        Assert.Equal(ConnectionConfig.UnsetHandshakeTimeoutMilliseconds, new ConnectionConfig().HandshakeTimeoutMilliseconds);

        int port = TestHarness.GetFreePort();
        using SynapseManager client = new(TestHarness.ClientConfig(config => config.Connection.TimeoutMilliseconds = FallbackIdleTimeoutMilliseconds));

        TestHarness.EventRecorder clientEventRecorder = new();
        clientEventRecorder.Attach(client);
        client.Start();

        Stopwatch stopwatch = Stopwatch.StartNew();
        client.Connect(new(IPAddress.Loopback, port));

        Assert.True(TestHarness.PumpUntil(() => clientEventRecorder.ConnectionsClosed >= 1, CloseWaitMilliseconds, client), "The unanswered handshake never timed out on the idle timeout.");

        long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        Assert.True(elapsedMilliseconds >= FallbackIdleTimeoutMilliseconds - ClockToleranceMilliseconds, $"The unanswered handshake closed after [{elapsedMilliseconds}] ms, before the [{FallbackIdleTimeoutMilliseconds}] ms idle timeout it falls back to.");
        Assert.Contains(ViolationReason.Timeout, clientEventRecorder.ViolationReasons);
    }

    /// <summary>
    /// A handshake timeout that would end before the full-cone hole-punch schedule finishes is rejected at construction,
    /// and one that covers the schedule exactly is accepted. The schedule runs for the direct-attempt grace plus every
    /// punch interval.
    /// </summary>
    [Fact]
    public void Handshake_Timeout_Shorter_Than_The_Full_Cone_Punch_Schedule_Is_Rejected()
    {
        NatTraversalConfig natTraversalConfig = new();
        uint punchScheduleMilliseconds = natTraversalConfig.FullCone.DirectAttemptMilliseconds + natTraversalConfig.MaximumAttempts * natTraversalConfig.IntervalMilliseconds;

        SynapseConfig shortConfig = TestHarness.ClientConfig(config =>
        {
            config.NatTraversal.Mode = NatTraversalMode.FullCone;
            config.Connection.HandshakeTimeoutMilliseconds = punchScheduleMilliseconds - 1;
        });
        SynapseConfig coveringConfig = TestHarness.ClientConfig(config =>
        {
            config.NatTraversal.Mode = NatTraversalMode.FullCone;
            config.Connection.HandshakeTimeoutMilliseconds = punchScheduleMilliseconds;
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => new SynapseManager(shortConfig));

        using SynapseManager synapseManager = new(coveringConfig);
    }

    /// <summary>
    /// Configures an engine in <see cref="Answered_Handshake_Is_Unaffected_By_The_Handshake_Timeout"/> with the short
    /// handshake timeout and keep-alives frequent enough that the idle timeout never fires.
    /// </summary>
    /// <param name="synapseConfig">The configuration to adjust.</param>
    private static void ConfigureAnsweredHandshake(SynapseConfig synapseConfig)
    {
        synapseConfig.Connection.KeepAliveIntervalMilliseconds = 50;
        synapseConfig.Connection.TimeoutMilliseconds = 5000;
        synapseConfig.Connection.HandshakeTimeoutMilliseconds = HandshakeTimeoutMilliseconds;
    }
}
