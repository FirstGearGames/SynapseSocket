using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Xunit;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;
using SynapseSocket.Packets;
using SynapseSocket.Security;

namespace SynapseSocket.Tests.Transport;

/// <summary>
/// End-to-end coverage for <see cref="IPacketTransform"/> over real loopback UDP sockets.
/// Every test drives two live engines through <see cref="TestHarness"/> and uses only the public API, so the transform
/// is exercised on the same path production traffic takes.
/// </summary>
public class PacketTransformTests
{
    /// <summary>
    /// The configured MTU the reservation tests measure against.
    /// </summary>
    private const uint TestMaximumTransmissionUnit = 1400;

    /// <summary>
    /// Proves an unreliable and a reliable payload both survive a full transform and reverse across live sockets.
    /// </summary>
    [Fact]
    public void Transform_Round_Trips_Unreliable_And_Reliable_Payloads()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, ApplyTransform));
        using SynapseManager client = new(TestHarness.ClientConfig(ApplyTransform));

        List<byte[]> receivedPayloads = [];
        server.PacketReceived += packetReceivedEventArgs => receivedPayloads.Add(packetReceivedEventArgs.Payload.ToArray());

        SynapseConnection synapseConnection = ConnectPeers(server, client, port);

        byte[] unreliablePayload = MakePayload(64, seed: 1);
        byte[] reliablePayload = MakePayload(512, seed: 2);

        client.Send(synapseConnection, unreliablePayload, isReliable: false);
        client.Send(synapseConnection, reliablePayload, isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => receivedPayloads.Count == 2, 5000, server, client), "server never received both transformed payloads");
        Assert.Equal(unreliablePayload, receivedPayloads[0]);
        Assert.Equal(reliablePayload, receivedPayloads[1]);
    }

    /// <summary>
    /// Proves a payload large enough to segment is transformed and reversed per segment and still reassembles byte for byte.
    /// </summary>
    [Fact]
    public void Transform_Round_Trips_A_Segmented_Payload()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, ApplyTransform));
        using SynapseManager client = new(TestHarness.ClientConfig(ApplyTransform));

        byte[]? receivedPayload = null;
        server.PacketReceived += packetReceivedEventArgs => receivedPayload = packetReceivedEventArgs.Payload.ToArray();

        SynapseConnection synapseConnection = ConnectPeers(server, client, port);

        // Comfortably more than the effective MTU, so the splitter produces many segments and each one is transformed
        // and reversed independently.
        byte[] payload = MakePayload(20_000, seed: 3);
        client.Send(synapseConnection, payload, isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => receivedPayload is not null, 10_000, server, client), "server never reassembled the transformed segmented payload");
        Assert.Equal(payload, receivedPayload);
    }

    /// <summary>
    /// Proves the transform's reserved bytes are deducted from the configured MTU in what the engine reports.
    /// </summary>
    [Fact]
    public void Reserved_Bytes_Reduce_The_Reported_Mtu_And_Payload_Size()
    {
        MarkerXorPacketTransform markerXorPacketTransform = new();
        using SynapseManager engine = new(TestHarness.ClientConfig(ApplyTransform));

        uint expectedMaximumTransmissionUnit = TestMaximumTransmissionUnit - markerXorPacketTransform.ReservedBytes;

        Assert.Equal(TestMaximumTransmissionUnit, engine.Config.MaximumTransmissionUnit);
        Assert.Equal(expectedMaximumTransmissionUnit, engine.MaximumTransmissionUnit);
        Assert.Equal((int)expectedMaximumTransmissionUnit - PacketHeader.TypeSize - PacketHeader.SequenceSize, engine.MaximumPayloadSize);
    }

    /// <summary>
    /// Proves the reported payload size is the precise point at which a send stops fitting in one packet.
    /// </summary>
    [Fact]
    public void Reported_Payload_Size_Is_The_Exact_Unsegmented_Boundary()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, ApplyTransform));
        using SynapseManager client = new(TestHarness.ClientConfig(synapseConfig =>
        {
            ApplyTransform(synapseConfig);
            // With segmentation off, anything over the reported payload size must throw, which pins the boundary exactly.
            synapseConfig.Segment.ReliableEnabled = false;
            synapseConfig.Segment.UnreliableMode = UnreliableSegmentMode.Disabled;
        }));

        byte[]? receivedPayload = null;
        server.PacketReceived += packetReceivedEventArgs => receivedPayload = packetReceivedEventArgs.Payload.ToArray();

        SynapseConnection synapseConnection = ConnectPeers(server, client, port);

        byte[] exactPayload = MakePayload(client.MaximumPayloadSize, seed: 4);
        client.Send(synapseConnection, exactPayload, isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => receivedPayload is not null, 5000, server, client), "a payload of exactly MaximumPayloadSize was not delivered unsegmented");
        Assert.Equal(exactPayload, receivedPayload);

        Assert.Throws<InvalidOperationException>(() => client.Send(synapseConnection, MakePayload(client.MaximumPayloadSize + 1, seed: 5), isReliable: true));
    }

    /// <summary>
    /// Proves no transformed datagram outgrows the configured MTU, by letting the receiver reject anything that does.
    /// </summary>
    [Fact]
    public void Transformed_Datagrams_Stay_Within_The_Configured_Mtu()
    {
        int port = TestHarness.GetFreePort();

        // MaximumPacketSize is pinned to the configured MTU, so any datagram that outgrew the reservation arrives
        // oversized and raises a violation. That turns the MTU budget into an assertion the engine itself makes.
        void PinPacketSizeToMtu(SynapseConfig synapseConfig)
        {
            ApplyTransform(synapseConfig);
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

        // Every full segment leaves at exactly the effective MTU and grows by ReservedBytes, landing on the configured
        // MTU to the byte. One byte of unreserved growth would trip the oversize check below.
        byte[] payload = MakePayload(20_000, seed: 6);
        client.Send(synapseConnection, payload, isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => receivedPayload is not null, 10_000, server, client), "server never reassembled the payload");
        Assert.Equal(payload, receivedPayload);
        Assert.Empty(violationReasons);
    }

    /// <summary>
    /// Proves an untransformed peer is turned away rather than silently accepted.
    /// </summary>
    [Fact]
    public void Peer_Without_The_Transform_Is_Rejected()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, ApplyTransform));
        using SynapseManager client = new(TestHarness.ClientConfig());

        List<ViolationReason> violationReasons = [];
        server.ViolationDetected += violationEventArgs =>
        {
            violationReasons.Add(violationEventArgs.Reason);
            return violationEventArgs.Action;
        };

        bool isPayloadReceived = false;
        server.PacketReceived += _ => isPayloadReceived = true;

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));

        Assert.False(TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 1500, server, client), "an untransformed peer completed the handshake against a transformed engine");
        Assert.Contains(ViolationReason.TransformRejected, violationReasons);
        Assert.False(isPayloadReceived);
    }

    /// <summary>
    /// Proves external protocols sharing the socket still exchange their packets untouched while a transform is installed.
    /// </summary>
    [Fact]
    public void Piggybacked_Raw_Packets_Bypass_The_Transform()
    {
        int port = TestHarness.GetFreePort();

        void AllowPiggyback(SynapseConfig synapseConfig)
        {
            ApplyTransform(synapseConfig);
            synapseConfig.Security.AllowUnknownPackets = true;
        }

        using SynapseManager server = new(TestHarness.ServerConfig(port, AllowPiggyback));
        using SynapseManager client = new(TestHarness.ClientConfig(AllowPiggyback));

        byte[]? receivedRawPacket = null;
        server.UnknownPacketReceived += (_, packet) =>
        {
            receivedRawPacket = packet.ToArray();
            return FilterResult.Allowed;
        };

        ConnectPeers(server, client, port);

        // A leading byte above PacketType.NatChallenge is the contract external protocols use, and it is what keeps
        // this datagram out of the transform in both directions.
        byte[] rawPacket = [200, 1, 2, 3, 4, 5, 6, 7];
        client.SendRaw(new(IPAddress.Loopback, port), rawPacket);

        Assert.True(TestHarness.PumpUntil(() => receivedRawPacket is not null, 5000, server, client), "server never received the piggybacked raw packet");
        Assert.Equal(rawPacket, receivedRawPacket);
    }

    /// <summary>
    /// Installs the test transform and pins the MTU so the reservation arithmetic is predictable.
    /// </summary>
    /// <param name="synapseConfig">The config to apply the transform to.</param>
    private static void ApplyTransform(SynapseConfig synapseConfig)
    {
        synapseConfig.MaximumTransmissionUnit = TestMaximumTransmissionUnit;
        synapseConfig.PacketTransform = new MarkerXorPacketTransform();
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
        Assert.True(TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 5000, server, client), "peers never completed the handshake through the transform");

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

    /// <summary>
    /// A length-changing transform used to prove the pipeline end to end: it prepends a four-byte marker and XORs the
    /// payload, so a packet that skipped the transform or arrived from an untransformed peer fails the marker check.
    /// This is a test fixture and offers no security whatsoever.
    /// </summary>
    private sealed class MarkerXorPacketTransform : IPacketTransform
    {
        /// <inheritdoc/>
        public uint ReservedBytes => MarkerSize;
        /// <summary>
        /// Size in bytes of the marker prepended to every transformed payload.
        /// </summary>
        private const int MarkerSize = 4;
        /// <summary>
        /// The marker value written ahead of each transformed payload and verified on the way back in.
        /// </summary>
        private const uint Marker = 0xC0FFEE01;
        /// <summary>
        /// The byte every payload byte is XORed against.
        /// </summary>
        private const byte Key = 0x5A;

        /// <inheritdoc/>
        public bool TryTransform(PacketTransformDirection packetTransformDirection, PacketType packetType, IPEndPoint endPoint, ReadOnlySpan<byte> source, Span<byte> destination, out int writtenLength)
        {
            if (packetTransformDirection == PacketTransformDirection.Outbound)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination, Marker);

                for (int i = 0; i < source.Length; i++)
                    destination[MarkerSize + i] = (byte)(source[i] ^ Key);

                writtenLength = MarkerSize + source.Length;

                return true;
            }

            writtenLength = 0;

            if (source.Length < MarkerSize || BinaryPrimitives.ReadUInt32LittleEndian(source) != Marker)
                return false;

            ReadOnlySpan<byte> body = source[MarkerSize..];

            for (int i = 0; i < body.Length; i++)
                destination[i] = (byte)(body[i] ^ Key);

            writtenLength = body.Length;

            return true;
        }
    }
}
