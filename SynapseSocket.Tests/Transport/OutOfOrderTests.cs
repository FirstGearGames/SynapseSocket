using System;
using System.Linq;
using System.Net;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;

namespace SynapseSocket.Tests.Transport;

public class OutOfOrderTests
{
    // -------------------------------------------------------------------------
    // Test 1 — Unreliable segmented out-of-order
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a large unreliable payload that is split into many segments.
    /// The latency simulator reorders the segments with random delays, released across successive client polls,
    /// so they arrive at the server in a shuffled order. Verifies the reassembler reconstructs the payload exactly.
    /// </summary>
    [Fact]
    public void OutOfOrder_Unreliable_Segmented_PayloadMatchesSent()
    {
        int port = TestHarness.GetFreePort();

        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.MaximumTransmissionUnit = 150;
            c.MaximumPacketSize = 4096 * 2;
            c.Segment.MaximumSegments = 128;
        }));

        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.MaximumTransmissionUnit = 150;
            c.MaximumPacketSize = 4096 * 2;
            c.Segment.MaximumSegments = 128;

            // Every packet gets a random extra delay up to 200 ms, released over successive polls — guarantees mixed arrival order.
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 0;
            c.LatencySimulator.ReorderChance = 1.0;
            c.LatencySimulator.OutOfOrderExtraDelayMilliseconds = 200;
        }));

        byte[]? receivedPayload = null;
        server.PacketReceived += args => receivedPayload = args.Payload.ToArray();

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 3000, server, client),
            "client did not reach Connected state");

        // Build a payload larger than any single segment (MTU=150 ⇒ payload budget
        // per segment is ~143 bytes; 4096 bytes requires ~29 segments).
        byte[] sentPayload = new byte[4096];
        new Random(7).NextBytes(sentPayload);

        client.Send(synapseConnection, sentPayload, isReliable: false);

        Assert.True(TestHarness.PumpUntil(() => receivedPayload is not null, 5000, server, client),
            "server never received the reassembled payload");
        Assert.Equal(sentPayload, receivedPayload);
    }

    // -------------------------------------------------------------------------
    // Test 2 — Reliable segmented out-of-order
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a large reliable payload that is split into many segments.
    /// The latency simulator reorders all segments so they arrive out of order.
    /// Verifies that the reassembler reconstructs the payload exactly.
    /// </summary>
    [Fact]
    public void OutOfOrder_Reliable_Segmented_PayloadMatchesSent()
    {
        int port = TestHarness.GetFreePort();

        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.MaximumTransmissionUnit = 150;
            c.MaximumPacketSize = 4096 * 2;
            c.Segment.MaximumSegments = 128;
        }));

        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.MaximumTransmissionUnit = 150;
            c.MaximumPacketSize = 4096 * 2;
            c.Segment.MaximumSegments = 128;

            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 0;
            c.LatencySimulator.ReorderChance = 1.0;
            c.LatencySimulator.OutOfOrderExtraDelayMilliseconds = 200;
        }));

        byte[]? receivedPayload = null;
        server.PacketReceived += args => receivedPayload = args.Payload.ToArray();

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 3000, server, client),
            "client did not reach Connected state");

        byte[] sentPayload = new byte[4096];
        new Random(13).NextBytes(sentPayload);

        client.Send(synapseConnection, sentPayload, isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => receivedPayload is not null, 6000, server, client),
            "server never received the reassembled reliable-segmented payload");
        Assert.Equal(sentPayload, receivedPayload);
    }

    // -------------------------------------------------------------------------
    // Test 3 — Unsegmented reliable out-of-order
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends three small reliable packets in quick succession. The latency simulator reorders them at the packet
    /// level so they arrive out of sequence. The engine's reorder buffer must buffer the early arrivals and deliver
    /// all three in sequence order. Verifies all three payloads arrive with their original content intact.
    /// </summary>
    [Fact]
    public void OutOfOrder_Reliable_Unsegmented_AllPayloadsMatchSent()
    {
        int port = TestHarness.GetFreePort();

        using SynapseManager server = new(TestHarness.ServerConfig(port));

        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 0;
            c.LatencySimulator.ReorderChance = 1.0;
            // Wide window: individual packets pick a random delay in [0, 250) ms,
            // so three packets sent back-to-back will very likely arrive in a
            // different order than sent.
            c.LatencySimulator.OutOfOrderExtraDelayMilliseconds = 250;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 3000, server, client),
            "client did not reach Connected state");

        byte[] payloadA = [0xAA, 0xBB, 0xCC];
        byte[] payloadB = [0x11, 0x22, 0x33];
        byte[] payloadC = [0xDE, 0xAD, 0xBE, 0xEF];

        // Fire three reliable sends back-to-back so the latency sim can interleave their delays.
        client.Send(synapseConnection, payloadA, isReliable: true);
        client.Send(synapseConnection, payloadB, isReliable: true);
        client.Send(synapseConnection, payloadC, isReliable: true);

        // All three must eventually arrive; the reorder buffer delivers them in order.
        Assert.True(TestHarness.PumpUntil(() => eventRecorder.PacketsReceived >= 3, 5000, server, client),
            "server did not receive all three reliable packets");

        // Every sent payload must appear exactly once in the received set.
        Assert.Contains(eventRecorder.Payloads, p => p.SequenceEqual(payloadA));
        Assert.Contains(eventRecorder.Payloads, p => p.SequenceEqual(payloadB));
        Assert.Contains(eventRecorder.Payloads, p => p.SequenceEqual(payloadC));
    }
}
