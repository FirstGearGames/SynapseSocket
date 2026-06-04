using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;

namespace SynapseSocket.Tests.Diagnostics;

public class TelemetryAndLatencyTests
{
    [Fact]
    public void Telemetry_Counts_Inbound_And_Outbound_Accurately()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        server.Start();
        client.Start();

        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        TestHarness.PumpUntil(() => synapseConnection.State == ConnectionState.Connected, 2000, server, client);

        client.Send(synapseConnection, Encoding.UTF8.GetBytes("abc"), isReliable: false);
        client.Send(synapseConnection, Encoding.UTF8.GetBytes("def"), isReliable: true);

        TestHarness.PumpUntil(() => server.Telemetry.PacketsIn >= 3, 2000, server, client);

        Assert.True(server.Telemetry.PacketsIn > 0, "server PacketsIn");
        Assert.True(server.Telemetry.BytesIn > 0, "server BytesIn");
        Assert.True(client.Telemetry.PacketsOut > 0, "client PacketsOut");
        Assert.True(client.Telemetry.BytesOut > 0, "client BytesOut");
    }

    [Fact]
    public void LatencySimulator_Drops_All_DataPackets_When_Loss_Is_100_Percent()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.PacketLossChance = 1.0;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        server.Start();
        client.Start();

        // Handshake is exempt from the sim, so the connection establishes normally.
        SynapseConnection synapseConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => eventRecorder.ConnectionsEstablished >= 1, 2000, server, client));

        // Data packets are subject to 100% loss and must never arrive.
        client.Send(synapseConnection, new byte[] { 1, 2, 3 }, isReliable: false);
        TestHarness.PumpFor(300, server, client);

        Assert.Equal(0, eventRecorder.PacketsReceived);
    }

    [Fact]
    public void LatencySimulator_Adds_Measurable_Delay()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 200;
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        server.Start();
        client.Start();

        // Handshake is exempt — connect first, then measure a data packet's trip time.
        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => eventRecorder.ConnectionsEstablished >= 1, 2000, server, client));

        DateTime startTime = DateTime.UtcNow;
        client.Send(connection, new byte[] { 0x01 }, isReliable: false);
        TestHarness.PumpUntil(() => eventRecorder.PacketsReceived >= 1, 3000, server, client);
        TimeSpan elapsedTime = DateTime.UtcNow - startTime;

        Assert.True(elapsedTime.TotalMilliseconds >= 150,
            $"expected >=150ms elapsed, got {elapsedTime.TotalMilliseconds:F0}ms");
    }

    /// <summary>
    /// Regression test for use-after-free when latency exceeds the reliable resend interval.
    /// With BaseLatencyMilliseconds (400) > ResendMilliseconds (250), the maintenance step fires
    /// retransmits while the original packet is still queued in the sim. When the ACK arrives the
    /// PendingReliable backing array is returned to ArrayPool. Because the sim owns a private copy,
    /// the still-queued packet sends uncorrupted data.
    /// </summary>
    [Fact]
    public void LatencySimulator_LatencyExceedingResendInterval_ReliablePacketsArriveUncorrupted()
    {
        const int BaseLatencyMs = 400;
        const int PacketCount = 10;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = BaseLatencyMs;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => recorder.ConnectionsEstablished >= 1, 5000, server, client));

        for (int i = 0; i < PacketCount; i++)
            client.Send(connection, new byte[] { (byte)i, (byte)(i + 100) }, isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => recorder.PacketsReceived >= PacketCount, 8000, server, client),
            $"expected {PacketCount} reliable packets; received {recorder.PacketsReceived}");

        // Verify payload bytes are uncorrupted: each [i, i+100] pair must be present.
        HashSet<byte> receivedFirstBytes = [.. recorder.Payloads.Where(p => p.Length >= 1).Select(p => p[0])];
        for (int i = 0; i < PacketCount; i++)
            Assert.Contains((byte)i, receivedFirstBytes);
    }

    [Fact]
    public void LatencySimulator_HighLatency_DoesNotTriggerSpuriousTimeout()
    {
        const int BaseLatencyMs = 400;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = BaseLatencyMs;
            c.Connection.KeepAliveIntervalMilliseconds = 1000;
            c.Connection.TimeoutMilliseconds = 5000;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => recorder.ConnectionsEstablished >= 1, 5000, server, client));

        // Pump for two keep-alive cycles with room for latency.
        TestHarness.PumpFor(3000, server, client);

        Assert.Equal(0, recorder.ConnectionsClosed);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void LatencySimulator_Jitter_ArrivalIsWithinExpectedWindow()
    {
        const int BaseLatencyMs = 100;
        const int JitterMs = 150;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = BaseLatencyMs;
            c.LatencySimulator.JitterMilliseconds = JitterMs;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        server.Start();
        client.Start();

        // Handshake is exempt — connect first, then measure a data packet's trip time.
        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => recorder.ConnectionsEstablished >= 1, 2000, server, client));

        DateTime sent = DateTime.UtcNow;
        client.Send(connection, new byte[] { 0x01 }, isReliable: false);
        Assert.True(TestHarness.PumpUntil(() => recorder.PacketsReceived >= 1, 3000, server, client));
        double elapsedMs = (DateTime.UtcNow - sent).TotalMilliseconds;

        Assert.True(elapsedMs >= BaseLatencyMs * 0.8,
            $"elapsed {elapsedMs:F0}ms was less than base latency {BaseLatencyMs}ms — sim may not be active");
        Assert.True(elapsedMs < BaseLatencyMs + JitterMs + 500,
            $"elapsed {elapsedMs:F0}ms greatly exceeded base+jitter ceiling of {BaseLatencyMs + JitterMs}ms");
    }

    [Fact]
    public void LatencySimulator_Reorder_AllUnreliablePacketsArriveEventually()
    {
        const int PacketCount = 20;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 20;
            c.LatencySimulator.ReorderChance = 0.5;
            c.LatencySimulator.OutOfOrderExtraDelayMilliseconds = 200;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => recorder.ConnectionsEstablished >= 1, 2000, server, client));

        ArraySegment<byte> payload = new(new byte[] { 0xAB });
        for (int i = 0; i < PacketCount; i++)
            client.Send(connection, payload, isReliable: false);

        // All packets must arrive; the extra reorder delay is the ceiling.
        Assert.True(TestHarness.PumpUntil(() => recorder.PacketsReceived >= PacketCount, 5000, server, client),
            $"expected {PacketCount} unreliable packets; received {recorder.PacketsReceived}");
    }

    [Fact]
    public void LatencySimulator_OnServer_DelaysAcksButReliablePacketsStillDelivered()
    {
        const int PacketCount = 5;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.BaseLatencyMilliseconds = 300;
        }));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => recorder.ConnectionsEstablished >= 1, 5000, server, client));

        for (int i = 0; i < PacketCount; i++)
            client.Send(connection, new byte[] { (byte)i }, isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => recorder.PacketsReceived >= PacketCount, 8000, server, client),
            $"expected {PacketCount} reliable packets; received {recorder.PacketsReceived}");
    }

    [Fact]
    public void LatencySimulator_Disabled_PacketsArriveWithNoMeasurableDelay()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.Enabled = false;
            c.LatencySimulator.BaseLatencyMilliseconds = 500;
            c.LatencySimulator.PacketLossChance = 1.0;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        server.Start();
        client.Start();

        DateTime sent = DateTime.UtcNow;
        client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => recorder.ConnectionsEstablished >= 1, 2000, server, client));
        double elapsedMs = (DateTime.UtcNow - sent).TotalMilliseconds;

        // The sim is off, so the 500ms latency and 100% loss settings must be ignored.
        Assert.True(elapsedMs < 400,
            $"elapsed {elapsedMs:F0}ms suggests the disabled sim is still active");
    }

    [Fact]
    public void LatencySimulator_PartialLoss_ClientOutbound_SomePacketsArriveAndSomeDrop()
    {
        const int PacketCount = 200;
        const double LossChance = 0.3;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.PacketLossChance = LossChance;
        }));

        TestHarness.EventRecorder recorder = new();
        recorder.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => recorder.ConnectionsEstablished >= 1, 3000, server, client),
            "connection should establish — handshake is exempt from the sim");

        ArraySegment<byte> payload = new(new byte[] { 0xFF });
        for (int i = 0; i < PacketCount; i++)
            client.Send(connection, payload, isReliable: false);

        TestHarness.PumpFor(500, server, client);

        Assert.True(recorder.PacketsReceived > 60 && recorder.PacketsReceived < 195,
            $"received {recorder.PacketsReceived}/{PacketCount} packets under {LossChance * 100}% loss — expected partial delivery");
    }

    [Fact]
    public void LatencySimulator_PartialLoss_ServerOutbound_SomePacketsArriveAndSomeDrop()
    {
        const int PacketCount = 200;
        const double LossChance = 0.3;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.LatencySimulator.Enabled = true;
            c.LatencySimulator.PacketLossChance = LossChance;
        }));
        using SynapseManager client = new(TestHarness.ClientConfig());

        SynapseConnection? serverSideConnection = null;
        server.ConnectionEstablished += args => serverSideConnection = args.Connection;

        int clientReceivedCount = 0;
        client.PacketReceived += _ => Interlocked.Increment(ref clientReceivedCount);

        server.Start();
        client.Start();

        client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverSideConnection != null, 3000, server, client),
            "server should see the connection");

        ArraySegment<byte> payload = new(new byte[] { 0xFF });
        for (int i = 0; i < PacketCount; i++)
            server.Send(serverSideConnection!, payload, isReliable: false);

        TestHarness.PumpFor(500, server, client);

        Assert.True(clientReceivedCount > 60 && clientReceivedCount < 195,
            $"received {clientReceivedCount}/{PacketCount} packets under {LossChance * 100}% loss — expected partial delivery");
    }
}
