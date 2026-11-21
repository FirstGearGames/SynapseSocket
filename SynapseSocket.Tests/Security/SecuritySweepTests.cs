using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;
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
    /// Skip reason for the findings whose fix is per-session authentication, which adds wire bytes.
    /// </summary>
    private const string HeldForSessionAuthentication = "Held for Ben: SYNCONN-1/2 need per-session authentication, which adds wire bytes.";
    /// <summary>
    /// Skip reason for the findings whose fix is a new per-connection byte budget.
    /// </summary>
    private const string HeldForReorderBudget = "Held for Ben: SYNREL-1/2 need a new per-connection byte budget.";
    /// <summary>
    /// Skip reason for the finding whose fix needs per-assembly state in CodeBoost's PacketReassembler.
    /// </summary>
    private const string HeldForSegmentAckPacing = "Held for Ben: SYNREL-3 needs per-assembly state in CodeBoost's PacketReassembler.";
    /// <summary>
    /// The type byte of a datagram no Synapse packet uses.
    /// </summary>
    private const byte UnknownTypeByte = 0xFF;

    /// <summary>
    /// SYNCONN-1: a malformed or oversized datagram forged from a connected peer's address must not end that peer's session.
    /// </summary>
    /// <param name="typeByte">The forged datagram's leading byte.</param>
    /// <param name="length">The forged datagram's length.</param>
    [Theory(Skip = HeldForSessionAuthentication)]
    [InlineData((int)UnknownTypeByte, 1)]
    [InlineData((int)PacketType.Reliable, 1)]
    [InlineData((int)PacketType.None, 1401)]
    public void SYNCONN1_ForgedMalformedDatagram_DoesNotEndTheVictimsSession(int typeByte, int length)
    {
        using ConnectedPair pair = new();
        SynapseConnection serverConnection = pair.ServerConnection;

        // An unknown type byte, a reliable header missing its sequence, or one byte over MaximumPacketSize (1400).
        byte[] forged = new byte[length];
        forged[0] = (byte)typeByte;

        pair.Forge(forged);
        TestHarness.PumpFor(300, pair.Server, pair.Client);

        AssertSessionIntact(pair, serverConnection, $"one forged [{length}]-byte datagram led by [{typeByte}]");
    }

    /// <summary>
    /// SYNCONN-1: a bare disconnect forged from a connected peer's address must not end that peer's session.
    /// </summary>
    [Fact(Skip = HeldForSessionAuthentication)]
    public void SYNCONN1_ForgedDisconnect_DoesNotEndTheVictimsSession()
    {
        using ConnectedPair pair = new();
        SynapseConnection serverConnection = pair.ServerConnection;

        pair.Forge([(byte)PacketType.Disconnect]);
        TestHarness.PumpFor(300, pair.Server, pair.Client);

        AssertSessionIntact(pair, serverConnection, "one forged 1-byte disconnect");
    }

    /// <summary>
    /// SYNCONN-1: a handshake forged from a connected peer's address, once the one second in which it would read as the peer's answer has
    /// passed, must not reset that peer's session.
    /// </summary>
    [Fact(Skip = HeldForSessionAuthentication)]
    public void SYNCONN1_ForgedHandshake_DoesNotResetTheVictimsSession()
    {
        using ConnectedPair pair = new();
        SynapseConnection serverConnection = pair.ServerConnection;

        // Past IngressEngine.HandshakeAnswerWindowTicks, the only guard on a reconnect.
        TestHarness.PumpFor(1200, pair.Server, pair.Client);

        pair.Forge(BuildHandshake());
        TestHarness.PumpFor(300, pair.Server, pair.Client);

        AssertSessionIntact(pair, serverConnection, "one forged 9-byte handshake with a random nonce");
    }

    /// <summary>
    /// SYNCONN-1: datagrams forged from a peer's address must not ban that peer, even one that reconnects each time it is dropped.
    /// </summary>
    /// <remarks>
    /// Every forged datagram here lands on a live session, so each one is a strike. A strike from a sender with no connection is
    /// SYNCONN-4, which <see cref="SYNCONN4_ForgedDatagramsAfterAKick_DoNotBanTheVictim"/> covers.
    /// </remarks>
    [Fact(Skip = HeldForSessionAuthentication)]
    public void SYNCONN1_ForgedDatagrams_DoNotBanAVictimThatReconnects()
    {
        using ConnectedPair pair = new();

        // ViolationsBeforeBlacklist (5) forged datagrams inside ViolationWindowMilliseconds (10 s), the victim reconnecting after each.
        for (int i = 0; i < pair.Server.Config.Security.ViolationsBeforeBlacklist; i++)
        {
            pair.Forge([UnknownTypeByte]);
            TestHarness.PumpFor(150, pair.Server, pair.Client);
            pair.Reconnect();
        }

        Assert.True(
            pair.ClientConnection.State is ConnectionState.Connected && !pair.ServerEvents.FailureReasons.Contains(ConnectionRejectedReason.Blacklisted),
            $"five forged datagrams banned the victim for BlacklistDurationMilliseconds; its last reconnect is [{pair.ClientConnection.State}], refusals [{string.Join(", ", pair.ServerEvents.FailureReasons)}]");
    }

    /// <summary>
    /// SYNCONN-2: an acknowledgement forged from a peer's address must not release a reliable message the peer never received.
    /// </summary>
    [Fact(Skip = HeldForSessionAuthentication)]
    public void SYNCONN2_ForgedAck_DoesNotLoseAReliableMessage()
    {
        using ConnectedPair pair = new();
        SynapseConnection serverConnection = pair.ServerConnection;

        byte[]? receivedPayload = null;
        pair.Client.PacketReceived += packetReceivedEventArgs => receivedPayload = packetReceivedEventArgs.Payload.ToArray();

        // The reliable message is lost on its first send, as any datagram can be.
        LatencySimulatorConfig latencySimulatorConfig = pair.Server.Config.LatencySimulator;
        latencySimulatorConfig.PacketLossChance = 1.0;
        latencySimulatorConfig.Enabled = true;

        pair.Server.Send(serverConnection, new([1, 2, 3]), isReliable: true);

        // One forged datagram acknowledging sequences 0 to 598, as many as fit under the MTU, covers the window without guessing.
        pair.Forge(BuildAck(firstSequence: 0, sequenceCount: 599));
        TestHarness.PumpFor(100, pair.Server);

        latencySimulatorConfig.Enabled = false;

        Assert.True(
            TestHarness.PumpUntil(() => receivedPayload is not null, 2000, pair.Server, pair.Client),
            "a forged acknowledgement released a reliable message the victim never received, and it was never resent");
    }

    /// <summary>
    /// SYNCONN-3: sources that never answer a handshake reply must not fill a server whose connection cap is at or below
    /// <see cref="SecurityConfig.HandshakeChallengeThreshold"/>, which is every capped game server at the default 1024.
    /// </summary>
    /// <param name="handshakeChallengeThreshold">The server's threshold: the default, or the cap itself, where the gate engages only once the server is full.</param>
    [Theory]
    [InlineData(1024u)]
    [InlineData(8u)]
    public void SYNCONN3_SpoofedHandshakes_DoNotLockARealClientOutOfACappedServer(uint handshakeChallengeThreshold)
    {
        const uint ConnectionCap = 8;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            config.MaximumConcurrentConnections = ConnectionCap;
            config.Security.HandshakeChallengeThreshold = handshakeChallengeThreshold;
        }));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        List<Socket> spoofedSources = [];

        try
        {
            // One 9-byte handshake per source, each standing in for a spoofed address that never hears the reply.
            for (int i = 0; i < ConnectionCap; i++)
            {
                Socket spoofedSource = TestHarness.CreateRawSocket();
                spoofedSources.Add(spoofedSource);
                spoofedSource.SendTo(BuildHandshake(), target);
            }

            TestHarness.PumpFor(300, server);

            using SynapseManager client = new(TestHarness.ClientConfig());
            client.Start();

            SynapseConnection clientConnection = client.Connect(target);

            Assert.True(
                TestHarness.PumpUntil(() => clientConnection.State is ConnectionState.Connected, 3000, server, client),
                $"[{ConnectionCap}] unanswered handshakes took every slot ([{server.Connections.Count}] connections), locking a real client out");
        }
        finally
        {
            foreach (Socket spoofedSource in spoofedSources)
                spoofedSource.Dispose();
        }
    }

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
    /// SYNREL-1 and SYNREL-2: a peer that never sends the reliable message a gap waits on must not pin more memory than one second of
    /// its byte allowance, across the reorder buffer and the reliable assemblies it holds open.
    /// </summary>
    /// <remarks>
    /// Sent at the default rate limits, paced under them, so the run takes about fifteen seconds. Each message declares the 255 segments
    /// the protocol allows, which nothing bounds while <see cref="SecurityConfig.MaximumReassembledPacketSize"/> is at its default of 0.
    /// </remarks>
    [Fact(Skip = HeldForReorderBudget)]
    public void SYNREL1_PeerThatNeverFillsAGap_PinsNoMoreThanItsByteAllowance()
    {
        const byte SegmentCount = 255;
        const ushort OutOfOrderMessageCount = 64;
        const ushort OpenAssemblyCount = 16;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket attacker = TestHarness.CreateRawSocket();

        attacker.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 2000, server), "the attacker never connected");

        SynapseConnection attackerConnection = server.Connections.Connections[0];
        int headerSize = PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented);
        int segmentPayloadLength = (int)server.MaximumTransmissionUnit - headerSize;
        PacedSender pacedSender = new(attacker, target, server, headerSize + segmentPayloadLength);

        // Sequences 1 to 64, never 0: each completes, is acknowledged, and waits in the reorder buffer for a gap that never fills.
        for (ushort sequence = 1; sequence <= OutOfOrderMessageCount; sequence++)
            pacedSender.SendMessage(sequence, SegmentCount, sentSegmentCount: SegmentCount);

        // Sixteen more stop one segment short, each holding a reliable assembly open; a reliable assembly is never evicted for room.
        for (ushort sequence = OutOfOrderMessageCount + 1; sequence <= OutOfOrderMessageCount + OpenAssemblyCount; sequence++)
            pacedSender.SendMessage(sequence, SegmentCount, sentSegmentCount: SegmentCount - 1);

        TestHarness.PumpFor(200, server);

        long reorderBytes = 0;

        foreach (ArraySegment<byte> reorderedPayload in attackerConnection.ReorderBuffer.Values)
            reorderBytes += reorderedPayload.Array!.Length;

        long assemblyBytes = 0;
        byte[] receivedBitmap = new byte[PacketHeader.SegmentAckBitmapSize];

        for (ushort sequence = OutOfOrderMessageCount + 1; sequence <= OutOfOrderMessageCount + OpenAssemblyCount; sequence++)
        {
            if (attackerConnection.Reassembler is null || !attackerConnection.Reassembler.TryWriteReceivedBitmap(sequence, new(receivedBitmap), out _))
                continue;

            foreach (byte bitmapByte in receivedBitmap)
                assemblyBytes += BitOperations.PopCount(bitmapByte) * (long)segmentPayloadLength;
        }

        long byteAllowance = server.Config.Security.MaximumBytesPerSecond;

        Assert.True(
            reorderBytes + assemblyBytes <= byteAllowance,
            $"a peer that never sent sequence 0 pinned [{reorderBytes}] bytes of pooled arrays in [{attackerConnection.ReorderBuffer.Count}] reorder entries and [{assemblyBytes}] payload bytes in open reliable assemblies, against a byte allowance of [{byteAllowance}] a second");
    }

    /// <summary>
    /// SYNREL-3: duplicates of a segment the receiver already holds must not each draw a segment acknowledgement, which turns a 7-byte
    /// datagram into a 35-byte one.
    /// </summary>
    [Fact(Skip = HeldForSegmentAckPacing)]
    public void SYNREL3_DuplicateSegments_DoNotEachDrawASegmentAck()
    {
        const int DuplicateCount = 50;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket attacker = TestHarness.CreateRawSocket();
        byte[] receiveBuffer = new byte[2048];

        attacker.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 2000, server), "the attacker never connected");

        DrainSocket(attacker, receiveBuffer);

        // Sequence 0, segment id 1, index 254 of 255: the last segment of a message, which opens an assembly and draws the one report an
        // incomplete message is owed.
        byte[] lastSegment = [(byte)PacketType.ReliableSegmented, 0, 0, 1, 0, 254, 255, 0xAA];
        attacker.SendTo(lastSegment, target);

        // The same segment again and again, with no payload: 7 bytes each.
        byte[] duplicateSegment = [(byte)PacketType.ReliableSegmented, 0, 0, 1, 0, 254, 255];

        for (int i = 0; i < DuplicateCount; i++)
            attacker.SendTo(duplicateSegment, target);

        TestHarness.PumpFor(200, server);

        int segmentAckCount = 0;
        int segmentAckBytes = 0;

        while (TryReceive(attacker, receiveBuffer, out int receivedLength))
        {
            if (receivedLength > 0 && receiveBuffer[0] == (byte)PacketType.SegmentAck)
            {
                segmentAckCount++;
                segmentAckBytes += receivedLength;
            }
        }

        int sentBytes = lastSegment.Length + DuplicateCount * duplicateSegment.Length;

        Assert.True(
            segmentAckCount <= 1,
            $"[{sentBytes}] bytes of one segment, sent [{DuplicateCount + 1}] times inside one resend interval, drew [{segmentAckCount}] segment acknowledgements totalling [{segmentAckBytes}] bytes");
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
    /// Builds a wire-format acknowledgement datagram naming a run of consecutive sequences.
    /// </summary>
    /// <param name="firstSequence">The first sequence acknowledged.</param>
    /// <param name="sequenceCount">How many consecutive sequences are acknowledged.</param>
    /// <returns>The datagram.</returns>
    private static byte[] BuildAck(ushort firstSequence, int sequenceCount)
    {
        byte[] datagram = new byte[PacketHeader.TypeSize + sequenceCount * PacketHeader.SequenceSize];
        datagram[0] = (byte)PacketType.Ack;

        for (int i = 0; i < sequenceCount; i++)
        {
            ushort sequence = (ushort)(firstSequence + i);
            datagram[PacketHeader.TypeSize + i * PacketHeader.SequenceSize] = (byte)sequence;
            datagram[PacketHeader.TypeSize + i * PacketHeader.SequenceSize + 1] = (byte)(sequence >> 8);
        }

        return datagram;
    }

    /// <summary>
    /// Asserts the server still holds the victim's session it held before the forgery, untouched.
    /// </summary>
    /// <param name="pair">The connected pair.</param>
    /// <param name="serverConnection">The server's connection to the victim before the forgery.</param>
    /// <param name="forgery">What was forged, for the failure message.</param>
    private static void AssertSessionIntact(ConnectedPair pair, SynapseConnection serverConnection, string forgery)
    {
        bool isSameSession = pair.Server.Connections.ConnectionsByEndPoint.TryGetValue(pair.VictimEndPoint, out SynapseConnection? currentConnection)
            && ReferenceEquals(currentConnection, serverConnection)
            && pair.ServerEvents.ConnectionsClosed == 0;

        Assert.True(isSameSession, $"{forgery} from the victim's address ended its session: [{pair.ServerEvents.ConnectionsClosed}] closed, still listed [{currentConnection is not null}]");
    }

    /// <summary>
    /// Reads and discards everything queued on <paramref name="socket"/>.
    /// </summary>
    /// <param name="socket">The socket to drain.</param>
    /// <param name="buffer">Scratch buffer the datagrams are read into.</param>
    private static void DrainSocket(Socket socket, byte[] buffer)
    {
        while (TryReceive(socket, buffer, out _))
        {
        }
    }

    /// <summary>
    /// Reads one queued datagram without blocking.
    /// </summary>
    /// <param name="socket">The socket to read.</param>
    /// <param name="buffer">Receives the datagram.</param>
    /// <param name="receivedLength">The datagram's length.</param>
    /// <returns>False once nothing more is queued.</returns>
    private static bool TryReceive(Socket socket, byte[] buffer, out int receivedLength)
    {
        receivedLength = 0;

        if (socket.Available == 0)
            return false;

        try
        {
            receivedLength = socket.Receive(buffer);

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
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
        /// The server's connection to the victim. Throws once the server no longer holds one.
        /// </summary>
        public SynapseConnection ServerConnection => Server.Connections.ConnectionsByEndPoint[VictimEndPoint];

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

    /// <summary>
    /// Sends hand-built reliable segments from a raw socket, paced under the server's default per-connection rate limits so that none is
    /// shed, and polls the server as it goes so its socket buffer never overflows.
    /// </summary>
    private sealed class PacedSender
    {
        /// <summary>
        /// The socket the segments leave from.
        /// </summary>
        private readonly Socket _socket;
        /// <summary>
        /// The server's address.
        /// </summary>
        private readonly IPEndPoint _target;
        /// <summary>
        /// The server, polled between bursts.
        /// </summary>
        private readonly SynapseManager _server;
        /// <summary>
        /// One full-size segment datagram, rewritten for each send.
        /// </summary>
        private readonly byte[] _datagram;
        /// <summary>
        /// Datagrams sent in the current window.
        /// </summary>
        private int _windowDatagramCount;
        /// <summary>
        /// When the current window opened, in <see cref="Environment.TickCount64"/> milliseconds.
        /// </summary>
        private long _windowStartMilliseconds = Environment.TickCount64;
        /// <summary>
        /// Datagrams sent per window: 1.8 MB of full-size segments, under both the 2000 packet and the 2 MiB byte allowance a second.
        /// </summary>
        private const int DatagramsPerWindow = 1500;
        /// <summary>
        /// How long a window lasts. The server's rate window is a second measured between its own polls, so a window any shorter can
        /// leave one of the server's holding the tail of one burst and the head of the next.
        /// </summary>
        private const long WindowMilliseconds = 1100;

        /// <summary>
        /// Creates a sender of full-size segments.
        /// </summary>
        /// <param name="socket">The socket the segments leave from.</param>
        /// <param name="target">The server's address.</param>
        /// <param name="server">The server, polled between bursts.</param>
        /// <param name="datagramLength">The length of one full-size segment datagram.</param>
        public PacedSender(Socket socket, IPEndPoint target, SynapseManager server, int datagramLength)
        {
            _socket = socket;
            _target = target;
            _server = server;
            _datagram = new byte[datagramLength];
        }

        /// <summary>
        /// Sends the first <paramref name="sentSegmentCount"/> segments of a reliable message declaring <paramref name="segmentCount"/>,
        /// using the sequence as its segment id.
        /// </summary>
        /// <param name="sequence">The message's reliable sequence and segment id.</param>
        /// <param name="segmentCount">The segments the message declares.</param>
        /// <param name="sentSegmentCount">How many of them to send, from index 0.</param>
        public void SendMessage(ushort sequence, byte segmentCount, int sentSegmentCount)
        {
            for (int segmentIndex = 0; segmentIndex < sentSegmentCount; segmentIndex++)
            {
                if (_windowDatagramCount >= DatagramsPerWindow)
                {
                    // Wait out the rest of the window, polling so nothing waits in the socket buffer.
                    while (Environment.TickCount64 - _windowStartMilliseconds < WindowMilliseconds)
                    {
                        _server.Poll();
                        Thread.Sleep(1);
                    }

                    _windowStartMilliseconds = Environment.TickCount64;
                    _windowDatagramCount = 0;
                }

                PacketHeader.Write(_datagram, PacketType.ReliableSegmented, sequence, segmentId: sequence, (byte)segmentIndex, segmentCount);
                _socket.SendTo(_datagram, _target);
                _windowDatagramCount++;

                if (_windowDatagramCount % 64 == 0)
                    _server.Poll();
            }

            _server.Poll();
        }
    }
}
