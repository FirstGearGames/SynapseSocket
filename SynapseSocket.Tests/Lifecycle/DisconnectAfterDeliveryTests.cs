using System;
using System.Net;
using Xunit;
using SynapseSocket.Connections;
using SynapseSocket.Core;

namespace SynapseSocket.Tests.Lifecycle;

/// <summary>
/// Live-socket coverage for <see cref="SynapseManager.DisconnectAfterDelivery"/>: a reliable payload sent just before the close still
/// reaches the peer when its first send is lost, and the close follows it.
/// </summary>
public class DisconnectAfterDeliveryTests
{
    /// <summary>
    /// How long a test waits for a handshake or a close before failing.
    /// </summary>
    private const int WaitMilliseconds = 3000;

    /// <summary>
    /// A reliable payload whose only send is lost is resent while the connection waits to close, reaches the peer, and the close follows
    /// it. <see cref="SynapseManager.Disconnect"/> tears the connection down with the payload still unacknowledged, so it never arrives.
    /// </summary>
    [Fact]
    public void Lost_Reliable_Payload_Is_Resent_Before_The_Connection_Closes()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, synapseConfig => synapseConfig.LatencySimulator.Enabled = true));
        using SynapseManager client = new(TestHarness.ClientConfig());

        int receivedCount = 0;
        int receivedCountAtClose = -1;
        client.PacketReceived += _ => receivedCount++;
        client.ConnectionClosed += _ => receivedCountAtClose = receivedCount;

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1 && clientConnection.State is ConnectionState.Connected, WaitMilliseconds, server, client), "The handshake did not complete.");

        SynapseConnection serverConnection = server.Connections.Connections[0];

        // The payload's first send is lost, and with nothing acknowledged the connection only starts to close.
        server.Config.LatencySimulator.PacketLossChance = 1.0;
        server.Send(serverConnection, new byte[] { 7 }, isReliable: true);
        server.DisconnectAfterDelivery(serverConnection);
        server.Config.LatencySimulator.PacketLossChance = 0.0;

        Assert.Throws<InvalidOperationException>(() => server.Send(serverConnection, new byte[] { 8 }, isReliable: true));

        Assert.True(TestHarness.PumpUntil(() => receivedCountAtClose >= 0, WaitMilliseconds, server, client), "The client was never told the connection closed.");
        Assert.True(receivedCountAtClose == 1, $"The connection closed with [{receivedCountAtClose}] payloads delivered rather than the one whose first send was lost.");
    }

    /// <summary>
    /// A connection closing after delivery hands its application nothing more: the peer's reliable and unreliable payloads that arrive
    /// while the close waits for its acknowledgement are dropped, and the close still follows.
    /// </summary>
    [Fact]
    public void Payloads_From_A_Closing_Connection_Are_Not_Delivered()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, synapseConfig => synapseConfig.LatencySimulator.Enabled = true));
        using SynapseManager client = new(TestHarness.ClientConfig());

        int serverReceivedCount = 0;
        bool isClientClosed = false;
        server.PacketReceived += _ => serverReceivedCount++;
        client.ConnectionClosed += _ => isClientClosed = true;

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1 && clientConnection.State is ConnectionState.Connected, WaitMilliseconds, server, client), "The handshake did not complete.");

        SynapseConnection serverConnection = server.Connections.Connections[0];

        // The server's last payload is lost, so its connection stays closing until the resend is acknowledged.
        server.Config.LatencySimulator.PacketLossChance = 1.0;
        server.Send(serverConnection, new byte[] { 7 }, isReliable: true);
        server.DisconnectAfterDelivery(serverConnection);
        server.Config.LatencySimulator.PacketLossChance = 0.0;

        client.Send(clientConnection, new byte[] { 1 }, isReliable: true);
        client.Send(clientConnection, new byte[] { 2 }, isReliable: false);

        Assert.True(TestHarness.PumpUntil(() => isClientClosed, WaitMilliseconds, server, client), "The client was never told the connection closed.");
        Assert.True(serverReceivedCount == 0, $"The closing connection delivered [{serverReceivedCount}] of the peer's payloads to the application.");
    }
}
