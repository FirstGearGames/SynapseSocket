using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests.Lifecycle;

public class KeepAliveAndTimeoutTests
{
    [Fact]
    public async Task Idle_Connection_Times_Out_And_Fires_Timeout_Failure()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Connection.KeepAliveIntervalMilliseconds = 10_000; // effectively never
            c.Connection.TimeoutMilliseconds = 300;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);
        await server.StartAsync(CancellationToken.None);

        // Stand up a raw socket that pretends to handshake once and then
        // goes silent so the server times it out.
        using Socket socket = TestHarness.CreateRawSocket();
        byte[] handshakePacket = [0x03]; // PacketType.Handshake
        socket.SendTo(handshakePacket, new IPEndPoint(IPAddress.Loopback, port));

        Assert.True(await TestHarness.WaitFor(() => eventRecorder.ConnectionsEstablished >= 1));
        Assert.True(await TestHarness.WaitFor(
                () => eventRecorder.ViolationReasons.Contains(ViolationReason.Timeout), 3000),
            "expected Timeout violation reason to be raised");
    }

    [Fact]
    public async Task KeepAlive_Prevents_Timeout()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Connection.KeepAliveIntervalMilliseconds = 50;
            c.Connection.TimeoutMilliseconds = 500;
        }));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.Connection.KeepAliveIntervalMilliseconds = 50;
            c.Connection.TimeoutMilliseconds = 500;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        await TestHarness.WaitFor(() => synapseConnection.State == ConnectionState.Connected);

        // Wait longer than ConnectionTimeoutMilliseconds; keep-alive should keep both sides alive.
        await Task.Delay(1500);

        Assert.DoesNotContain(ViolationReason.Timeout, eventRecorder.ViolationReasons);
        Assert.Equal(0, eventRecorder.ConnectionsClosed);
    }
}
