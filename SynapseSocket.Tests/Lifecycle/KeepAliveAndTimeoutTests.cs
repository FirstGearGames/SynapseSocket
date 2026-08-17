using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests.Lifecycle;

public class KeepAliveAndTimeoutTests
{
    [Fact]
    public void Idle_Connection_Times_Out_And_Fires_Timeout_Failure()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Connection.KeepAliveIntervalMilliseconds = 10_000; // effectively never
            c.Connection.TimeoutMilliseconds = 300;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);
        server.Start();

        // Stand up a raw socket that pretends to handshake once and then
        // goes silent so the server times it out.
        using Socket socket = TestHarness.CreateRawSocket();
        byte[] handshakePacket = [0x03]; // PacketType.Handshake
        socket.SendTo(handshakePacket, new IPEndPoint(IPAddress.Loopback, port));

        Assert.True(TestHarness.PumpUntil(() => eventRecorder.ConnectionsEstablished >= 1, 2000, server));
        Assert.True(TestHarness.PumpUntil(
                () => eventRecorder.ViolationReasons.Contains(ViolationReason.Timeout), 3000, server),
            "expected Timeout violation reason to be raised");
    }

    [Fact]
    public void KeepAlive_Prevents_Timeout()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Connection.KeepAliveIntervalMilliseconds = 50;
            c.Connection.TimeoutMilliseconds = 500;
        }));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.Connection.KeepAliveIntervalMilliseconds = 50;
            c.Connection.TimeoutMilliseconds = 500;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 2000, server, client);

        // Pump for longer than ConnectionTimeoutMilliseconds; keep-alive should keep both sides alive.
        TestHarness.PumpFor(1500, server, client);

        Assert.DoesNotContain(ViolationReason.Timeout, eventRecorder.ViolationReasons);
        Assert.Equal(0, eventRecorder.ConnectionsClosed);
    }

    /// <summary>
    /// A peer that only ever receives must still be kept alive. This is the relay traffic shape: one host broadcasting
    /// to listening clients, where a listening client never calls Send and its only outbound traffic is the keep-alive
    /// the maintenance sweep owes the peer. Scheduling that sweep off received traffic silences the listener precisely
    /// here, because its last-received is always fresh, and the broadcaster then tears down a healthy connection at
    /// TimeoutMilliseconds with the stream still flowing. Scheduling off last-sent keeps the listener talking.
    /// </summary>
    [Fact]
    public void Receive_Only_Peer_Survives_Past_Timeout_While_Traffic_Flows_One_Way()
    {
        const int TimeoutMilliseconds = 300;
        const int BroadcastIntervalMilliseconds = 33; // ~30 Hz, matching the relay soak's broadcast rate.

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Connection.KeepAliveIntervalMilliseconds = 50;
            c.Connection.TimeoutMilliseconds = TimeoutMilliseconds;
        }));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.Connection.KeepAliveIntervalMilliseconds = 50;
            c.Connection.TimeoutMilliseconds = TimeoutMilliseconds;
        }));

        TestHarness.EventRecorder serverRecorder = new();
        TestHarness.EventRecorder clientRecorder = new();
        serverRecorder.Attach(server);
        clientRecorder.Attach(client);

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(
                () => clientConnection.State == ConnectionState.Connected && server.Connections.Count == 1, 2000, server, client),
            "expected the handshake to complete on both sides");

        SynapseConnection serverConnection = server.Connections.Connections[0];
        long establishedReceivedTicks = serverConnection.LastReceivedTicks;

        // Broadcast one way for several times TimeoutMilliseconds.
        // The client is polled so it receives and runs maintenance, but it never calls Send.
        // Every byte the client puts on the wire therefore has to have come from the keep-alive.
        byte[] broadcastPayload = [7, 7, 7, 7];
        long deadlineMilliseconds = Environment.TickCount64 + (TimeoutMilliseconds * 5);
        long nextBroadcastMilliseconds = 0;

        while (Environment.TickCount64 < deadlineMilliseconds)
        {
            if (Environment.TickCount64 >= nextBroadcastMilliseconds && server.Connections.Count > 0)
            {
                server.Send(serverConnection, broadcastPayload, isReliable: false);
                nextBroadcastMilliseconds = Environment.TickCount64 + BroadcastIntervalMilliseconds;
            }

            server.Poll();
            client.Poll();
            Thread.Sleep(1);
        }

        Assert.DoesNotContain(ViolationReason.Timeout, serverRecorder.ViolationReasons);
        Assert.DoesNotContain(ViolationReason.Timeout, clientRecorder.ViolationReasons);
        Assert.Equal(0, serverRecorder.ConnectionsClosed);
        Assert.Equal(0, clientRecorder.ConnectionsClosed);

        Assert.Equal(ConnectionState.Connected, clientConnection.State);
        Assert.Equal(ConnectionState.Connected, serverConnection.State);
        Assert.Equal(1, server.Connections.Count);
        Assert.Equal(1, client.Connections.Count);

        // The stream really did keep flowing for the whole run, so the listener was the silent side throughout.
        Assert.True(clientRecorder.PacketsReceived > 0, "expected the client to receive the broadcast");

        // The client sent no payloads at all, so only its keep-alive can have advanced the server's last-received.
        // Without one the stamp would still read the handshake.
        Assert.True(serverConnection.LastReceivedTicks > establishedReceivedTicks,
            "expected the receive-only client to have transmitted keep-alives to the server");
    }
}
