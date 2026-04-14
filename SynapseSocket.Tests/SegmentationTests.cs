using System.Linq;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests;

public class SegmentationTests
{
    [Fact]
    public async Task Large_Unreliable_Payload_Is_Segmented_And_Reassembled()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.MaximumTransmissionUnit = 200;  // very small MTU to force many segments
            c.MaximumPacketSize = 400;
            c.MaximumSegments = 128;
        }));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.MaximumTransmissionUnit = 200;
            c.MaximumPacketSize = 400;
            c.MaximumSegments = 128;
        }));

        byte[]? receivedPayload = null;
        server.PacketReceived += (packetReceivedEventArgs) => receivedPayload = packetReceivedEventArgs.Payload.ToArray();

        await server.StartAsync();
        await client.StartAsync();

        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port));
        await TestHarness.WaitFor(() => synapseConnection.State == ConnectionState.Connected);

        byte[] payload = new byte[4096];
        new Random(42).NextBytes(payload);

        await client.SendAsync(synapseConnection, payload, isReliable: false);
        Assert.True(await TestHarness.WaitFor(() => receivedPayload != null, 5000),
            "server never reassembled the segmented payload");
        Assert.Equal(payload, receivedPayload);
    }

    [Fact]
    public async Task Reliable_Oversized_Payload_Throws_Clearly()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.MaximumTransmissionUnit = 256;
            c.MaximumPacketSize = 512;
        }));

        await server.StartAsync();
        await client.StartAsync();

        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port));
        await TestHarness.WaitFor(() => synapseConnection.State == ConnectionState.Connected);

        byte[] oversizedPayload = new byte[1024];
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendAsync(synapseConnection, oversizedPayload, isReliable: true));
    }

    [Fact]
    public async Task Declared_Segment_Assembly_Exceeding_MaximumReassembledPacketSize_Is_Rejected()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.MaximumTransmissionUnit = 200;
            c.MaximumSegments = 64;
            // 5 * 200 = 1000 > 500, so a packet claiming segmentCount=5 is rejected
            c.MaximumReassembledPacketSize = 500;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);
        await server.StartAsync();

        // Establish a connection first so the server has a known peer.
        using Socket socket = TestHarness.CreateRawSocket();
        IPEndPoint serverEndPoint = new(IPAddress.Loopback, port);
        socket.SendTo(new byte[] { 0x03 }, serverEndPoint); // PacketType.Handshake
        Assert.True(await TestHarness.WaitFor(() => eventRecorder.ConnectionsEstablished >= 1),
            "connection should have been established");

        // Craft a raw segmented packet:
        // PacketType.Segmented = 6 = 0x06
        // Header: [type=0x06, segmentId_lo=0x01, segmentId_hi=0x00, segmentIndex=0x00, segmentCount=0x05]
        // segmentCount=5, MTU=200 => 5*200=1000 > MaximumReassembledPacketSize=500 => blacklist
        byte[] segmentedPacket = [0x06, 0x01, 0x00, 0x00, 0x05, 0xAA, 0xBB, 0xCC];
        socket.SendTo(segmentedPacket, serverEndPoint);

        Assert.True(await TestHarness.WaitFor(
                () => eventRecorder.ViolationReasons.Contains(ViolationReason.Oversized), 3000),
            "expected an Oversized violation for a segment assembly exceeding MaximumReassembledPacketSize");
    }
}
