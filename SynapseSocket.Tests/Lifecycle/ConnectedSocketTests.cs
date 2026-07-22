using System;
using System.Net;
using System.Text;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using Xunit;

namespace SynapseSocket.Tests.Lifecycle;

/// <summary>
/// Live-socket coverage for <see cref="SynapseConfig.ConnectedSocketEnabled"/>: a client whose socket is OS-connected to the
/// server must handshake and exchange payloads in both directions through the endpoint-free Receive and Send calls — the mode
/// that removes the per-datagram endpoint serialization the classic ReceiveFrom and SendTo paths pay on every runtime.
/// </summary>
public class ConnectedSocketTests
{
    /// <summary>
    /// A connected-socket client completes the handshake and exchanges unreliable and reliable payloads with the server in
    /// both directions — the connected receive attributes the server's datagrams correctly, and the connected send carries the
    /// client's payloads and acks.
    /// </summary>
    [Fact]
    public void ConnectedClient_HandshakeAndBidirectionalPayloads_Deliver()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(config => config.ConnectedSocketEnabled = true));

        TestHarness.EventRecorder serverEventRecorder = new();
        TestHarness.EventRecorder clientEventRecorder = new();
        serverEventRecorder.Attach(server);
        clientEventRecorder.Attach(client);

        SynapseManager serverReference = server;
        server.PacketReceived += (packetReceivedEventArgs) => serverReference.Send(packetReceivedEventArgs.Connection, Encoding.UTF8.GetBytes("pong"), isReliable: true);

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 2000, server, client), "The connected-socket client never completed the handshake.");

        client.Send(synapseConnection, Encoding.UTF8.GetBytes("ping-unreliable"), isReliable: false);
        client.Send(synapseConnection, Encoding.UTF8.GetBytes("ping-reliable"), isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => serverEventRecorder.PacketsReceived >= 2, 3000, server, client), "The server never received the connected-socket client's payloads.");
        Assert.True(TestHarness.PumpUntil(() => clientEventRecorder.PacketsReceived >= 2, 3000, server, client), "The connected-socket client never received the server's echoes.");

        bool hasPong = false;

        foreach (byte[] payload in clientEventRecorder.Payloads)
            hasPong |= Encoding.UTF8.GetString(payload) == "pong";

        Assert.True(hasPong, "The connected-socket client's received payloads did not contain the server's echo.");
    }

    /// <summary>
    /// A connected socket cannot receive the third-party probes full-cone traversal relies on, so combining the two must fail
    /// loudly at Connect rather than silently dropping probes.
    /// </summary>
    [Fact]
    public void ConnectedClient_FullConeNat_ConnectThrows()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager client = new(TestHarness.ClientConfig(config =>
        {
            config.ConnectedSocketEnabled = true;
            config.NatTraversal.Mode = NatTraversalMode.FullCone;
        }));

        client.Start();

        Assert.Throws<InvalidOperationException>(() => client.Connect(new(IPAddress.Loopback, port)));
    }
}
