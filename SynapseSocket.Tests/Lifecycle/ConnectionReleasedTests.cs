using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Xunit;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Packets;

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

        SynapseConnection clientConnection = client.Connect(new(IPAddress.Loopback, port));
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

        firstClient.Connect(new(IPAddress.Loopback, port));
        secondClient.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 2, WaitMilliseconds, server, firstClient, secondClient), "The handshakes did not complete.");

        List<SynapseConnection> openConnections = [.. server.Connections.Connections];
        server.Stop();

        Assert.Equal(2, serverEventLog.Count(EventKind.Released));
        Assert.Equal(0, serverEventLog.Count(EventKind.Closed));
        Assert.True(serverEventLog.HasReleasedEach(openConnections), "Stop did not release every open connection.");
    }

    /// <summary>
    /// A handler that disconnects another connection from inside <see cref="SynapseManager.ConnectionReleased"/> gets
    /// that connection closed and released too, and neither connection is released twice.
    /// </summary>
    [Fact]
    public void Disconnect_Inside_Released_Handler_Releases_Each_Connection_Once()
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

        SynapseConnection firstClientConnection = firstClient.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 1, WaitMilliseconds, server, firstClient), "The first handshake did not complete.");
        SynapseConnection firstServerConnection = server.Connections.Connections[0];

        secondClient.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Established) == 2, WaitMilliseconds, server, firstClient, secondClient), "The second handshake did not complete.");
        SynapseConnection secondServerConnection = server.Connections.Connections[0] == firstServerConnection ? server.Connections.Connections[1] : server.Connections.Connections[0];

        server.ConnectionReleased += connectionEventArgs =>
        {
            if (connectionEventArgs.Connection == firstServerConnection)
                server.Disconnect(secondServerConnection);
        };

        firstClient.Disconnect(firstClientConnection);
        Assert.True(TestHarness.PumpUntil(() => serverEventLog.Count(EventKind.Released) == 2, WaitMilliseconds, server, firstClient, secondClient), "Both connections were not released.");
        TestHarness.PumpFor(100, server, firstClient, secondClient);

        Assert.Equal(2, serverEventLog.Count(EventKind.Closed));
        Assert.Equal(2, serverEventLog.Count(EventKind.Released));
        Assert.True(serverEventLog.HasReleasedEach([firstServerConnection, secondServerConnection]), "A connection was released twice, or not at all.");
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
}
