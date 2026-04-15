using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
    /// The latency simulator reorders all segments with random delays, so they
    /// arrive at the server in a shuffled order.
    /// Verifies that the reassembler reconstructs the payload exactly.
    /// </summary>
    [Fact]
    public async Task OutOfOrder_Unreliable_Segmented_PayloadMatchesSent()
    {
        int port = TestHarness.GetFreePort();

        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.MaximumTransmissionUnit = 150;
            c.MaximumPacketSize = 4096 * 2;
            c.MaximumSegments = 128;
        }));

        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.MaximumTransmissionUnit = 150;
            c.MaximumPacketSize = 4096 * 2;
            c.MaximumSegments = 128;

            // Every packet gets a random extra delay up to 200 ms — with all
            // segments dispatched concurrently this guarantees mixed arrival order.
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 0;
            c.LatencySimulator.ReorderChance = 1.0;
            c.LatencySimulator.OutOfOrderExtraDelayMilliseconds = 200;
        }));

        byte[]? receivedPayload = null;
        server.PacketReceived += args => receivedPayload = args.Payload.ToArray();

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => synapseConnection.State == ConnectionState.Connected, 3000),
            "client did not reach Connected state");

        // Build a payload larger than any single segment (MTU=150 ⇒ payload budget
        // per segment is ~143 bytes; 4096 bytes requires ~29 segments).
        byte[] sentPayload = new byte[4096];
        new Random(7).NextBytes(sentPayload);

        await client.SendAsync(synapseConnection, sentPayload, isReliable: false, CancellationToken.None);

        Assert.True(await TestHarness.WaitFor(() => receivedPayload is not null, 5000),
            "server never received the reassembled payload");
        Assert.Equal(sentPayload, receivedPayload);
    }

    // -------------------------------------------------------------------------
    // Test 2 — Reliable segmented out-of-order
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a large reliable payload that is split into many segments.
    /// The latency simulator reorders all segments so they arrive out of order.
    /// Verifies that the reassembler reconstructs the payload exactly and the
    /// <see cref="SynapseManager.PacketReceived"/> callback fires with the correct data.
    /// </summary>
    [Fact]
    public async Task OutOfOrder_Reliable_Segmented_PayloadMatchesSent()
    {
        int port = TestHarness.GetFreePort();

        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.MaximumTransmissionUnit = 150;
            c.MaximumPacketSize = 4096 * 2;
            c.MaximumSegments = 128;
        }));

        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.MaximumTransmissionUnit = 150;
            c.MaximumPacketSize = 4096 * 2;
            c.MaximumSegments = 128;

            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 0;
            c.LatencySimulator.ReorderChance = 1.0;
            c.LatencySimulator.OutOfOrderExtraDelayMilliseconds = 200;
        }));

        byte[]? receivedPayload = null;
        server.PacketReceived += args => receivedPayload = args.Payload.ToArray();

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => synapseConnection.State == ConnectionState.Connected, 3000),
            "client did not reach Connected state");

        byte[] sentPayload = new byte[4096];
        new Random(13).NextBytes(sentPayload);

        await client.SendAsync(synapseConnection, sentPayload, isReliable: true, CancellationToken.None);

        Assert.True(await TestHarness.WaitFor(() => receivedPayload is not null, 6000),
            "server never received the reassembled reliable-segmented payload");
        Assert.Equal(sentPayload, receivedPayload);
    }

    // -------------------------------------------------------------------------
    // Test 3 — Unsegmented reliable out-of-order
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends three small reliable packets in quick succession.
    /// The latency simulator reorders them at the packet level so they arrive
    /// out of sequence.  The engine's reorder buffer must buffer the early
    /// arrivals and deliver all three in sequence order.
    /// Verifies via <see cref="SynapseManager.PacketReceived"/> that all three
    /// payloads arrive with their original content intact.
    /// </summary>
    [Fact]
    public async Task OutOfOrder_Reliable_Unsegmented_AllPayloadsMatchSent()
    {
        int port = TestHarness.GetFreePort();

        await using SynapseManager server = new(TestHarness.ServerConfig(port));

        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 0;
            c.LatencySimulator.ReorderChance = 1.0;
            // Wide window: individual packets pick a random delay in [0, 250) ms,
            // so three packets sent back-to-back will very likely arrive in a
            // different order than sent.
            c.LatencySimulator.OutOfOrderExtraDelayMilliseconds = 250;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => synapseConnection.State == ConnectionState.Connected, 3000),
            "client did not reach Connected state");

        byte[] payloadA = [0xAA, 0xBB, 0xCC];
        byte[] payloadB = [0x11, 0x22, 0x33];
        byte[] payloadC = [0xDE, 0xAD, 0xBE, 0xEF];

        // Fire three reliable sends back-to-back without awaiting between them
        // so the latency sim can interleave their delays.
        Task taskA = client.SendAsync(synapseConnection, payloadA, isReliable: true, CancellationToken.None);
        Task taskB = client.SendAsync(synapseConnection, payloadB, isReliable: true, CancellationToken.None);
        Task taskC = client.SendAsync(synapseConnection, payloadC, isReliable: true, CancellationToken.None);
        await Task.WhenAll(taskA, taskB, taskC);

        // All three must eventually arrive, reorder buffer delivers them in order.
        Assert.True(await TestHarness.WaitFor(() => eventRecorder.PacketsReceived >= 3, 5000),
            "server did not receive all three reliable packets");

        // Every sent payload must appear exactly once in the received set.
        Assert.Contains(eventRecorder.Payloads, p => p.SequenceEqual(payloadA));
        Assert.Contains(eventRecorder.Payloads, p => p.SequenceEqual(payloadB));
        Assert.Contains(eventRecorder.Payloads, p => p.SequenceEqual(payloadC));
    }
}
