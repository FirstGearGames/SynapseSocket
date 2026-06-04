using System;
using System.Net;
using System.Net.Sockets;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests.Lifecycle;

public class EngineLifecycleTests
{
    [Fact]
    public void Engine_Starts_And_Binds_Successfully()
    {
        int port = TestHarness.GetFreePort();
        SynapseConfig synapseConfig = TestHarness.ServerConfig(port);
        using SynapseManager engine = new(synapseConfig);

        engine.Start();

        Assert.True(engine.IsRunning);
    }

    [Fact]
    public void Engine_Throws_When_Started_Twice()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager engine = new(TestHarness.ServerConfig(port));

        engine.Start();

        Assert.Throws<InvalidOperationException>(() => engine.Start());
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
    public void Dispose_Releases_Sockets_And_Stops_Running()
    {
        int port = TestHarness.GetFreePort();
        SynapseManager engine = new(TestHarness.ServerConfig(port));
        engine.Start();
        Assert.True(engine.IsRunning);

        engine.Dispose();
        Assert.False(engine.IsRunning);

        // Port should be reusable after dispose.
        using Socket probeSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probeSocket.Bind(new IPEndPoint(IPAddress.Loopback, port));
    }

    [Fact]
    public void SendReliable_Before_Start_Throws()
    {
        SynapseManager engine = new(TestHarness.ServerConfig(TestHarness.GetFreePort()));
        // Not calling Start.
        SynapseConnection fakeConnection = new();
        fakeConnection.Initialize(new(IPAddress.Loopback, port: 1), signature: 1, connectionsIndex: SynapseConnection.UnsetConnectionsIndex);

        Assert.Throws<InvalidOperationException>(() =>
            engine.Send(fakeConnection, new byte[] { 1, 2, 3 }, isReliable: true));

        engine.Dispose();
    }

    [Fact]
    public void Bind_Failure_Fires_ConnectionFailed_Event()
    {
        int port = TestHarness.GetFreePort();
        using Socket blockerSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        blockerSocket.Bind(new IPEndPoint(IPAddress.Loopback, port));

        SynapseConfig synapseConfig = TestHarness.ServerConfig(port);
        using SynapseManager engine = new(synapseConfig);

        ConnectionRejectedReason? connectionRejectedReason = null;
        engine.ConnectionFailed += (connectionFailedEventArgs) => connectionRejectedReason = connectionFailedEventArgs.Reason;

        Assert.Throws<InvalidOperationException>(() => engine.Start());
        Assert.Equal(ConnectionRejectedReason.BindFailed, connectionRejectedReason);
    }
}
