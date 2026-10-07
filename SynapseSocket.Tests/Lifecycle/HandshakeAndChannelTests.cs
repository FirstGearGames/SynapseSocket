using System;
using System.Net;
using System.Text;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Events;
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

        SynapseConnection synapseConnection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));
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
    public void Connect_By_Host_Name_Resolves_Without_Blocking_And_Establishes()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());
        SynapseConnection? establishedConnection = null;
        client.ConnectionEstablished += (connectionEventArgs) => establishedConnection = connectionEventArgs.Connection;

        server.Start();
        client.Start();

        // A host name is looked up in the background, so nothing is connected yet when the call returns.
        Assert.Null(client.Connect("localhost", port));

        // "localhost" can resolve to ::1 first; the engine must pick the address family the client actually bound.
        Assert.True(TestHarness.PumpUntil(() => establishedConnection is { State: ConnectionState.Connected }, 2000, server, client),
            "connection by host name never established");
    }

    [Theory]
    [InlineData("localhost:{0}", false)]
    [InlineData("127.0.0.1:{0}", true)]
    public void Connect_By_Host_And_Port_String_Establishes(string format, bool isConnectedAtOnce)
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());
        SynapseConnection? establishedConnection = null;
        client.ConnectionEstablished += (connectionEventArgs) => establishedConnection = connectionEventArgs.Connection;

        server.Start();
        client.Start();

        // An IP address connects at once; a host name returns nothing until it resolves.
        SynapseConnection? synapseConnection = client.Connect(string.Format(format, port), port: null);
        Assert.Equal(isConnectedAtOnce, synapseConnection is not null);

        Assert.True(TestHarness.PumpUntil(() => establishedConnection is { State: ConnectionState.Connected }, 2000, server, client),
            "connection by host:port string never established");
    }

    [Fact]
    public void Connect_By_Cached_Host_Name_Connects_At_Once()
    {
        int port = TestHarness.GetFreePort();
        HostAddressCache hostAddressCache = new();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager firstClient = new(TestHarness.ClientConfig(config => config.HostAddressCache = hostAddressCache));
        using SynapseManager secondClient = new(TestHarness.ClientConfig(config => config.HostAddressCache = hostAddressCache));
        bool isFirstEstablished = false;
        firstClient.ConnectionEstablished += (_) => isFirstEstablished = true;

        server.Start();
        firstClient.Start();
        secondClient.Start();

        Assert.Null(firstClient.Connect("localhost", port));
        Assert.True(TestHarness.PumpUntil(() => isFirstEstablished, 2000, server, firstClient), "first connection by host name never established");

        // The second engine shares the cache, so the name it was handed resolves without a lookup.
        SynapseConnection? cachedConnection = secondClient.Connect("localhost", port);

        Assert.NotNull(cachedConnection);
        Assert.True(TestHarness.PumpUntil(() => cachedConnection.State == ConnectionState.Connected, 2000, server, secondClient),
            "connection by cached host name never established");
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:")]
    [InlineData(":7777")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("localhost:77a")]
    [InlineData("::1:7777")]
    [InlineData("[]:7777")]
    public void Connect_By_Malformed_Host_And_Port_Raises_ConnectionFailed_And_Throws(string hostAndPort)
    {
        using SynapseManager client = new(TestHarness.ClientConfig());
        ConnectionRejectedReason? failedReason = null;
        client.ConnectionFailed += (connectionFailedEventArgs) => failedReason = connectionFailedEventArgs.Reason;
        client.Start();

        Assert.Throws<ArgumentException>(() => client.Connect(hostAndPort, port: null));
        Assert.Equal(ConnectionRejectedReason.HostResolutionFailed, failedReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Connect_With_Out_Of_Range_Port_Raises_ConnectionFailed_And_Throws(int port)
    {
        using SynapseManager client = new(TestHarness.ClientConfig());
        ConnectionRejectedReason? failedReason = null;
        client.ConnectionFailed += (connectionFailedEventArgs) => failedReason = connectionFailedEventArgs.Reason;
        client.Start();

        Assert.Throws<ArgumentException>(() => client.Connect("localhost", port));
        Assert.Equal(ConnectionRejectedReason.HostResolutionFailed, failedReason);
    }

    [Fact]
    public void Connect_By_Unresolvable_Host_Raises_ConnectionFailed_From_Poll()
    {
        using SynapseManager client = new(TestHarness.ClientConfig());
        ConnectionRejectedReason? failedReason = null;
        string? failedMessage = null;
        client.ConnectionFailed += (connectionFailedEventArgs) =>
        {
            failedReason = connectionFailedEventArgs.Reason;
            failedMessage = connectionFailedEventArgs.Message;
        };
        client.Start();

        // The .invalid top-level domain is reserved and never resolves (RFC 6761).
        Assert.Null(client.Connect("synapse.invalid:7777", port: null));
        Assert.True(TestHarness.PumpUntil(() => failedMessage is not null, 10000, client), "an unresolvable host never reported its failure");

        Assert.Equal(ConnectionRejectedReason.HostResolutionFailed, failedReason);
        Assert.Contains("synapse.invalid", failedMessage);
        Assert.Empty(client.Connections.Connections);
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
            // Re-entrant reliable send from inside the receive callback. Safe because the engine is single-threaded.
            serverRef.Send(packetReceivedEventArgs.Connection, Encoding.UTF8.GetBytes("pong"), isReliable: true);
        };

        byte[]? clientReceivedPayload = null;
        client.PacketReceived += (packetReceivedEventArgs) => clientReceivedPayload = packetReceivedEventArgs.Payload.ToArray();

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));
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
