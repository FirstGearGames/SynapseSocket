using System.Linq;
using System.Net;
using System.Net.Sockets;
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
}
