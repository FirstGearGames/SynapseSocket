using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;
using SynapseSocket.Packets;
using SynapseSocket.Security;
using Xunit;

namespace SynapseSocket.Tests.Security;

/// <summary>
/// Reproductions of the 2026-11 security sweep findings: traffic forged from an established peer's address (SYNCONN), and memory and
/// bandwidth a peer can make the reliable channel spend (SYNREL). Test names carry the finding IDs.
/// <para>
/// Every test asserts the corrected behaviour, so a failing test means its finding is present. A test whose fix is held for a design
/// decision is skipped with the reason, and fails when the skip is removed.
/// </para>
/// <para>
/// Everything runs over real loopback sockets. A forged datagram leaves from the victim's own bound socket, through
/// <see cref="SynapseManager.SendRaw"/> on the victim's engine or a raw <see cref="Socket"/> standing in for the victim, which is
/// exactly what a spoofed source address looks like to the receiver.
/// </para>
/// </summary>
public sealed class SecuritySweepTests
{
    /// <summary>
    /// The type byte of a datagram no Synapse packet uses.
    /// </summary>
    private const byte UnknownTypeByte = 0xFF;

    /// <summary>
    /// SYNCONN-4: a datagram refused from a sender with no connection is dropped and counted, without a violation, so it writes no log
    /// line and records no strike.
    /// </summary>
    /// <param name="isUnknownPacketHandlerRefusing">True to hand the unknown type byte to an <see cref="SynapseManager.UnknownPacketReceived"/> handler that refuses it, rather than refusing it outright.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SYNCONN4_RefusedDatagramsFromASenderWithNoConnection_RaiseNoViolation(bool isUnknownPacketHandlerRefusing)
    {
        const int RoundCount = 2;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.Security.AllowUnknownPackets = isUnknownPacketHandlerRefusing));

        if (isUnknownPacketHandlerRefusing)
            server.UnknownPacketReceived += (_, _) => FilterResult.SignatureFailure;

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket stranger = TestHarness.CreateRawSocket();

        // An unknown type byte, a reliable header missing its sequence, and one byte over MaximumPacketSize (1400), twice each: more
        // than the ViolationsBeforeBlacklist (5) strikes a ban takes.
        byte[][] refusedDatagrams = [[UnknownTypeByte], [(byte)PacketType.Reliable], new byte[server.Config.MaximumPacketSize + 1]];
        long droppedBefore = server.Telemetry.PacketsDroppedIn;

        for (int round = 0; round < RoundCount; round++)
        {
            foreach (byte[] refusedDatagram in refusedDatagrams)
                stranger.SendTo(refusedDatagram, target);
        }

        TestHarness.PumpFor(300, server);

        Assert.True(
            serverEvents.Violations == 0,
            $"datagrams from a sender with no connection raised [{serverEvents.Violations}] violations [{string.Join(", ", serverEvents.ViolationReasons)}]");
        Assert.Equal(droppedBefore + RoundCount * refusedDatagrams.Length, server.Telemetry.PacketsDroppedIn);

        // Nothing was counted against the sender either, so it connects.
        stranger.SendTo(BuildHandshake(), target);

        Assert.True(
            TestHarness.PumpUntil(() => server.Connections.Count == 1, 2000, server),
            $"the sender was banned for datagrams it sent with no connection; refusals [{string.Join(", ", serverEvents.FailureReasons)}]");
    }

    /// <summary>
    /// SYNCONN-4: a run of datagrams forged from a connected peer's address must not ban that peer. The first ends its session, which is
    /// SYNCONN-1; every later one finds no connection and must count toward nothing.
    /// </summary>
    [Fact]
    public void SYNCONN4_ForgedDatagramsAfterAKick_DoNotBanTheVictim()
    {
        using ConnectedPair pair = new();

        // ViolationsBeforeBlacklist (5) forged datagrams, back to back from the victim's address.
        for (int i = 0; i < pair.Server.Config.Security.ViolationsBeforeBlacklist; i++)
            pair.Forge([UnknownTypeByte]);

        TestHarness.PumpFor(300, pair.Server, pair.Client);

        pair.Reconnect();

        Assert.True(
            pair.ClientConnection.State is ConnectionState.Connected && !pair.ServerEvents.FailureReasons.Contains(ConnectionRejectedReason.Blacklisted),
            $"five forged datagrams banned the victim for BlacklistDurationMilliseconds; its reconnect is [{pair.ClientConnection.State}], refusals [{string.Join(", ", pair.ServerEvents.FailureReasons)}]");
    }

    /// <summary>
    /// SYNREL-4: a reliable send past <see cref="ReliableConfig.MaximumPending"/> unacknowledged messages is refused with
    /// <see cref="SendResult.Dropped"/>, without the exception that a peer which never acknowledges could otherwise draw on every send.
    /// </summary>
    [Fact]
    public void SYNREL4_FullReliableBacklog_DropsTheSendWithoutThrowingOrAllocating()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);

        // A peer that connects and then acknowledges nothing.
        using Socket silentPeer = TestHarness.CreateRawSocket();
        silentPeer.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 2000, server), "the peer never connected");

        SynapseConnection peerConnection = server.Connections.Connections[0];
        ArraySegment<byte> payload = new(new byte[8]);
        ArraySegment<byte> segmentedPayload = new(new byte[server.MaximumPayloadSize * 2]);

        for (int i = 0; i < server.Config.Reliable.MaximumPending; i++)
            server.Send(peerConnection, payload, isReliable: true);

        Assert.Equal(SendResult.Dropped, server.Send(peerConnection, payload, isReliable: true));
        Assert.Equal(SendResult.Dropped, server.Send(peerConnection, segmentedPayload, isReliable: true));

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++)
        {
            server.Send(peerConnection, payload, isReliable: true);
            server.Send(peerConnection, segmentedPayload, isReliable: true);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    /// <summary>
    /// Builds a wire-format handshake datagram: the type byte and the 8-byte nonce the protocol expects.
    /// </summary>
    /// <returns>The datagram.</returns>
    private static byte[] BuildHandshake()
    {
        byte[] datagram = new byte[PacketHeader.TypeSize + PacketHeader.HandshakeNonceSize];
        datagram[0] = (byte)PacketType.Handshake;
        RandomNumberGenerator.Fill(datagram.AsSpan(PacketHeader.TypeSize));

        return datagram;
    }

    /// <summary>
    /// A server and a real client connected over loopback, the client being the victim whose address is forged.
    /// </summary>
    private sealed class ConnectedPair : IDisposable
    {
        /// <summary>
        /// The server the forged datagrams are sent to.
        /// </summary>
        public readonly SynapseManager Server;
        /// <summary>
        /// The victim's engine, whose socket the forged datagrams leave from.
        /// </summary>
        public readonly SynapseManager Client;
        /// <summary>
        /// Everything the server raised.
        /// </summary>
        public readonly TestHarness.EventRecorder ServerEvents = new();
        /// <summary>
        /// The server's address.
        /// </summary>
        public readonly IPEndPoint ServerEndPoint;
        /// <summary>
        /// The victim's address as the server sees it.
        /// </summary>
        public readonly IPEndPoint VictimEndPoint;
        /// <summary>
        /// The victim's connection to the server, replaced by <see cref="Reconnect"/>.
        /// </summary>
        public SynapseConnection ClientConnection;

        /// <summary>
        /// Starts both engines and connects the victim.
        /// </summary>
        public ConnectedPair()
        {
            int port = TestHarness.GetFreePort();

            Server = new(TestHarness.ServerConfig(port));
            Client = new(TestHarness.ClientConfig());
            ServerEvents.Attach(Server);

            Server.Start();
            Client.Start();

            ServerEndPoint = new(IPAddress.Loopback, port);
            VictimEndPoint = Client.BoundEndPoints[0];
            ClientConnection = Client.Connect(ServerEndPoint);

            Assert.True(
                TestHarness.PumpUntil(() => ClientConnection.State is ConnectionState.Connected && ServerEvents.ConnectionsEstablished == 1, 3000, Server, Client),
                "the victim never connected");
        }

        /// <summary>
        /// Sends <paramref name="datagram"/> to the server from the victim's own socket, as a spoofed source address would arrive.
        /// </summary>
        /// <param name="datagram">The forged datagram.</param>
        public void Forge(byte[] datagram) => Client.SendRaw(ServerEndPoint, new(datagram));

        /// <summary>
        /// Connects the victim again, as a client that noticed it was dropped does, and waits for the attempt to settle.
        /// </summary>
        public void Reconnect()
        {
            ClientConnection = Client.Connect(ServerEndPoint);
            TestHarness.PumpUntil(() => ClientConnection.State is ConnectionState.Connected, 1500, Server, Client);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }
}
