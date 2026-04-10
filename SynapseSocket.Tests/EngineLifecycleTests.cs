using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SynapseSocket.Core;
using Xunit;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests;

public class EngineLifecycleTests
{
    [Fact]
    public async Task Engine_Starts_And_Binds_Successfully()
    {
        int port = TestHarness.GetFreePort();
        SynapseConfig synapseConfig = TestHarness.ServerConfig(port);
        await using SynapseManager engine = new(synapseConfig);

        await engine.StartAsync();

        Assert.True(engine.IsRunning);
    }

    [Fact]
    public async Task Engine_Throws_When_Started_Twice()
    {
        int port = TestHarness.GetFreePort();
        await using SynapseManager engine = new(TestHarness.ServerConfig(port));

        await engine.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.StartAsync());
    }

    [Fact]
    public void Engine_Throws_When_Config_Has_No_Bind_Endpoints()
    {
        SynapseConfig synapseConfig = new();
        Assert.Throws<ArgumentException>(() => new SynapseManager(synapseConfig));
    }

    [Fact]
    public void Engine_Throws_When_Config_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new SynapseManager(null!));
    }

    [Fact]
    public async Task Dispose_Releases_Sockets_And_Stops_Running()
    {
        int port = TestHarness.GetFreePort();
        SynapseManager engine = new(TestHarness.ServerConfig(port));
        await engine.StartAsync();
        Assert.True(engine.IsRunning);

        engine.Dispose();
        Assert.False(engine.IsRunning);

        // Port should be reusable after dispose.
        using Socket probeSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probeSocket.Bind(new IPEndPoint(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task SendReliable_Before_Start_Throws()
    {
        SynapseManager engine = new(TestHarness.ServerConfig(TestHarness.GetFreePort()));
        // Not calling StartAsync.
        Connections.SynapseConnection fakeConnection = new(new(IPAddress.Loopback, 1), 0UL);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.SendAsync(fakeConnection, new byte[] { 1, 2, 3 }, isReliable: true));
        engine.Dispose();
    }

    [Fact]
    public async Task Bind_Failure_Fires_ConnectionFailed_Event()
    {
        int port = TestHarness.GetFreePort();
        using Socket blockerSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        blockerSocket.Bind(new IPEndPoint(IPAddress.Loopback, port));

        SynapseConfig synapseConfig = TestHarness.ServerConfig(port);
        await using SynapseManager engine = new(synapseConfig);

        ConnectionRejectedReason? connectionRejectedReason = null;
        engine.ConnectionFailed += (connectionFailedEventArgs) => connectionRejectedReason = connectionFailedEventArgs.Reason;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.StartAsync());
        Assert.Equal(ConnectionRejectedReason.BindFailed, connectionRejectedReason);
    }
}
