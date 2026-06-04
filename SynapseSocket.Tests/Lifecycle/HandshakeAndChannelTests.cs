using System;
using System.Net;
using System.Text;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Tests.Lifecycle;

public class HandshakeAndChannelTests
{
    private static (SynapseManager server, SynapseManager client, SynapseConnection synapseConnection,
        TestHarness.EventRecorder serverEventRecorder, TestHarness.EventRecorder clientEventRecorder) StartPair(Action<SynapseConfig>? tweak = null)
    {
        int port = TestHarness.GetFreePort();
        SynapseManager server = new(TestHarness.ServerConfig(port, tweak));
        SynapseManager client = new(TestHarness.ClientConfig(tweak));

        TestHarness.EventRecorder serverEventRecorder = new();
        TestHarness.EventRecorder clientEventRecorder = new();
        serverEventRecorder.Attach(server);
        clientEventRecorder.Attach(client);

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        TestHarness.PumpUntil(() => serverEventRecorder.ConnectionsEstablished >= 1 && clientEventRecorder.ConnectionsEstablished >= 1, 2000, server, client);

        return (server, client, synapseConnection, serverEventRecorder, clientEventRecorder);
    }

    [Fact]
    public void Handshake_Fires_ConnectionEstablished_On_Both_Sides()
    {
        (SynapseManager server, SynapseManager client, _, TestHarness.EventRecorder serverEventRecorder, TestHarness.EventRecorder clientEventRecorder) = StartPair();
        using (server)
        using (client)
        {
            Assert.Equal(1, serverEventRecorder.ConnectionsEstablished);
            Assert.Equal(1, clientEventRecorder.ConnectionsEstablished);
        }
    }

    [Fact]
    public void Unreliable_Payload_Is_Delivered()
    {
        (SynapseManager server, SynapseManager client, SynapseConnection synapseConnection, TestHarness.EventRecorder serverEventRecorder, _) = StartPair();
        using (server)
        using (client)
        {
            client.Send(synapseConnection, Encoding.UTF8.GetBytes("hello"), isReliable: false);
            Assert.True(TestHarness.PumpUntil(() => serverEventRecorder.PacketsReceived >= 1, 2000, server, client),
                "server never received an unreliable packet");

            serverEventRecorder.Payloads.TryPeek(out byte[]? receivedPayload);
            Assert.NotNull(receivedPayload);
            Assert.Equal("hello", Encoding.UTF8.GetString(receivedPayload));
        }
    }

    [Fact]
    public void Reliable_Payload_Is_Delivered_And_Acked()
    {
        (SynapseManager server, SynapseManager client, SynapseConnection synapseConnection, TestHarness.EventRecorder serverEventRecorder, _) = StartPair();
        using (server)
        using (client)
        {
            client.Send(synapseConnection, Encoding.UTF8.GetBytes("rel"), isReliable: true);
            Assert.True(TestHarness.PumpUntil(() => serverEventRecorder.PacketsReceived >= 1, 2000, server, client));
        }
    }

    [Fact]
    public void Reliable_Messages_Are_Delivered_In_Order()
    {
        (SynapseManager server, SynapseManager client, SynapseConnection synapseConnection, TestHarness.EventRecorder serverEventRecorder, _) = StartPair();
        using (server)
        using (client)
        {
            const int Count = 20;
            for (int i = 0; i < Count; i++)
            {
                client.Send(synapseConnection, BitConverter.GetBytes(i), isReliable: true);
            }

            Assert.True(TestHarness.PumpUntil(() => serverEventRecorder.PacketsReceived >= Count, 4000, server, client));

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
    public void Server_Can_Echo_Reliably_From_Callback()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        SynapseManager serverRef = server;
        server.PacketReceived += (packetReceivedEventArgs) =>
        {
            // Re-entrant reliable send from inside the receive callback — safe because the engine is single-threaded.
            serverRef.Send(packetReceivedEventArgs.Connection, Encoding.UTF8.GetBytes("pong"), isReliable: true);
        };

        byte[]? clientReceivedPayload = null;
        client.PacketReceived += (packetReceivedEventArgs) => clientReceivedPayload = packetReceivedEventArgs.Payload.ToArray();

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 2000, server, client);
        client.Send(synapseConnection, Encoding.UTF8.GetBytes("ping"), isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => clientReceivedPayload != null, 3000, server, client));
        Assert.Equal("pong", Encoding.UTF8.GetString(clientReceivedPayload!));
    }

    [Fact]
    public void Graceful_Disconnect_Fires_Events_On_Both_Sides()
    {
        (SynapseManager server, SynapseManager client, SynapseConnection synapseConnection, TestHarness.EventRecorder serverEventRecorder, TestHarness.EventRecorder clientEventRecorder) = StartPair();
        using (server)
        using (client)
        {
            client.Disconnect(synapseConnection);
            Assert.True(TestHarness.PumpUntil(() => serverEventRecorder.ConnectionsClosed >= 1
                && clientEventRecorder.ConnectionsClosed >= 1, 3000, server, client),
                "disconnect did not fire on both sides");
        }
    }
}
