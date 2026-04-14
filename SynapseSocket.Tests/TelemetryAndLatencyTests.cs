using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;

namespace SynapseSocket.Tests;

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
    public async Task LatencySimulator_Drops_All_Packets_When_Loss_Is_100_Percent()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager server = new(TestHarness.ServerConfig(port));
        await using SynapseManager client = new(TestHarness.ClientConfig(c =>
        {
            c.LatencySimulator.IsEnabled = true;
            c.LatencySimulator.PacketLossChance = 1.0; // everything dropped at the sender
        }));

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        // ConnectAsync sends a handshake, but it gets dropped by the latency sim.
        SynapseConnection synapseConnection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);

        // Also send data, all of which should disappear.
        await client.SendAsync(synapseConnection, new byte[] { 1, 2, 3 }, isReliable: false, CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(0, eventRecorder.PacketsReceived);
        Assert.Equal(0, eventRecorder.ConnectionsEstablished);
    }

    [Fact]
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

        DateTime startTime = DateTime.UtcNow;
        SynapseConnection _ = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        await TestHarness.WaitFor(() => eventRecorder.ConnectionsEstablished >= 1, 3000);
        TimeSpan elapsedTime = DateTime.UtcNow - startTime;

        // With 200ms base latency on the handshake, the server should see
        // the handshake at least ~150ms in (allow jitter).
        Assert.True(elapsedTime.TotalMilliseconds >= 150,
            $"expected >=150ms elapsed, got {elapsedTime.TotalMilliseconds:F0}ms");
    }
}
