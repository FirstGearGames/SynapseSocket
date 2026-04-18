using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;

namespace SynapseSocket.Tests.Diagnostics;

public class TelemetryAndLatencyTests
{
    [Fact]
    public async Task Telemetry_Counts_Inbound_And_Outbound_Accurately()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig());

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        await TestHarness.WaitFor(() => synapseConnection.State == ConnectionState.Connected);

        await client.SendAsync(synapseConnection, Encoding.UTF8.GetBytes("abc"), isReliable: false, CancellationToken.None);
        await client.SendAsync(synapseConnection, Encoding.UTF8.GetBytes("def"), isReliable: true, CancellationToken.None);

        await TestHarness.WaitFor(() => server.Telemetry.PacketsIn >= 3);

        Assert.True(server.Telemetry.PacketsIn > 0, "server PacketsIn");
        Assert.True(server.Telemetry.BytesIn > 0, "server BytesIn");
        Assert.True(client.Telemetry.PacketsOut > 0, "client PacketsOut");
        Assert.True(client.Telemetry.BytesOut > 0, "client BytesOut");
    }

    [Fact]
    public async Task LatencySimulator_Drops_All_DataPackets_When_Loss_Is_100_Percent()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.PacketLossChance = 1.0;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        // Handshake is exempt from the sim, so the connection establishes normally.
        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => eventRecorder.ConnectionsEstablished >= 1, 2000));

        // Data packets are subject to 100% loss and must never arrive.
        await client.SendAsync(synapseConnection, new byte[] { 1, 2, 3 }, isReliable: false, CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(0, eventRecorder.PacketsReceived);
    }

    [Fact(Timeout = 5000)]
    public async Task LatencySimulator_Adds_Measurable_Delay()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 200;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        // Handshake is exempt — connect first, then measure a data packet's trip time.
        SynapseConnection connection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => eventRecorder.ConnectionsEstablished >= 1, 2000));

        DateTime startTime = DateTime.UtcNow;
        await client.SendAsync(connection, new byte[] { 0x01 }, isReliable: false, CancellationToken.None);
        await TestHarness.WaitFor(() => eventRecorder.PacketsReceived >= 1, 3000);
        TimeSpan elapsedTime = DateTime.UtcNow - startTime;

        Assert.True(elapsedTime.TotalMilliseconds >= 150,
            $"expected >=150ms elapsed, got {elapsedTime.TotalMilliseconds:F0}ms");
    }

    /// <summary>
    /// Regression test for use-after-free when latency exceeds the reliable resend interval.
    /// With BaseLatencyMilliseconds (400) > ResendMilliseconds (250), the maintenance loop fires
    /// retransmits while the original packet is still queued in the sim. When the ACK arrives
    /// the PendingReliable backing array is returned to ArrayPool. Without the sim owning a private
    /// copy, the in-flight retransmit tasks would send corrupted data from the recycled buffer.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task LatencySimulator_LatencyExceedingResendInterval_ReliablePacketsArriveUncorrupted()
    {
        const int BaseLatencyMs = 400;
        const int PacketCount = 10;

        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = BaseLatencyMs;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection connection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => recorder.ConnectionsEstablished >= 1, 5000));

        for (int i = 0; i < PacketCount; i++)
            await client.SendAsync(connection, new byte[] { (byte)i, (byte)(i + 100) }, isReliable: true, CancellationToken.None);

        Assert.True(await TestHarness.WaitFor(() => recorder.PacketsReceived >= PacketCount, 8000),
            $"expected {PacketCount} reliable packets; received {recorder.PacketsReceived}");

        // Verify payload bytes are uncorrupted: each [i, i+100] pair must be present.
        HashSet<byte> receivedFirstBytes = [.. recorder.Payloads.Where(p => p.Length >= 1).Select(p => p[0])];
        for (int i = 0; i < PacketCount; i++)
            Assert.Contains((byte)i, receivedFirstBytes);
    }

    [Fact(Timeout = 10000)]
    public async Task LatencySimulator_HighLatency_DoesNotTriggerSpuriousTimeout()
    {
        const int BaseLatencyMs = 400;

        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = BaseLatencyMs;
            c.Connection.KeepAliveIntervalMilliseconds = 1000;
            c.Connection.TimeoutMilliseconds = 5000;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection connection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => recorder.ConnectionsEstablished >= 1, 5000));

        // Wait for two keep-alive cycles with room for latency.
        await Task.Delay(3000);

        Assert.Equal(0, recorder.ConnectionsClosed);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact(Timeout = 5000)]
    public async Task LatencySimulator_Jitter_ArrivalIsWithinExpectedWindow()
    {
        const int BaseLatencyMs = 100;
        const int JitterMs = 150;

        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = BaseLatencyMs;
            c.LatencySimulator.JitterMilliseconds = JitterMs;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        // Handshake is exempt — connect first, then measure a data packet's trip time.
        SynapseConnection connection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => recorder.ConnectionsEstablished >= 1, 2000));

        DateTime sent = DateTime.UtcNow;
        await client.SendAsync(connection, new byte[] { 0x01 }, isReliable: false, CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => recorder.PacketsReceived >= 1, 3000));
        double elapsedMs = (DateTime.UtcNow - sent).TotalMilliseconds;

        Assert.True(elapsedMs >= BaseLatencyMs * 0.8,
            $"elapsed {elapsedMs:F0}ms was less than base latency {BaseLatencyMs}ms — sim may not be active");
        Assert.True(elapsedMs < BaseLatencyMs + JitterMs + 500,
            $"elapsed {elapsedMs:F0}ms greatly exceeded base+jitter ceiling of {BaseLatencyMs + JitterMs}ms");
    }

    [Fact(Timeout = 10000)]
    public async Task LatencySimulator_Reorder_AllUnreliablePacketsArriveEventually()
    {
        const int PacketCount = 20;

        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 20;
            c.LatencySimulator.ReorderChance = 0.5;
            c.LatencySimulator.OutOfOrderExtraDelayMilliseconds = 200;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection connection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => recorder.ConnectionsEstablished >= 1, 2000));

        ArraySegment<byte> payload = new(new byte[] { 0xAB });
        for (int i = 0; i < PacketCount; i++)
            await client.SendAsync(connection, payload, isReliable: false, CancellationToken.None);

        // All packets must arrive; the extra reorder delay is the ceiling.
        Assert.True(await TestHarness.WaitFor(() => recorder.PacketsReceived >= PacketCount, 5000),
            $"expected {PacketCount} unreliable packets; received {recorder.PacketsReceived}");
    }

    [Fact(Timeout = 10000)]
    public async Task LatencySimulator_OnServer_DelaysAcksButReliablePacketsStillDelivered()
    {
        const int PacketCount = 5;

        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 300;
        }));
        await using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection connection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => recorder.ConnectionsEstablished >= 1, 5000));

        for (int i = 0; i < PacketCount; i++)
            await client.SendAsync(connection, new byte[] { (byte)i }, isReliable: true, CancellationToken.None);

        Assert.True(await TestHarness.WaitFor(() => recorder.PacketsReceived >= PacketCount, 8000),
            $"expected {PacketCount} reliable packets; received {recorder.PacketsReceived}");
    }

    [Fact(Timeout = 5000)]
    public async Task LatencySimulator_Disabled_PacketsArriveWithNoMeasurableDelay()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = false;
            c.LatencySimulator.BaseLatencyMilliseconds = 500;
            c.LatencySimulator.PacketLossChance = 1.0;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        DateTime sent = DateTime.UtcNow;
        SynapseConnection _ = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => recorder.ConnectionsEstablished >= 1, 2000));
        double elapsedMs = (DateTime.UtcNow - sent).TotalMilliseconds;

        // The sim is off, so the 500ms latency and 100% loss settings must be ignored.
        Assert.True(elapsedMs < 400,
            $"elapsed {elapsedMs:F0}ms suggests the disabled sim is still active");
    }

    [Fact(Timeout = 8000)]
    public async Task LatencySimulator_PartialLoss_ClientOutbound_SomePacketsArriveAndSomeDrop()
    {
        const int PacketCount = 200;
        const double LossChance = 0.3;

        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.PacketLossChance = LossChance;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        SynapseConnection connection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => recorder.ConnectionsEstablished >= 1, 3000),
            "connection should establish — handshake is exempt from the sim");

        ArraySegment<byte> payload = new(new byte[] { 0xFF });
        for (int i = 0; i < PacketCount; i++)
            await client.SendAsync(connection, payload, isReliable: false, CancellationToken.None);

        await Task.Delay(500);

        Assert.True(recorder.PacketsReceived > 60 && recorder.PacketsReceived < 195,
            $"received {recorder.PacketsReceived}/{PacketCount} packets under {LossChance * 100}% loss — expected partial delivery");
    }

    [Fact(Timeout = 8000)]
    public async Task LatencySimulator_PartialLoss_ServerOutbound_SomePacketsArriveAndSomeDrop()
    {
        const int PacketCount = 200;
        const double LossChance = 0.3;

        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.PacketLossChance = LossChance;
        }));
        await using SynapseManager client = new(TestHarness.ClientConfig());

        SynapseConnection? serverSideConnection = null;
        server.ConnectionEstablished += args => serverSideConnection = args.Connection;

        int clientReceivedCount = 0;
        client.PacketReceived += _ => Interlocked.Increment(ref clientReceivedCount);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => serverSideConnection != null, 3000),
            "server should see the connection");

        ArraySegment<byte> payload = new(new byte[] { 0xFF });
        for (int i = 0; i < PacketCount; i++)
            await server.SendAsync(serverSideConnection!, payload, isReliable: false, CancellationToken.None);

        await Task.Delay(500);

        Assert.True(clientReceivedCount > 60 && clientReceivedCount < 195,
            $"received {clientReceivedCount}/{PacketCount} packets under {LossChance * 100}% loss — expected partial delivery");
    }
}
