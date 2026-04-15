using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Tests.Lifecycle;

public class HandshakeAndChannelTests
{
    private static async Task<(SynapseManager server, SynapseManager client, SynapseConnection synapseConnection,
        TestHarness.EventRecorder serverEventRecorder, TestHarness.EventRecorder clientEventRecorder)> StartPair(Action<SynapseConfig>? tweak = null)
    {
        int port = TestHarness.GetFreePort();
        SynapseManager server = new(TestHarness.ServerConfig(port, tweak));
        SynapseManager client = new(TestHarness.ClientConfig(tweak));

        TestHarness.EventRecorder serverEventRecorder = new();
        TestHarness.EventRecorder clientEventRecorder = new();
        serverEventRecorder.Attach(server);
        clientEventRecorder.Attach(client);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        await TestHarness.WaitFor(() => serverEventRecorder.ConnectionsEstablished >= 1 && clientEventRecorder.ConnectionsEstablished >= 1);

        return (server, client, synapseConnection, serverEventRecorder, clientEventRecorder);
    }

    [Fact]
    public async Task Handshake_Fires_ConnectionEstablished_On_Both_Sides()
    {
        (SynapseManager server, SynapseManager client, _, TestHarness.EventRecorder serverEventRecorder, TestHarness.EventRecorder clientEventRecorder) = await StartPair();
        await using (server)
        await using (client)
        {
            Assert.Equal(1, serverEventRecorder.ConnectionsEstablished);
            Assert.Equal(1, clientEventRecorder.ConnectionsEstablished);
        }
    }

    [Fact]
    public async Task Unreliable_Payload_Is_Delivered()
    {
        (SynapseManager server, SynapseManager client, SynapseConnection synapseConnection, TestHarness.EventRecorder serverEventRecorder, _) = await StartPair();
        await using (server)
        await using (client)
        {
            await client.SendAsync(synapseConnection, Encoding.UTF8.GetBytes("hello"), isReliable: false, CancellationToken.None);
            Assert.True(await TestHarness.WaitFor(() => serverEventRecorder.PacketsReceived >= 1),
                "server never received an unreliable packet");

            serverEventRecorder.Payloads.TryPeek(out byte[]? receivedPayload);
            Assert.NotNull(receivedPayload);
            Assert.Equal("hello", Encoding.UTF8.GetString(receivedPayload));
        }
    }

    [Fact]
    public async Task Reliable_Payload_Is_Delivered_And_Acked()
    {
        (SynapseManager server, SynapseManager client, SynapseConnection synapseConnection, TestHarness.EventRecorder serverEventRecorder, _) = await StartPair();
        await using (server)
        await using (client)
        {
            await client.SendAsync(synapseConnection, Encoding.UTF8.GetBytes("rel"), isReliable: true, CancellationToken.None);
            Assert.True(await TestHarness.WaitFor(() => serverEventRecorder.PacketsReceived >= 1));
        }
    }

    [Fact]
    public async Task Reliable_Messages_Are_Delivered_In_Order()
    {
        (SynapseManager server, SynapseManager client, SynapseConnection synapseConnection, TestHarness.EventRecorder serverEventRecorder, _) = await StartPair();
        await using (server)
        await using (client)
        {
            const int Count = 20;
            for (int i = 0; i < Count; i++)
            {
                await client.SendAsync(synapseConnection, BitConverter.GetBytes(i), isReliable: true, CancellationToken.None);
            }

            Assert.True(await TestHarness.WaitFor(() => serverEventRecorder.PacketsReceived >= Count, 4000));

            int expectedIndex = 0;
            byte[][] receivedPayloads = [.. serverEventRecorder.Payloads];
            Array.Sort(receivedPayloads, (a, b) => BitConverter.ToInt32(a, 0).CompareTo(BitConverter.ToInt32(b, 0)));
            foreach (byte[] payload in receivedPayloads)
            {
                Assert.Equal(expectedIndex++, BitConverter.ToInt32(payload, 0));
            }
        }
    }

    [Fact]
    public async Task Server_Can_Echo_Reliably_From_Callback_Without_Deadlock()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig());

        SynapseManager serverRef = server;
        server.PacketReceived += async (packetReceivedEventArgs) =>
        {
            await serverRef.SendAsync(packetReceivedEventArgs.Connection, Encoding.UTF8.GetBytes("pong"), isReliable: true, CancellationToken.None);
        };

        byte[]? clientReceivedPayload = null;
        client.PacketReceived += (packetReceivedEventArgs) => clientReceivedPayload = packetReceivedEventArgs.Payload.ToArray();

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        await TestHarness.WaitFor(() => synapseConnection.State == ConnectionState.Connected);
        await client.SendAsync(synapseConnection, Encoding.UTF8.GetBytes("ping"), isReliable: true, CancellationToken.None);

        Assert.True(await TestHarness.WaitFor(() => clientReceivedPayload != null, 3000));
        Assert.Equal("pong", Encoding.UTF8.GetString(clientReceivedPayload!));
    }

    [Fact]
    public async Task Graceful_Disconnect_Fires_Events_On_Both_Sides()
    {
        (SynapseManager server, SynapseManager client, SynapseConnection synapseConnection, TestHarness.EventRecorder serverEventRecorder, TestHarness.EventRecorder clientEventRecorder) = await StartPair();
        await using (server)
        await using (client)
        {
            await client.DisconnectAsync(synapseConnection, CancellationToken.None);
            Assert.True(await TestHarness.WaitFor(() => serverEventRecorder.ConnectionsClosed >= 1
                && clientEventRecorder.ConnectionsClosed >= 1, 3000),
                "disconnect did not fire on both sides");
        }
    }
}
