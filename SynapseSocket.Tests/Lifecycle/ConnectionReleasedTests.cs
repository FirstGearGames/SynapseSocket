using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Xunit;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;
using SynapseSocket.Packets;
using SynapseSocket.Security;
using SynapseSocket.Transport;

namespace SynapseSocket.Tests.Lifecycle;

/// <summary>
/// Live-socket coverage for <see cref="SynapseManager.ConnectionReleased"/>: raised once per closed connection, after its
/// buffers are released, on every path a connection ends by.
/// </summary>
public class ConnectionReleasedTests
{
    /// <summary>
    /// How long a test waits for a handshake or a close before failing.
    /// </summary>
    private const int WaitMilliseconds = 3000;
    /// <summary>
    /// Longer than the engine's one second window in which a repeated handshake counts as an answer rather than a
    /// reconnect.
    /// </summary>
    private const int ReconnectDelayMilliseconds = 1200;

    /// <summary>
    /// A peer disconnecting raises <see cref="SynapseManager.ConnectionClosed"/> and then, once the connection's
    /// buffers have gone back to their pools, <see cref="SynapseManager.ConnectionReleased"/>, each exactly once and for
    /// the same connection.
    /// </summary>
    [Fact]
    public void Disconnect_Raises_Released_Once_After_Closed_With_Buffers_Already_Released()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);
        bool isSplitterReleasedAtEvent = false;
        server.ConnectionReleased += connectionEventArgs => isSplitterReleasedAtEvent = connectionEventArgs.Connection.Splitter is null;

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1 && clientConnection.State is ConnectionState.Connected, WaitMilliseconds, server, client), "The handshake did not complete.");

        SynapseConnection serverConnection = server.Connections.Connections[0];

        // A segmented send rents a splitter, so there is a pooled buffer for the release to return.
        server.Send(serverConnection, new byte[3000], isReliable: false);
        Assert.NotNull(serverConnection.Splitter);

        client.Disconnect(clientConnection);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Released) == 1, WaitMilliseconds, server, client), "The server never raised ConnectionReleased.");
        TestHarness.PumpFor(100, server, client);

        Assert.Equal(new[] { EventKind.Established, EventKind.Closed, EventKind.Released }, serverEventLog.Kinds);
        Assert.True(serverEventLog.IsAllFor(serverConnection), "An event carried a different connection.");
        Assert.True(isSplitterReleasedAtEvent, "ConnectionReleased was raised before the connection's splitter was released.");
    }

    /// <summary>
    /// <see cref="SynapseManager.Stop"/> raises <see cref="SynapseManager.ConnectionReleased"/> for every connection
    /// still open, without raising <see cref="SynapseManager.ConnectionClosed"/>.
    /// </summary>
    [Fact]
    public void Stop_Raises_Released_For_Open_Connections_Without_Closed()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager firstClient = new(TestHarness.ClientConfig());
        using SynapseManager secondClient = new(TestHarness.ClientConfig());

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);

        server.Start();
        firstClient.Start();
        secondClient.Start();

        firstClient.Connect(new IPEndPoint(IPAddress.Loopback, port));
        secondClient.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 2, WaitMilliseconds, server, firstClient, secondClient), "The handshakes did not complete.");

        List<SynapseConnection> openConnections = [.. server.Connections.Connections];
        server.Stop();

        Assert.Equal(2, serverEventLog.Count(EventKind.Released));
        Assert.Equal(0, serverEventLog.Count(EventKind.Closed));
        Assert.True(serverEventLog.HasReleasedEach(openConnections), "Stop did not release every open connection.");
    }

    /// <summary>
    /// <see cref="SynapseManager.Stop"/> raises <see cref="SynapseManager.ConnectionReleased"/> for every open connection
    /// while it is still intact, and then returns each one to the pool exactly once, even though the handler disconnects
    /// the connection it was given.
    /// </summary>
    [Fact]
    public void Stop_Returns_Every_Connection_Once_After_Raising_Released() => TestHarness.RunOnNewThread(AssertStopReturnsEveryConnectionOnce);

    /// <summary>
    /// A handler that disconnects another connection from inside <see cref="SynapseManager.ConnectionReleased"/> gets
    /// that connection closed and released too. Neither connection is released twice, and each goes back to the pool
    /// once, although the handler also disconnects the connection it was given.
    /// </summary>
    [Fact]
    public void Disconnect_Inside_Released_Handler_Releases_And_Returns_Each_Connection_Once() => TestHarness.RunOnNewThread(AssertDisconnectInsideReleasedHandlerReturnsEachConnectionOnce);

    /// <summary>
    /// The body of <see cref="Stop_Returns_Every_Connection_Once_After_Raising_Released"/>, run on a new thread.
    /// </summary>
    private static void AssertStopReturnsEveryConnectionOnce()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager firstClient = new(TestHarness.ClientConfig());
        using SynapseManager secondClient = new(TestHarness.ClientConfig());
        using SynapseManager thirdClient = new(TestHarness.ClientConfig());

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);
        int intactAtReleaseCount = 0;

        server.ConnectionReleased += connectionEventArgs =>
        {
            SynapseConnection releasedConnection = connectionEventArgs.Connection;

            if (releasedConnection.IsTornDown && releasedConnection.RemoteEndPoint is not null)
                intactAtReleaseCount++;

            // Already torn down, so this must do nothing, and above all must not queue a second return.
            server.Disconnect(releasedConnection);
        };

        server.Start();
        firstClient.Start();
        secondClient.Start();
        thirdClient.Start();

        firstClient.Connect(new IPEndPoint(IPAddress.Loopback, port));
        secondClient.Connect(new IPEndPoint(IPAddress.Loopback, port));
        thirdClient.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 3, WaitMilliseconds, server, firstClient, secondClient, thirdClient), "The handshakes did not complete.");

        List<SynapseConnection> openConnections = [.. server.Connections.Connections];
        server.Stop();

        Assert.Equal(3, serverEventLog.Count(EventKind.Released));
        Assert.Equal(3, intactAtReleaseCount);
        Assert.True(serverEventLog.HasReleasedEach(openConnections), "Stop did not release every open connection exactly once.");
        AssertEachReturnedOnce(openConnections);
    }

    /// <summary>
    /// The body of <see cref="Disconnect_Inside_Released_Handler_Releases_And_Returns_Each_Connection_Once"/>, run on a
    /// new thread.
    /// </summary>
    private static void AssertDisconnectInsideReleasedHandlerReturnsEachConnectionOnce()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager firstClient = new(TestHarness.ClientConfig());
        using SynapseManager secondClient = new(TestHarness.ClientConfig());

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);

        server.Start();
        firstClient.Start();
        secondClient.Start();

        SynapseConnection firstClientConnection = firstClient.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 1, WaitMilliseconds, server, firstClient), "The first handshake did not complete.");
        SynapseConnection firstServerConnection = server.Connections.Connections[0];

        secondClient.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 2, WaitMilliseconds, server, firstClient, secondClient), "The second handshake did not complete.");
        SynapseConnection secondServerConnection = server.Connections.Connections[0] == firstServerConnection ? server.Connections.Connections[1] : server.Connections.Connections[0];

        server.ConnectionReleased += connectionEventArgs =>
        {
            // Already torn down, so this must do nothing, and above all must not queue a second return.
            server.Disconnect(connectionEventArgs.Connection);

            if (connectionEventArgs.Connection == firstServerConnection)
                server.Disconnect(secondServerConnection);
        };

        firstClient.Disconnect(firstClientConnection);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Released) == 2, WaitMilliseconds, server, firstClient, secondClient), "Both connections were not released.");
        TestHarness.PumpFor(100, server, firstClient, secondClient);

        Assert.Equal(2, serverEventLog.Count(EventKind.Closed));
        Assert.Equal(2, serverEventLog.Count(EventKind.Released));
        Assert.True(serverEventLog.HasReleasedEach([firstServerConnection, secondServerConnection]), "A connection was released twice, or not at all.");
        AssertEachReturnedOnce([firstServerConnection, secondServerConnection]);
    }

    /// <summary>
    /// Asserts that each of <paramref name="releasedConnections"/> went back to this thread's connection pool exactly
    /// once, by connecting a new engine on this thread to enough endpoints to empty the pool. Each released connection
    /// must be handed out once, and no object may be handed out twice, which is what a double return would cause.
    /// </summary>
    /// <param name="releasedConnections">Connections released on this thread, which must have been returned.</param>
    /// <remarks>
    /// Only meaningful on a thread that started with an empty pool, so callers run on <see cref="TestHarness.RunOnNewThread"/>.
    /// The spare connections cover the ones the test's other engines returned on the same thread.
    /// </remarks>
    private static void AssertEachReturnedOnce(List<SynapseConnection> releasedConnections)
    {
        const int SpareConnectionCount = 8;

        using SynapseManager renter = new(TestHarness.ClientConfig());
        renter.Start();

        List<SynapseConnection> rentedConnections = [];

        for (int i = 0; i < releasedConnections.Count + SpareConnectionCount; i++)
            rentedConnections.Add(renter.Connect(new IPEndPoint(IPAddress.Loopback, TestHarness.GetFreePort())));

        Assert.True(rentedConnections.Distinct().Count() == rentedConnections.Count, "The pool handed one connection object out twice, so it had been returned twice.");

        foreach (SynapseConnection releasedConnection in releasedConnections)
            Assert.True(rentedConnections.Contains(releasedConnection), "A released connection was never returned to the pool.");
    }

    /// <summary>
    /// A peer that handshakes again from the same endpoint, without disconnecting first, has its old session closed and
    /// released before the same connection object is established for the new one.
    /// </summary>
    [Fact]
    public void Reconnect_From_Same_Endpoint_Raises_Released_Before_Reestablishing()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using Socket peer = TestHarness.CreateRawSocket();

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);

        server.Start();

        IPEndPoint serverEndPoint = new(IPAddress.Loopback, port);

        peer.SendTo(BuildHandshake(), serverEndPoint);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 1, WaitMilliseconds, server), "The first handshake did not complete.");
        SynapseConnection serverConnection = server.Connections.Connections[0];

        TestHarness.PumpFor(ReconnectDelayMilliseconds, server);
        peer.SendTo(BuildHandshake(), serverEndPoint);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 2, WaitMilliseconds, server), "The reconnect was not established.");

        Assert.Equal(new[] { EventKind.Established, EventKind.Closed, EventKind.Released, EventKind.Established }, serverEventLog.Kinds);
        Assert.True(serverEventLog.IsAllFor(serverConnection), "The reconnect did not reuse the connection object.");
    }

    /// <summary>
    /// A <see cref="SynapseManager.ConnectionClosed"/> handler that disconnects the connection a reconnect just closed
    /// ends it for good: the new session is never established, the connection is left disconnected and out of the
    /// tables, and it is released exactly once.
    /// </summary>
    [Fact]
    public void Disconnect_Inside_Closed_Handler_On_Reconnect_Does_Not_Reestablish()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using Socket peer = TestHarness.CreateRawSocket();

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);

        server.Start();

        IPEndPoint serverEndPoint = new(IPAddress.Loopback, port);

        peer.SendTo(BuildHandshake(), serverEndPoint);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 1, WaitMilliseconds, server), "The first handshake did not complete.");
        SynapseConnection serverConnection = server.Connections.Connections[0];

        server.ConnectionClosed += connectionEventArgs => server.Disconnect(connectionEventArgs.Connection);

        TestHarness.PumpFor(ReconnectDelayMilliseconds, server);
        peer.SendTo(BuildHandshake(), serverEndPoint);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Released) == 1, WaitMilliseconds, server), "The disconnected connection was never released.");
        TestHarness.PumpFor(100, server);

        Assert.Equal(1, serverEventLog.Count(EventKind.Established));
        Assert.Equal(1, serverEventLog.Count(EventKind.Released));
        Assert.Equal(ConnectionState.Disconnected, serverConnection.State);
        Assert.DoesNotContain(serverConnection, server.Connections.Connections);
        Assert.True(serverEventLog.IsAllFor(serverConnection), "An event carried a different connection.");
    }

    /// <summary>
    /// A <see cref="SynapseManager.ConnectionClosed"/> handler that disposes the engine, reached from a
    /// <see cref="SynapseManager.Disconnect"/> made outside any poll, still gets the closing connection released. The
    /// disposal drained the release queue before the connection was in it, and no poll runs after a disposal.
    /// </summary>
    [Fact]
    public void Dispose_Inside_Closed_Handler_Still_Releases_The_Closing_Connection()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);

        server.Start();
        client.Start();

        client.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 1, WaitMilliseconds, server, client), "The handshake did not complete.");
        SynapseConnection serverConnection = server.Connections.Connections[0];

        server.ConnectionClosed += _ => server.Dispose();
        server.Disconnect(serverConnection);

        Assert.Equal(new[] { EventKind.Established, EventKind.Closed, EventKind.Released }, serverEventLog.Kinds);
        Assert.True(serverEventLog.IsAllFor(serverConnection), "An event carried a different connection.");
    }

    /// <summary>
    /// A handler that stops the engine from inside <see cref="SynapseManager.PacketReceived"/> gets
    /// <see cref="SynapseManager.ConnectionReleased"/> once that poll returns, not inside the stop, because the ingress
    /// frames beneath the handler still hold the connection.
    /// </summary>
    [Fact]
    public void Stop_Inside_PacketReceived_Releases_When_The_Poll_Returns()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 1 && clientConnection.State is ConnectionState.Connected, WaitMilliseconds, server, client), "The handshake did not complete.");

        int releasedCountInsideHandler = -1;

        server.PacketReceived += _ =>
        {
            if (!server.IsRunning)
                return;

            server.Stop();
            releasedCountInsideHandler = serverEventLog.Count(EventKind.Released);
        };

        client.Send(clientConnection, new byte[] { 1 }, isReliable: true);
        Assert.True(TestHarness.PumpUntil(() => releasedCountInsideHandler >= 0, WaitMilliseconds, server, client), "The payload never arrived.");

        Assert.Equal(0, releasedCountInsideHandler);
        Assert.Equal(new[] { EventKind.Established, EventKind.Released }, serverEventLog.Kinds);
    }

    /// <summary>
    /// A <see cref="SynapseManager.ConnectionClosed"/> handler that stops the engine on the reconnect path ends the
    /// connection for good: it is released once, after the poll that carried the reconnect, and never re-established.
    /// </summary>
    [Fact]
    public void Stop_Inside_Closed_Handler_On_Reconnect_Releases_Once_When_The_Poll_Returns()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using Socket peer = TestHarness.CreateRawSocket();

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);

        server.Start();

        IPEndPoint serverEndPoint = new(IPAddress.Loopback, port);

        peer.SendTo(BuildHandshake(), serverEndPoint);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 1, WaitMilliseconds, server), "The first handshake did not complete.");
        SynapseConnection serverConnection = server.Connections.Connections[0];

        int releasedCountInsideHandler = -1;

        server.ConnectionClosed += _ =>
        {
            server.Stop();
            releasedCountInsideHandler = serverEventLog.Count(EventKind.Released);
        };

        TestHarness.PumpFor(ReconnectDelayMilliseconds, server);
        peer.SendTo(BuildHandshake(), serverEndPoint);
        Assert.True(TestHarness.PumpUntil(() => releasedCountInsideHandler >= 0, WaitMilliseconds, server), "The reconnect never closed the old session.");

        Assert.Equal(0, releasedCountInsideHandler);
        Assert.Equal(new[] { EventKind.Established, EventKind.Closed, EventKind.Released }, serverEventLog.Kinds);
        Assert.True(serverEventLog.IsAllFor(serverConnection), "An event carried a different connection.");
    }

    /// <summary>
    /// With a signature provider that mixes in the handshake payload, the peer's answering handshake produces a different
    /// signature from the one <see cref="SynapseManager.Connect"/> computed. The connection must still be listed under
    /// one signature only, and under none once it is released.
    /// </summary>
    [Fact]
    public void Payload_Mixing_Signature_Leaves_No_Signature_Entry_After_Release()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig(config => config.Security.SignatureProvider = new PayloadMixingSignatureProvider()));

        EventLog clientEventLog = new();
        clientEventLog.Attach(client);

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => clientEventLog.Count(EventKind.Established) == 1, WaitMilliseconds, server, client), "The handshake did not complete.");
        Assert.Single(client.Connections.ConnectionsBySignature);

        client.Disconnect(clientConnection);
        Assert.True(TestHarness.PumpUntil(() => clientEventLog.Count(EventKind.Released) == 1, WaitMilliseconds, server, client), "The client never released its connection.");

        Assert.Empty(client.Connections.ConnectionsBySignature);
    }

    /// <summary>
    /// With a signature provider that mixes in the handshake payload, every reconnect from the same endpoint produces a
    /// new signature. The connection that carries the new session must stay listed under one signature only, and under
    /// none once it is released.
    /// </summary>
    [Fact]
    public void Payload_Mixing_Signature_On_Reconnect_Leaves_No_Signature_Entry_After_Release()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.Security.SignatureProvider = new PayloadMixingSignatureProvider()));
        using Socket peer = TestHarness.CreateRawSocket();

        EventLog serverEventLog = new();
        serverEventLog.Attach(server);

        server.Start();

        IPEndPoint serverEndPoint = new(IPAddress.Loopback, port);

        peer.SendTo(BuildHandshake(), serverEndPoint);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 1, WaitMilliseconds, server), "The first handshake did not complete.");
        SynapseConnection serverConnection = server.Connections.Connections[0];

        TestHarness.PumpFor(ReconnectDelayMilliseconds, server);
        peer.SendTo(BuildHandshake(), serverEndPoint);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 2, WaitMilliseconds, server), "The reconnect was not established.");
        Assert.Single(server.Connections.ConnectionsBySignature);

        server.Disconnect(serverConnection);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Released) == 2, WaitMilliseconds, server), "The disconnected connection was never released.");

        Assert.Empty(server.Connections.ConnectionsBySignature);
    }

    /// <summary>
    /// A full-cone hole-punch that a <see cref="SynapseManager.ConnectionReleased"/> handler registers, by restarting the
    /// engine during <see cref="SynapseManager.Stop"/> and connecting again, survives that stop and runs to its end.
    /// </summary>
    [Fact]
    public void Punch_Registered_By_A_Released_Handler_During_Stop_Survives_The_Stop()
    {
        using SynapseManager client = new(TestHarness.ClientConfig(ConfigureShortPunch));

        TestHarness.EventRecorder clientEventRecorder = new();
        clientEventRecorder.Attach(client);

        // Nothing listens on either port, so each punch runs until its attempts are exhausted.
        IPEndPoint firstEndPoint = new(IPAddress.Loopback, TestHarness.GetFreePort());
        IPEndPoint secondEndPoint = new(IPAddress.Loopback, TestHarness.GetFreePort());
        bool isRestarted = false;

        client.ConnectionReleased += _ =>
        {
            if (isRestarted)
                return;

            isRestarted = true;
            client.Start();
            client.Connect(secondEndPoint);
        };

        client.Start();
        client.Connect(firstEndPoint);
        client.Stop();

        Assert.True(isRestarted, "Stop did not release the pending connection.");
        Assert.True(TestHarness.PumpUntil(() => clientEventRecorder.FailureReasons.Contains(ConnectionRejectedReason.NatTraversalFailed), WaitMilliseconds, client), "The punch registered during Stop was discarded by it.");
    }

    /// <summary>
    /// A <see cref="SynapseManager.ConnectionFailed"/> handler that stops the engine when a hole-punch gives up leaves the
    /// poll intact, and the pending connection is released once.
    /// </summary>
    [Fact]
    public void Stop_Inside_Nat_Failure_Handler_Leaves_The_Poll_Intact()
    {
        using SynapseManager client = new(TestHarness.ClientConfig(config =>
        {
            ConfigureShortPunch(config);
            config.NatTraversal.MaximumAttempts = 0;
        }));

        EventLog clientEventLog = new();
        clientEventLog.Attach(client);

        client.ConnectionFailed += connectionFailedEventArgs =>
        {
            if (connectionFailedEventArgs.Reason is ConnectionRejectedReason.NatTraversalFailed)
                client.Stop();
        };

        client.Start();
        client.Connect(new IPEndPoint(IPAddress.Loopback, TestHarness.GetFreePort()));

        Assert.True(TestHarness.PumpUntil(() => !client.IsRunning, WaitMilliseconds, client), "The hole-punch never gave up.");
        Assert.Equal(new[] { EventKind.Released }, clientEventLog.Kinds);
    }

    /// <summary>
    /// A <see cref="SynapseManager.ConnectionClosed"/> handler that connects to the same endpoint while
    /// <see cref="SynapseManager.Connect"/> is replacing a connection to it gets the connection the outer call returns.
    /// No second connection is made and dropped from the tables unannounced, and the new one completes its handshake.
    /// </summary>
    [Fact]
    public void Connect_Inside_Closed_Handler_Of_A_Replaced_Connection_Returns_The_Same_Connection()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        EventLog clientEventLog = new();
        clientEventLog.Attach(client);

        server.Start();
        client.Start();

        IPEndPoint serverEndPoint = new(IPAddress.Loopback, port);
        SynapseConnection firstConnection = client.Connect(serverEndPoint);
        Assert.True(TestHarness.PumpUntil(() => firstConnection.State is ConnectionState.Connected, WaitMilliseconds, server, client), "The first handshake did not complete.");

        SynapseConnection? handlerConnection = null;
        client.ConnectionClosed += connectionEventArgs =>
        {
            if (connectionEventArgs.Connection == firstConnection && handlerConnection is null)
                handlerConnection = client.Connect(serverEndPoint);
        };

        SynapseConnection outerConnection = client.Connect(serverEndPoint);

        Assert.NotNull(handlerConnection);
        Assert.Same(handlerConnection, outerConnection);
        Assert.Equal(1, client.Connections.Count);
        Assert.True(TestHarness.PumpUntil(() => outerConnection.State is ConnectionState.Connected, WaitMilliseconds, server, client), "The replacing connection never completed its handshake.");
        Assert.Equal(1, clientEventLog.Count(EventKind.Closed));
        Assert.Equal(1, clientEventLog.Count(EventKind.Released));
    }

    /// <summary>
    /// A client whose <see cref="SynapseManager.ConnectionClosed"/> handler stops or disposes its own manager, raised
    /// from inside the drain by the server's disconnect, shuts down without any exception reaching the engine, even
    /// though the drain is still reading the receive buffer the shutdown would otherwise return to the pool.
    /// </summary>
    /// <param name="isDisposing">True to dispose the client from the handler, false to stop it.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_Inside_Closed_Handler_During_Drain_Stops_Cleanly(bool isDisposing)
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        using SynapseManager client = new(TestHarness.ClientConfig());

        List<Exception> clientExceptions = [];
        client.UnhandledException += exception => clientExceptions.Add(exception);

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1 && clientConnection.State is ConnectionState.Connected, WaitMilliseconds, server, client), "The handshake did not complete.");

        // The manager drops its engines on shutdown, so the engine is held here to inspect it afterwards.
        IngressEngine clientEngine = client.IngressEngines[0];
        bool isClosedRaised = false;
        bool isBufferRentedAfterStop = false;
        client.ConnectionClosed += _ =>
        {
            isClosedRaised = true;

            if (isDisposing)
                client.Dispose();
            else
                client.Stop();

            // The drain further up the stack is still reading this buffer, so it must not be back in the pool yet.
            isBufferRentedAfterStop = clientEngine.IsReceiveBufferRented;
        };

        server.Disconnect(server.Connections.Connections[0]);
        Assert.True(TestHarness.PumpUntil(() => isClosedRaised, WaitMilliseconds, server, client), "The client never saw the disconnect.");
        TestHarness.PumpFor(100, server, client);

        Assert.Empty(clientExceptions);
        Assert.False(client.IsRunning);
        Assert.True(isBufferRentedAfterStop, "Stop returned the receive buffer while the drain was still reading it.");
        Assert.False(clientEngine.IsRunning);
        Assert.False(clientEngine.IsReceiveBufferRented, "The receive buffer was never returned once the drain unwound.");
    }

    /// <summary>
    /// Configures full-cone traversal with a hole-punch schedule short enough to finish well inside a test.
    /// </summary>
    /// <param name="synapseConfig">The configuration to adjust.</param>
    private static void ConfigureShortPunch(SynapseConfig synapseConfig)
    {
        synapseConfig.NatTraversal.Mode = NatTraversalMode.FullCone;
        synapseConfig.NatTraversal.FullCone.DirectAttemptMilliseconds = 50;
        synapseConfig.NatTraversal.IntervalMilliseconds = 50;
        synapseConfig.NatTraversal.MaximumAttempts = 1;
    }

    /// <summary>
    /// Builds a wire-format handshake datagram: the type byte plus the 8-byte nonce the protocol expects.
    /// </summary>
    private static byte[] BuildHandshake()
    {
        byte[] datagram = new byte[1 + 8];
        datagram[0] = (byte)PacketType.Handshake;
        RandomNumberGenerator.Fill(datagram.AsSpan(1, 8));

        return datagram;
    }

    /// <summary>
    /// The connection events <see cref="EventLog"/> records.
    /// </summary>
    private enum EventKind : byte
    {
        /// <summary>
        /// The engine raised <see cref="SynapseManager.ConnectionEstablished"/>.
        /// </summary>
        Established,
        /// <summary>
        /// The engine raised <see cref="SynapseManager.ConnectionClosed"/>.
        /// </summary>
        Closed,
        /// <summary>
        /// The engine raised <see cref="SynapseManager.ConnectionReleased"/>.
        /// </summary>
        Released
    }

    /// <summary>
    /// Records every connection event an engine raises, in order, with the connection it carried. The engines raise
    /// their events from Poll, which the tests call on their own thread, so no locking is needed.
    /// </summary>
    private sealed class EventLog
    {
        /// <summary>
        /// The kinds of the recorded events, in the order they were raised.
        /// </summary>
        public List<EventKind> Kinds { get; } = [];
        /// <summary>
        /// The connection each recorded event carried, index for index with <see cref="Kinds"/>.
        /// </summary>
        private readonly List<SynapseConnection> _connections = [];

        /// <summary>
        /// Subscribes to the connection events of <paramref name="synapseManager"/>.
        /// </summary>
        public void Attach(SynapseManager synapseManager)
        {
            synapseManager.ConnectionEstablished += connectionEventArgs => Record(EventKind.Established, connectionEventArgs.Connection);
            synapseManager.ConnectionClosed += connectionEventArgs => Record(EventKind.Closed, connectionEventArgs.Connection);
            synapseManager.ConnectionReleased += connectionEventArgs => Record(EventKind.Released, connectionEventArgs.Connection);
        }

        /// <summary>
        /// Returns how many events of <paramref name="eventKind"/> were recorded.
        /// </summary>
        public int Count(EventKind eventKind) => Kinds.FindAll(kind => kind == eventKind).Count;

        /// <summary>
        /// Returns true when every recorded event carried <paramref name="synapseConnection"/>.
        /// </summary>
        public bool IsAllFor(SynapseConnection synapseConnection) => _connections.TrueForAll(connection => connection == synapseConnection);

        /// <summary>
        /// Returns true when each of <paramref name="synapseConnections"/> was released exactly once, and after any close
        /// recorded for it.
        /// </summary>
        public bool HasReleasedEach(List<SynapseConnection> synapseConnections)
        {
            foreach (SynapseConnection synapseConnection in synapseConnections)
            {
                int releasedCount = 0;

                for (int i = 0; i < Kinds.Count; i++)
                {
                    if (_connections[i] != synapseConnection)
                        continue;

                    if (Kinds[i] is EventKind.Released)
                        releasedCount++;
                    else if (Kinds[i] is EventKind.Closed && releasedCount > 0)
                        return false;
                }

                if (releasedCount != 1)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Records one event.
        /// </summary>
        private void Record(EventKind eventKind, SynapseConnection synapseConnection)
        {
            Kinds.Add(eventKind);
            _connections.Add(synapseConnection);
        }
    }

    /// <summary>
    /// A signature provider that folds the handshake payload into the signature, as the <see cref="ISignatureProvider"/>
    /// contract allows, so every handshake carrying a fresh nonce produces a different signature for the same endpoint.
    /// </summary>
    private sealed class PayloadMixingSignatureProvider : ISignatureProvider
    {
        /// <inheritdoc/>
        public bool TryCompute(IPEndPoint endPoint, ReadOnlySpan<byte> handshakePayload, out ulong signature)
        {
            signature = (ulong)endPoint.Port;

            foreach (byte payloadByte in handshakePayload)
                signature = signature * 31 + payloadByte;

            return true;
        }
    }
}
