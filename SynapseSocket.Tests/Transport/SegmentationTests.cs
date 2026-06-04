using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests.Transport;

public class SegmentationTests
{
    [Fact]
    public void Large_Unreliable_Payload_Is_Segmented_And_Reassembled()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.MaximumTransmissionUnit = 200;  // very small MTU to force many segments
            c.MaximumPacketSize = 400;
            c.Segment.MaximumSegments = 128;
        }));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.MaximumTransmissionUnit = 200;
            c.MaximumPacketSize = 400;
            c.Segment.MaximumSegments = 128;
        }));

        byte[]? receivedPayload = null;
        server.PacketReceived += (packetReceivedEventArgs) => receivedPayload = packetReceivedEventArgs.Payload.ToArray();

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 2000, server, client);

        byte[] payload = new byte[4096];
        new Random(42).NextBytes(payload);

        client.Send(synapseConnection, payload, isReliable: false);
        Assert.True(TestHarness.PumpUntil(() => receivedPayload != null, 5000, server, client),
            "server never reassembled the segmented payload");
        Assert.Equal(payload, receivedPayload);
    }

    [Fact]
    public void Reliable_Oversized_Payload_Throws_Clearly()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.MaximumTransmissionUnit = 256;
            c.MaximumPacketSize = 512;
            c.Segment.ReliableEnabled = false;
            c.Segment.UnreliableMode = UnreliableSegmentMode.Disabled;
        }));

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 2000, server, client);

        byte[] oversizedPayload = new byte[1024];
        Assert.Throws<InvalidOperationException>(() =>
            client.Send(synapseConnection, oversizedPayload, isReliable: true));
    }

    [Fact]
    public void Declared_Segment_Assembly_Exceeding_MaximumReassembledPacketSize_Is_Rejected()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.MaximumTransmissionUnit = 200;
            c.Segment.MaximumSegments = 64;
            // 5 * 200 = 1000 > 500, so a packet claiming segmentCount=5 is rejected
            c.Security.MaximumReassembledPacketSize = 500;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);
        server.Start();

        // Establish a connection first so the server has a known peer.
        using Socket socket = TestHarness.CreateRawSocket();
        IPEndPoint serverEndPoint = new(IPAddress.Loopback, port);
        socket.SendTo(new byte[] { 0x03 }, serverEndPoint); // PacketType.Handshake
        Assert.True(TestHarness.PumpUntil(() => eventRecorder.ConnectionsEstablished >= 1, 2000, server),
            "connection should have been established");

        // Craft a raw segmented packet:
        // PacketType.Segmented = 6 = 0x06
        // Header: [type=0x06, segmentId_lo=0x01, segmentId_hi=0x00, segmentIndex=0x00, segmentCount=0x05]
        // segmentCount=5, MTU=200 => 5*200=1000 > MaximumReassembledPacketSize=500 => blacklist
        byte[] segmentedPacket = [0x06, 0x01, 0x00, 0x00, 0x05, 0xAA, 0xBB, 0xCC];
        socket.SendTo(segmentedPacket, serverEndPoint);

        Assert.True(TestHarness.PumpUntil(
                () => eventRecorder.ViolationReasons.Contains(ViolationReason.Oversized), 3000, server),
            "expected an Oversized violation for a segment assembly exceeding MaximumReassembledPacketSize");
    }
}
