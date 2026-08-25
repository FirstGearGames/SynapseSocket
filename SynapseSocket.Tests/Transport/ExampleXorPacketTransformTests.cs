using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Xunit;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;
using SynapseSocket.Packets;

namespace SynapseSocket.Tests.Transport;

/// <summary>
/// End-to-end coverage for <see cref="ExampleXorPacketTransform"/> over real loopback UDP sockets.
/// One test reads the datagrams off a plain UDP socket, so the masking and the fixed four-byte overhead are observed
/// on the wire rather than inferred from the engine's own reports.
/// </summary>
/// <remarks>
/// Nothing here tests secrecy, because the transform under test offers none by design. What is tested is the length
/// accounting, the round trip, and the untouched header.
/// </remarks>
public class ExampleXorPacketTransformTests
{
    /// <summary>
    /// The configured MTU these tests measure the reservation against.
    /// </summary>
    private const uint TestMaximumTransmissionUnit = 1400;
    /// <summary>
    /// The overhead <see cref="ExampleXorPacketTransform"/> adds to every payload: the four-byte mask.
    /// </summary>
    private const int ExpectedReservedBytes = 4;

    /// <summary>
    /// Proves the reserved overhead is the fixed four bytes the layout implies, and that the engine deducts it from the MTU.
    /// </summary>
    [Fact]
    public void Reserved_Bytes_Are_Fixed_And_Deducted_From_The_Mtu()
    {
        ExampleXorPacketTransform exampleXorPacketTransform = new();
        using SynapseManager engine = new(TestHarness.ClientConfig(Configure));

        Assert.Equal((uint)ExpectedReservedBytes, exampleXorPacketTransform.ReservedBytes);
        Assert.Equal(TestMaximumTransmissionUnit - ExpectedReservedBytes, engine.MaximumTransmissionUnit);
        Assert.Equal((int)engine.MaximumTransmissionUnit - PacketHeader.TypeSize - PacketHeader.SequenceSize, engine.MaximumPayloadSize);
    }

    /// <summary>
    /// Proves an unreliable, a reliable, and a segmented payload all unmask back to the exact bytes that were sent.
    /// </summary>
    [Fact]
    public void Masked_Payloads_Round_Trip_On_Every_Channel()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, Configure));
        using SynapseManager client = new(TestHarness.ClientConfig(Configure));

        List<byte[]> receivedPayloads = [];
        server.PacketReceived += packetReceivedEventArgs => receivedPayloads.Add(packetReceivedEventArgs.Payload.ToArray());

        SynapseConnection synapseConnection = ConnectPeers(server, client, port);

        byte[] unreliablePayload = MakePayload(64, seed: 1);
        byte[] reliablePayload = MakePayload(512, seed: 2);
        byte[] segmentedPayload = MakePayload(20_000, seed: 3);

        client.Send(synapseConnection, unreliablePayload, isReliable: false);
        client.Send(synapseConnection, reliablePayload, isReliable: true);
        client.Send(synapseConnection, segmentedPayload, isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => receivedPayloads.Count == 3, 10_000, server, client), "server never unmasked all three payloads");
        Assert.Equal(unreliablePayload, receivedPayloads[0]);
        Assert.Equal(reliablePayload, receivedPayloads[1]);
        Assert.Equal(segmentedPayload, receivedPayloads[2]);
    }

    /// <summary>
    /// Proves on the wire that the payload really is masked, that it grew by exactly the reserved bytes, that the
    /// Synapse header in front of it was left alone, and that a fresh mask is drawn per packet.
    /// </summary>
    [Fact]
    public void Wire_Datagram_Is_Masked_And_Grows_By_Exactly_The_Reserved_Bytes()
    {
        // A plain UDP socket standing in for a peer, so the bytes asserted on are the bytes that actually left the socket.
        using Socket observerSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        observerSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint observerEndPoint = (IPEndPoint)observerSocket.LocalEndPoint!;

        using SynapseManager client = new(TestHarness.ClientConfig(Configure));

        client.Start();

        SynapseConnection synapseConnection = client.Connect(observerEndPoint);

        // The same payload twice, so the two datagrams can be compared against each other.
        byte[] payload = MakePayload(64, seed: 4);
        client.Send(synapseConnection, payload, isReliable: false);
        client.Send(synapseConnection, payload, isReliable: false);

        List<byte[]> dataDatagrams = [];
        byte[] receiveBuffer = new byte[2048];

        // The handshake arrives first; the unreliable data packets are the ones carrying the payload under test.
        TestHarness.PumpUntil(() =>
        {
            while (observerSocket.Available > 0)
            {
                int length = observerSocket.Receive(receiveBuffer);

                if (receiveBuffer[0] == (byte)PacketType.None)
                    dataDatagrams.Add(receiveBuffer.AsSpan(0, length).ToArray());
            }

            return dataDatagrams.Count == 2;
        }, 5000, client);

        Assert.Equal(2, dataDatagrams.Count);

        foreach (byte[] dataDatagram in dataDatagrams)
        {
            Assert.Equal(PacketHeader.TypeSize + ExpectedReservedBytes + payload.Length, dataDatagram.Length);
            Assert.Equal((byte)PacketType.None, dataDatagram[0]);
            Assert.Equal(-1, dataDatagram.AsSpan().IndexOf(payload.AsSpan()));
        }

        Assert.NotEqual(dataDatagrams[0], dataDatagrams[1]);
    }

    /// <summary>
    /// Proves no masked datagram outgrows the configured MTU, by letting the receiver reject anything that does.
    /// </summary>
    [Fact]
    public void Masked_Datagrams_Stay_Within_The_Configured_Mtu()
    {
        int port = TestHarness.GetFreePort();

        // Pinning MaximumPacketSize to the configured MTU makes any datagram that outgrew the reservation arrive
        // oversized, so the engine itself asserts the budget.
        void PinPacketSizeToMtu(SynapseConfig synapseConfig)
        {
            Configure(synapseConfig);
            synapseConfig.MaximumPacketSize = TestMaximumTransmissionUnit;
        }

        using SynapseManager server = new(TestHarness.ServerConfig(port, PinPacketSizeToMtu));
        using SynapseManager client = new(TestHarness.ClientConfig(PinPacketSizeToMtu));

        List<ViolationReason> violationReasons = [];
        server.ViolationDetected += violationEventArgs =>
        {
            violationReasons.Add(violationEventArgs.Reason);

            return violationEventArgs.Action;
        };

        byte[]? receivedPayload = null;
        server.PacketReceived += packetReceivedEventArgs => receivedPayload = packetReceivedEventArgs.Payload.ToArray();

        SynapseConnection synapseConnection = ConnectPeers(server, client, port);

        // Full segments leave at exactly the effective MTU and grow by the reserved four, landing on the configured MTU
        // to the byte. One byte of unreserved growth would trip the oversize check below.
        byte[] payload = MakePayload(20_000, seed: 5);
        client.Send(synapseConnection, payload, isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => receivedPayload is not null, 10_000, server, client), "server never reassembled the masked payload");
        Assert.Equal(payload, receivedPayload);
        Assert.Empty(violationReasons);
    }

    /// <summary>
    /// Proves an inbound payload too short to carry a mask is rejected rather than read past its end.
    /// </summary>
    [Fact]
    public void Inbound_Payload_Shorter_Than_The_Mask_Is_Rejected()
    {
        ExampleXorPacketTransform exampleXorPacketTransform = new();
        byte[] destination = new byte[64];

        bool isTransformed = exampleXorPacketTransform.TryTransform(PacketTransformDirection.Inbound, PacketType.None, new(IPAddress.Loopback, 1), new byte[ExpectedReservedBytes - 1], destination, out int writtenLength);

        Assert.False(isTransformed);
        Assert.Equal(0, writtenLength);
    }

    /// <summary>
    /// Installs the transform and pins the MTU so the reservation arithmetic is predictable.
    /// </summary>
    /// <param name="synapseConfig">The config to apply the transform to.</param>
    private static void Configure(SynapseConfig synapseConfig)
    {
        synapseConfig.MaximumTransmissionUnit = TestMaximumTransmissionUnit;
        synapseConfig.PacketTransform = new ExampleXorPacketTransform();
    }

    /// <summary>
    /// Starts both engines, connects the client to the server, and returns the established connection.
    /// </summary>
    /// <param name="server">The listening engine.</param>
    /// <param name="client">The connecting engine.</param>
    /// <param name="port">The loopback port the server is bound to.</param>
    /// <returns>The client's connection to the server, in the connected state.</returns>
    private static SynapseConnection ConnectPeers(SynapseManager server, SynapseManager client, int port)
    {
        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 5000, server, client), "peers never completed the masked handshake");

        return synapseConnection;
    }

    /// <summary>
    /// Builds a deterministic payload of the requested length.
    /// </summary>
    /// <param name="length">Number of bytes to generate.</param>
    /// <param name="seed">Seed making each payload in a test distinct but reproducible.</param>
    /// <returns>The generated payload.</returns>
    private static byte[] MakePayload(int length, int seed)
    {
        byte[] payload = new byte[length];
        new Random(seed).NextBytes(payload);

        return payload;
    }
}
