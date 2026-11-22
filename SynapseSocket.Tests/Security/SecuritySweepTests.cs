using System;
using System.Buffers.Binary;
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
    /// The type byte of a datagram no Synapse packet uses.
    /// </summary>
    private const byte UnknownTypeByte = 0xFF;
    /// <summary>
    /// The bytes of session token a disconnect, keep-alive, acknowledgement or segment acknowledgement ends with between two peers that
    /// both advertise session tokens: the first bytes of the handshake nonce the receiver sent. Pinned here rather than read from the
    /// engine, so these tests describe the wire format independently of it.
    /// </summary>
    private const int SessionTokenSize = 4;
    /// <summary>
    /// The protocol version byte a handshake, a challenge or a proof ends with to advertise session tokens.
    /// </summary>
    private const byte SessionTokenVersion = 1;
    /// <summary>
    /// The protocol version byte a handshake ends with to advertise session tokens and ask for them on data packets too:
    /// <see cref="SessionTokenVersion"/> with its high bit set.
    /// </summary>
    private const byte DataSessionTokenVersion = SessionTokenVersion | 0x80;

    /// <summary>
    /// SYNCONN-1: a malformed or oversized datagram forged from a connected peer's address must not end that peer's session.
    /// </summary>
    /// <param name="typeByte">The forged datagram's leading byte.</param>
    /// <param name="length">The forged datagram's length.</param>
    [Theory]
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
    [Fact]
    public void SYNCONN1_ForgedDisconnect_DoesNotEndTheVictimsSession()
    {
        using ConnectedPair pair = new();
        SynapseConnection serverConnection = pair.ServerConnection;
        long droppedBefore = pair.Server.Telemetry.PacketsDroppedIn;

        pair.Forge([(byte)PacketType.Disconnect]);
        TestHarness.PumpFor(300, pair.Server, pair.Client);

        AssertSessionIntact(pair, serverConnection, "one forged 1-byte disconnect");
        Assert.True(pair.Server.Telemetry.PacketsDroppedIn == droppedBefore + 1, "the forged disconnect was not counted as a dropped datagram");
    }

    /// <summary>
    /// SYNCONN-1: a handshake forged from a connected peer's address, once the one second in which it would read as the peer's answer has
    /// passed, must not reset that peer's session, whether it is shaped as a handshake or as a proof answering a challenge.
    /// </summary>
    /// <param name="payloadLength">The forged handshake's payload length: a nonce or a challenge, with or without the version byte.</param>
    [Theory]
    [InlineData(PacketHeader.HandshakeNonceSize)]
    [InlineData(PacketHeader.HandshakeNonceSize + 1)]
    [InlineData(PacketHeader.HandshakeChallengeSize)]
    [InlineData(PacketHeader.HandshakeChallengeSize + 1)]
    public void SYNCONN1_ForgedHandshake_DoesNotResetTheVictimsSession(int payloadLength)
    {
        using ConnectedPair pair = new();
        SynapseConnection serverConnection = pair.ServerConnection;

        // Past IngressEngine.HandshakeAnswerWindowTicks, the only guard on a reconnect.
        TestHarness.PumpFor(1200, pair.Server, pair.Client);

        pair.Forge(BuildHandshake(payloadLength));
        TestHarness.PumpFor(300, pair.Server, pair.Client);

        AssertSessionIntact(pair, serverConnection, $"one forged handshake with a random [{payloadLength}]-byte payload");
    }

    /// <summary>
    /// SYNCONN-1: keep-alives forged from the address of a peer that has gone silent must not hold its session open past the timeout.
    /// </summary>
    [Fact]
    public void SYNCONN1_ForgedKeepAlives_DoNotHoldASilentPeersSessionOpen()
    {
        const uint TimeoutMilliseconds = 500;

        using ConnectedPair pair = new(config => config.Connection.TimeoutMilliseconds = TimeoutMilliseconds);

        // The victim is never polled again, so it sends nothing, while a keep-alive forged from its address arrives every 50 ms.
        long deadlineMilliseconds = Environment.TickCount64 + TimeoutMilliseconds * 3;

        while (pair.ServerEvents.ConnectionsClosed == 0 && Environment.TickCount64 < deadlineMilliseconds)
        {
            pair.Forge([(byte)PacketType.KeepAlive]);
            TestHarness.PumpFor(50, pair.Server);
        }

        Assert.True(
            pair.ServerEvents.ConnectionsClosed == 1,
            $"keep-alives forged from a silent peer's address held its session open for [{TimeoutMilliseconds * 3}] ms against a [{TimeoutMilliseconds}] ms timeout");
    }

    /// <summary>
    /// SYNCONN-1: a disconnect forged from the address an outbound connection is still waiting to hear from must not end the attempt.
    /// </summary>
    [Fact]
    public void SYNCONN1_ForgedDisconnect_DoesNotEndAPendingConnection()
    {
        using Socket peer = TestHarness.CreateRawSocket();
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder clientEvents = new();
        clientEvents.Attach(client);
        client.Start();

        SynapseConnection pendingConnection = client.Connect((IPEndPoint)peer.LocalEndPoint!);

        // The peer never answers the handshake, and a disconnect from its address arrives before any answer.
        peer.SendTo([(byte)PacketType.Disconnect], client.BoundEndPoints[0]);
        TestHarness.PumpFor(300, client);

        Assert.True(
            clientEvents.ConnectionsClosed == 0 && pendingConnection.State is ConnectionState.Pending,
            $"one 1-byte disconnect ended a connection still waiting on its handshake: [{clientEvents.ConnectionsClosed}] closed, state [{pendingConnection.State}]");
    }

    /// <summary>
    /// SYNCONN-1: every handshake an outbound connection sends opens with the same session token, so whichever one the peer answers, a
    /// control packet the peer sends back carrying that token is accepted, and one carrying any other token is not.
    /// </summary>
    [Fact]
    public void SYNCONN1_EveryHandshakeRetry_OpensWithTheSameSessionToken()
    {
        using Socket peer = TestHarness.CreateRawSocket();
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder clientEvents = new();
        clientEvents.Attach(client);
        client.Start();

        IPEndPoint clientEndPoint = client.BoundEndPoints[0];
        SynapseConnection connection = client.Connect((IPEndPoint)peer.LocalEndPoint!);
        byte[] receiveBuffer = new byte[64];

        // The handshake Connect sends, and the retry the peer's silence draws 300 ms later.
        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, client, PacketType.Handshake, out int firstLength), "the client sent no handshake");
        byte[] firstHandshake = receiveBuffer.AsSpan(0, firstLength).ToArray();
        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, client, PacketType.Handshake, out int retryLength), "the client never retried its handshake");
        byte[] retryHandshake = receiveBuffer.AsSpan(0, retryLength).ToArray();

        int versionedLength = PacketHeader.TypeSize + PacketHeader.HandshakeNonceSize + 1;

        Assert.True(
            firstLength == versionedLength && retryLength == versionedLength && firstHandshake[^1] == SessionTokenVersion && retryHandshake[^1] == SessionTokenVersion,
            $"the handshakes were [{firstLength}] and [{retryLength}] bytes, not a nonce followed by the session token version");
        Assert.True(
            firstHandshake.AsSpan(1, SessionTokenSize).SequenceEqual(retryHandshake.AsSpan(1, SessionTokenSize)),
            "a handshake retry opened with a different session token than the handshake it repeats");
        Assert.False(
            firstHandshake.AsSpan(1, PacketHeader.HandshakeNonceSize).SequenceEqual(retryHandshake.AsSpan(1, PacketHeader.HandshakeNonceSize)),
            "a handshake retry repeated the whole nonce, which the peer's replay cache refuses");

        // The peer answers, advertising session tokens, and then disconnects: first with a wrong token, then with the one the client sent.
        peer.SendTo(BuildHandshake(PacketHeader.HandshakeNonceSize + 1), clientEndPoint);
        Assert.True(TestHarness.PumpUntil(() => connection.State is ConnectionState.Connected, 2000, client), "the peer's answer did not connect the client");

        byte[] wrongTokenDisconnect = BuildTokenedPacket([(byte)PacketType.Disconnect], firstHandshake.AsSpan(1, SessionTokenSize));
        wrongTokenDisconnect[1] ^= 0xFF;
        peer.SendTo(wrongTokenDisconnect, clientEndPoint);
        TestHarness.PumpFor(100, client);

        Assert.True(clientEvents.ConnectionsClosed == 0, "a disconnect carrying a wrong session token ended the connection");

        peer.SendTo(BuildTokenedPacket([(byte)PacketType.Disconnect], firstHandshake.AsSpan(1, SessionTokenSize)), clientEndPoint);

        Assert.True(TestHarness.PumpUntil(() => clientEvents.ConnectionsClosed == 1, 2000, client), "a disconnect carrying the session token of the first handshake was refused");
    }

    /// <summary>
    /// SYNCONN-1: a control packet carries the session token only between two peers that both advertise it. A peer whose handshake
    /// advertises it is sent the token, the first bytes of its own nonce, and a keep-alive from it without the token is refused; a peer
    /// whose handshake predates session tokens is sent none and needs none, exactly as before.
    /// </summary>
    /// <param name="isPeerAdvertising">True when the peer's handshake ends in the session token version.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SYNCONN1_ControlPackets_CarryTheSessionTokenOnlyToAPeerThatAdvertisesIt(bool isPeerAdvertising)
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();
        byte[] receiveBuffer = new byte[64];
        byte[] handshake = BuildHandshake(isPeerAdvertising ? PacketHeader.HandshakeNonceSize + 1 : PacketHeader.HandshakeNonceSize);

        peer.SendTo(handshake, target);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Handshake, out int replyLength), "the server never answered the handshake");
        Assert.True(
            replyLength == PacketHeader.TypeSize + PacketHeader.HandshakeNonceSize + 1 && receiveBuffer[replyLength - 1] == SessionTokenVersion,
            $"the server's [{replyLength}]-byte answer does not advertise session tokens");

        SynapseConnection serverConnection = server.Connections.Connections[0];

        // A keep-alive without the token, sent once the poll clock has moved on so a refresh would show.
        long receivedTicksBefore = serverConnection.LastReceivedTicks;
        TestHarness.PumpFor(50, server);
        peer.SendTo([(byte)PacketType.KeepAlive], target);
        TestHarness.PumpFor(50, server);

        bool isKeepAliveAccepted = serverConnection.LastReceivedTicks != receivedTicksBefore;

        Assert.True(
            isKeepAliveAccepted != isPeerAdvertising,
            $"a keep-alive without the session token was [{(isKeepAliveAccepted ? "accepted" : "refused")}] from a peer that [{(isPeerAdvertising ? "advertised" : "never advertised")}] session tokens");

        // The server's own disconnect carries the first bytes of the peer's nonce only to a peer that advertised session tokens.
        server.Disconnect(serverConnection);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Disconnect, out int disconnectLength), "the server's disconnect never arrived");

        if (isPeerAdvertising)
        {
            Assert.True(
                disconnectLength == PacketHeader.TypeSize + SessionTokenSize && receiveBuffer.AsSpan(1, SessionTokenSize).SequenceEqual(handshake.AsSpan(1, SessionTokenSize)),
                $"the server's [{disconnectLength}]-byte disconnect does not end in the session token the peer's handshake opened with");
        }
        else
        {
            Assert.Equal(PacketHeader.TypeSize, disconnectLength);
        }
    }

    /// <summary>
    /// SYNCONN-1: a handshake from a connected peer, such as one that restarted, is answered with a challenge rather than resetting the
    /// session; the proof that comes back starts the new session, which keeps the server's session token and adopts the peer's new one.
    /// </summary>
    [Fact]
    public void SYNCONN1_HandshakeFromAConnectedPeer_IsChallengedAndItsProofStartsANewSession()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();
        byte[] receiveBuffer = new byte[64];

        peer.SendTo(BuildHandshake(PacketHeader.HandshakeNonceSize + 1), target);
        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Handshake, out int firstAnswerLength), "the server never answered the first handshake");
        byte[] firstAnswer = receiveBuffer.AsSpan(0, firstAnswerLength).ToArray();

        // Past IngressEngine.HandshakeAnswerWindowTicks, the peer restarts and handshakes again from the same address with a new nonce.
        TestHarness.PumpFor(1200, server);

        byte[] secondHandshake = BuildHandshake(PacketHeader.HandshakeNonceSize + 1);
        peer.SendTo(secondHandshake, target);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Handshake, out int challengeLength), "the server never answered the second handshake");
        Assert.True(
            challengeLength == PacketHeader.TypeSize + PacketHeader.HandshakeChallengeSize + 1 && serverEvents.ConnectionsClosed == 0,
            $"the second handshake drew a [{challengeLength}]-byte answer and [{serverEvents.ConnectionsClosed}] closed sessions, not a challenge");

        // The proof: the challenge echoed back verbatim, as the restarted peer's pending connection does.
        peer.SendTo(receiveBuffer.AsSpan(0, challengeLength).ToArray(), target);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Handshake, out int secondAnswerLength), "the proof drew no handshake");
        Assert.True(
            serverEvents.ConnectionsClosed == 1 && serverEvents.ConnectionsEstablished == 2,
            $"the proof left [{serverEvents.ConnectionsClosed}] closed and [{serverEvents.ConnectionsEstablished}] established sessions rather than replacing the first");
        Assert.True(
            secondAnswerLength == firstAnswerLength && receiveBuffer.AsSpan(1, SessionTokenSize).SequenceEqual(firstAnswer.AsSpan(1, SessionTokenSize)),
            "the new session's handshake opened with a different session token than the first, so a peer holding the first answer would be refused");

        // The new session presents the restarted peer's new token.
        server.Disconnect(server.Connections.Connections[0]);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Disconnect, out int disconnectLength), "the server's disconnect never arrived");
        Assert.True(
            disconnectLength == PacketHeader.TypeSize + SessionTokenSize && receiveBuffer.AsSpan(1, SessionTokenSize).SequenceEqual(secondHandshake.AsSpan(1, SessionTokenSize)),
            "the new session's disconnect does not carry the session token of the handshake that started it");
    }

    /// <summary>
    /// SYNCONN-1: datagrams forged from a peer's address must not ban that peer, even one that reconnects each time it is dropped.
    /// </summary>
    /// <remarks>
    /// Every forged datagram here lands on a live session, so each one is a strike. A strike from a sender with no connection is
    /// SYNCONN-4, which <see cref="SYNCONN4_ForgedDatagramsAfterAKick_DoNotBanTheVictim"/> covers.
    /// </remarks>
    [Fact]
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
    [Fact]
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
    /// SYNCONN-2: a segment acknowledgement forged from a peer's address, claiming every segment, must not release a reliable segmented
    /// message the peer never received.
    /// </summary>
    [Fact]
    public void SYNCONN2_ForgedSegmentAck_DoesNotLoseAReliableMessage()
    {
        using ConnectedPair pair = new();
        SynapseConnection serverConnection = pair.ServerConnection;

        byte[]? receivedPayload = null;
        pair.Client.PacketReceived += packetReceivedEventArgs => receivedPayload = packetReceivedEventArgs.Payload.ToArray();

        // Every segment of the reliable message is lost on its first send.
        LatencySimulatorConfig latencySimulatorConfig = pair.Server.Config.LatencySimulator;
        latencySimulatorConfig.PacketLossChance = 1.0;
        latencySimulatorConfig.Enabled = true;

        byte[] payload = new byte[pair.Server.MaximumPayloadSize * 2];
        RandomNumberGenerator.Fill(payload);
        pair.Server.Send(serverConnection, new(payload), isReliable: true);

        // One forged segment acknowledgement for sequence 0 whose bitmap claims every segment index.
        byte[] forgedSegmentAck = new byte[PacketHeader.ComputeHeaderSize(PacketType.SegmentAck) + PacketHeader.SegmentAckBitmapSize];
        PacketHeader.Write(forgedSegmentAck, PacketType.SegmentAck, sequence: 0, segmentId: 0, segmentIndex: 0, segmentCount: 0);
        forgedSegmentAck.AsSpan(PacketHeader.ComputeHeaderSize(PacketType.SegmentAck)).Fill(0xFF);

        pair.Forge(forgedSegmentAck);
        TestHarness.PumpFor(100, pair.Server);

        latencySimulatorConfig.Enabled = false;

        Assert.True(
            TestHarness.PumpUntil(() => receivedPayload is not null && receivedPayload.AsSpan().SequenceEqual(payload), 3000, pair.Server, pair.Client),
            "a forged segment acknowledgement released a reliable message the victim never received, and it was never resent");
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
    /// SYNCONN-3: under a connection cap at or below <see cref="SecurityConfig.HandshakeChallengeThreshold"/> every handshake is
    /// challenged, and the challenge carries the protocol version only when the handshake did, because a peer that predates session
    /// tokens reads a challenge by its exact length. The challenge echoed back connects either peer, and the session takes part in
    /// session tokens only when the peer advertised them.
    /// </summary>
    /// <param name="isPeerAdvertising">True when the peer's handshake ends in the session token version.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SYNCONN3_CappedServer_ChallengesEveryHandshakeInTheFormThePeerReads(bool isPeerAdvertising)
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.MaximumConcurrentConnections = 8));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();
        byte[] receiveBuffer = new byte[64];
        byte[] handshake = BuildHandshake(isPeerAdvertising ? PacketHeader.HandshakeNonceSize + 1 : PacketHeader.HandshakeNonceSize);

        peer.SendTo(handshake, target);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Handshake, out int challengeLength), "the server never answered the handshake");

        int expectedChallengeLength = PacketHeader.TypeSize + PacketHeader.HandshakeChallengeSize + (isPeerAdvertising ? 1 : 0);

        Assert.True(
            challengeLength == expectedChallengeLength && server.Connections.Count == 0,
            $"a [{handshake.Length}]-byte handshake drew a [{challengeLength}]-byte answer and [{server.Connections.Count}] connections, not a [{expectedChallengeLength}]-byte challenge");

        // The challenge echoed back verbatim, as a pending engine does, is the proof that connects the peer.
        peer.SendTo(receiveBuffer.AsSpan(0, challengeLength).ToArray(), target);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Handshake, out _), "the proof drew no handshake");
        Assert.Equal(1, server.Connections.Count);

        // The stateless challenge recalls whether the peer advertised session tokens only through the version byte it carried back.
        server.Disconnect(server.Connections.Connections[0]);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Disconnect, out int disconnectLength), "the server's disconnect never arrived");
        Assert.Equal(isPeerAdvertising ? PacketHeader.TypeSize + SessionTokenSize : PacketHeader.TypeSize, disconnectLength);
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
    /// SYNCONN-4: a run of datagrams forged from the address of a connected peer that predates session tokens must not ban that peer.
    /// The first still ends its session, as it always has for such a peer; every later one finds no connection and must count toward
    /// nothing.
    /// </summary>
    [Fact]
    public void SYNCONN4_ForgedDatagramsAfterAKick_DoNotBanTheVictim()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket victim = TestHarness.CreateRawSocket();

        // A handshake without the session token version, so the first forged datagram still ends the session.
        victim.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 1, 2000, server), "the victim never connected");

        // ViolationsBeforeBlacklist (5) forged datagrams, back to back from the victim's address.
        for (int i = 0; i < server.Config.Security.ViolationsBeforeBlacklist; i++)
            victim.SendTo([UnknownTypeByte], target);

        TestHarness.PumpFor(300, server);

        victim.SendTo(BuildHandshake(), target);

        Assert.True(
            TestHarness.PumpUntil(() => serverEvents.ConnectionsEstablished == 2, 2000, server) && serverEvents.ConnectionsClosed == 1,
            $"five forged datagrams banned the victim for BlacklistDurationMilliseconds; [{serverEvents.ConnectionsClosed}] closed, [{serverEvents.ConnectionsEstablished}] established, refusals [{string.Join(", ", serverEvents.FailureReasons)}]");
    }

    /// <summary>
    /// SYNREL-1 and SYNREL-2: a peer that never sends the reliable message a gap waits on must not hold more than one second of its
    /// byte allowance in payload bytes, across the reorder buffer and the assemblies it holds open.
    /// </summary>
    /// <remarks>
    /// Sent at the default rate limits, paced under them, so the run takes about fifteen seconds. Each message declares the 255 segments
    /// the protocol allows, which nothing bounds while <see cref="SecurityConfig.MaximumReassembledPacketSize"/> is at its default of 0.
    /// </remarks>
    [Fact]
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
        int segmentPayloadLength = (int)server.MaximumTransmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented);
        PacedSender pacedSender = new(attacker, target, server);

        // Sequences 1 to 64, never 0: each completes, is acknowledged, and waits in the reorder buffer for a gap that never fills.
        for (ushort sequence = 1; sequence <= OutOfOrderMessageCount; sequence++)
            pacedSender.SendMessage(sequence, SegmentCount, sentSegmentCount: SegmentCount);

        // Sixteen more stop one segment short, each holding a reliable assembly open; a reliable assembly is never evicted for room.
        for (ushort sequence = OutOfOrderMessageCount + 1; sequence <= OutOfOrderMessageCount + OpenAssemblyCount; sequence++)
            pacedSender.SendMessage(sequence, SegmentCount, sentSegmentCount: SegmentCount - 1);

        TestHarness.PumpFor(200, server);

        long reorderBytes = 0;
        long reorderArrayBytes = 0;

        foreach (ArraySegment<byte> reorderedPayload in attackerConnection.ReorderBuffer.Values)
        {
            reorderBytes += reorderedPayload.Count;
            reorderArrayBytes += reorderedPayload.Array!.Length;
        }

        // Any message sent may hold an assembly open, including one the budget stopped partway through.
        long assemblyBytes = MeasureAssemblyBytes(attackerConnection, lastSegmentId: OutOfOrderMessageCount + OpenAssemblyCount, segmentPayloadLength);
        long byteAllowance = server.Config.Security.MaximumBytesPerSecond;

        Assert.True(
            reorderBytes + assemblyBytes <= byteAllowance,
            $"a peer that never sent sequence 0 held [{reorderBytes}] payload bytes ([{reorderArrayBytes}] in pooled arrays) in [{attackerConnection.ReorderBuffer.Count}] reorder entries and [{assemblyBytes}] payload bytes in open assemblies, against a byte allowance of [{byteAllowance}] a second");
    }

    /// <summary>
    /// SYNREL-1: an out-of-order reliable packet that would take a peer past its byte allowance is dropped without an acknowledgement and
    /// reported as the per-second rate limit reports one, while a resend of a packet already delivered or already held is still
    /// acknowledged.
    /// </summary>
    [Fact]
    public void SYNREL1_OutOfOrderPacketPastTheBudget_IsDroppedUnacknowledged()
    {
        const int ByteAllowance = 16 * 1024;
        const int PayloadLength = 1000;
        // 15 packets of 1003 bytes stay under the allowance in one rate window.
        const int DatagramsPerWindow = 15;
        // 16 packets of 1000 bytes fit the allowance: sequences 2 to 17, behind the never-sent 1.
        const ushort LastHeldSequence = 1 + ByteAllowance / PayloadLength;
        const ushort LastSentSequence = LastHeldSequence + 3;
        // The three sent past the budget, and the last of them again.
        const int DroppedPacketCount = 4;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.Security.MaximumBytesPerSecond = ByteAllowance));
        List<ViolationEventArgs> violations = RecordViolations(server);
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket attacker = TestHarness.CreateRawSocket();
        byte[] receiveBuffer = new byte[2048];

        attacker.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 2000, server), "the attacker never connected");

        SynapseConnection attackerConnection = server.Connections.Connections[0];
        PacedSender pacedSender = new(attacker, target, server, DatagramsPerWindow);

        // Sequence 0 is delivered and 1 is never sent, so everything from 2 waits behind it. The run spans two rate windows, since one
        // window carries no more than the allowance the budget is sized from.
        pacedSender.Send(PacketType.Reliable, sequence: 0, segmentId: 0, segmentIndex: 0, segmentCount: 0, PayloadLength);

        for (ushort sequence = 2; sequence <= LastSentSequence; sequence++)
            pacedSender.Send(PacketType.Reliable, sequence, segmentId: 0, segmentIndex: 0, segmentCount: 0, PayloadLength);

        TestHarness.PumpFor(100, server);
        HashSet<ushort> ackedSequences = ReceiveAckedSequences(attacker, receiveBuffer);

        // The sender missed these acknowledgements: 0 is delivered, LastHeldSequence is held, and the last one was dropped.
        pacedSender.Send(PacketType.Reliable, sequence: 0, segmentId: 0, segmentIndex: 0, segmentCount: 0, PayloadLength);
        pacedSender.Send(PacketType.Reliable, LastHeldSequence, segmentId: 0, segmentIndex: 0, segmentCount: 0, PayloadLength);
        pacedSender.Send(PacketType.Reliable, LastSentSequence, segmentId: 0, segmentIndex: 0, segmentCount: 0, PayloadLength);

        TestHarness.PumpFor(100, server);
        HashSet<ushort> resentAckedSequences = ReceiveAckedSequences(attacker, receiveBuffer);

        long heldBytes = 0;

        foreach (ArraySegment<byte> reorderedPayload in attackerConnection.ReorderBuffer.Values)
            heldBytes += reorderedPayload.Count;

        for (ushort sequence = 2; sequence <= LastSentSequence; sequence++)
        {
            bool isWithinBudget = sequence <= LastHeldSequence;
            Assert.True(ackedSequences.Contains(sequence) == isWithinBudget, $"sequence [{sequence}] was acknowledged [{ackedSequences.Contains(sequence)}] where the budget holds up to [{LastHeldSequence}]");
        }

        Assert.True(resentAckedSequences.Contains(0) && resentAckedSequences.Contains(LastHeldSequence), $"a resend of a delivered or held packet went unacknowledged: [{string.Join(", ", resentAckedSequences)}]");
        Assert.DoesNotContain(LastSentSequence, resentAckedSequences);
        Assert.True(heldBytes <= ByteAllowance, $"[{heldBytes}] payload bytes held against an allowance of [{ByteAllowance}]");
        Assert.True(server.Connections.Count == 1, "the budget dropped the peer");
        AssertReportedAsRateLimited(violations, DroppedPacketCount);
    }

    /// <summary>
    /// SYNREL-2: the bytes of a reliable message that never completes count toward the allowance for as long as its assembly is held, and
    /// are given back when the assembly expires.
    /// </summary>
    [Fact]
    public void SYNREL2_AbandonedMessage_HoldsTheBudgetUntilItsAssemblyExpires()
    {
        const int ByteAllowance = 16 * 1024;
        // 13 datagrams of up to 1200 bytes stay under the allowance in one rate window.
        const int DatagramsPerWindow = 13;
        const uint AssemblyTimeoutMilliseconds = 2000;
        const byte SegmentCount = 14;
        const int HeldSegmentCount = 12;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            config.Security.MaximumBytesPerSecond = ByteAllowance;
            config.Segment.AssemblyTimeoutMilliseconds = AssemblyTimeoutMilliseconds;
        }));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket attacker = TestHarness.CreateRawSocket();
        byte[] receiveBuffer = new byte[2048];

        attacker.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 2000, server), "the attacker never connected");

        SynapseConnection attackerConnection = server.Connections.Connections[0];
        int segmentPayloadLength = (int)server.MaximumTransmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented);
        PacedSender pacedSender = new(attacker, target, server, DatagramsPerWindow);

        // Sequence 0 is never sent and 1 never completes: twelve of its fourteen segments hold 14,316 bytes.
        for (byte segmentIndex = 0; segmentIndex < HeldSegmentCount; segmentIndex++)
            pacedSender.Send(PacketType.ReliableSegmented, sequence: 1, segmentId: 1, segmentIndex, SegmentCount, segmentPayloadLength);

        // One full unsegmented packet still fits beside it, and a second does not.
        pacedSender.StartNextWindow();
        pacedSender.Send(PacketType.Reliable, sequence: 2, segmentId: 0, segmentIndex: 0, segmentCount: 0, server.MaximumPayloadSize);
        pacedSender.Send(PacketType.Reliable, sequence: 3, segmentId: 0, segmentIndex: 0, segmentCount: 0, server.MaximumPayloadSize);

        TestHarness.PumpFor(100, server);
        HashSet<ushort> ackedSequences = ReceiveAckedSequences(attacker, receiveBuffer);

        Assert.True(
            TestHarness.PumpUntil(() => attackerConnection.Reassembler is { HasAssemblies: false }, (int)AssemblyTimeoutMilliseconds + 1000, server),
            "the abandoned message was never discarded");

        pacedSender.Send(PacketType.Reliable, sequence: 3, segmentId: 0, segmentIndex: 0, segmentCount: 0, server.MaximumPayloadSize);

        TestHarness.PumpFor(100, server);
        HashSet<ushort> resentAckedSequences = ReceiveAckedSequences(attacker, receiveBuffer);

        Assert.True(ackedSequences.Contains(2) && !ackedSequences.Contains(3), $"with the abandoned message held, [{string.Join(", ", ackedSequences)}] were acknowledged where only 2 fits the budget");
        Assert.True(resentAckedSequences.Contains(3), "the abandoned message's bytes were still counted after its assembly expired");
    }

    /// <summary>
    /// SYNREL-2: a reliable message the split-message table has no room to count is refused as past the budget only where the reassembler
    /// would hold it, because the concurrent-assembly cap is disabled. At the default cap the reassembler refuses it itself, without a
    /// violation, as it always has.
    /// </summary>
    /// <param name="maximumConcurrentAssemblies">The receiver's cap on messages in reassembly.</param>
    [Theory]
    [InlineData(SegmentConfig.DisabledMaximumConcurrentAssembliesPerConnection)]
    [InlineData(16u)]
    public void SYNREL2_MessageTheTableCannotCount_IsNeverHeldUncounted(uint maximumConcurrentAssemblies)
    {
        const byte SegmentCount = 2;
        const ushort MessageCount = SynapseConnection.SplitMessageTableLength + 1;
        const int PayloadLength = 10;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.Segment.MaximumConcurrentAssembliesPerConnection = maximumConcurrentAssemblies));
        List<ViolationEventArgs> violations = RecordViolations(server);
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket attacker = TestHarness.CreateRawSocket();

        attacker.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 2000, server), "the attacker never connected");

        SynapseConnection attackerConnection = server.Connections.Connections[0];
        PacedSender pacedSender = new(attacker, target, server);

        // One more incomplete message than the table holds, each behind the never-sent sequence 0.
        for (ushort sequence = 1; sequence <= MessageCount; sequence++)
            pacedSender.Send(PacketType.ReliableSegmented, sequence, segmentId: sequence, segmentIndex: 0, SegmentCount, PayloadLength);

        TestHarness.PumpFor(100, server);

        Assert.Equal(SynapseConnection.SplitMessageTableLength * (long)PayloadLength, MeasureAssemblyBytes(attackerConnection, lastSegmentId: MessageCount, PayloadLength));
        AssertReportedAsRateLimited(violations, maximumConcurrentAssemblies == SegmentConfig.DisabledMaximumConcurrentAssembliesPerConnection ? 1 : 0);
    }

    /// <summary>
    /// SYNREL-2: nothing goes past the allowance, not even the segments of the message a gap waits on, so assemblies opened on the
    /// awaited sequence hold no more than the allowance.
    /// </summary>
    [Fact]
    public void SYNREL2_AssembliesOnTheAwaitedSequence_HoldNoMoreThanTheBudget()
    {
        const byte SegmentCount = 255;
        const int SentSegmentCount = 250;
        const ushort AssemblyCount = 12;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port));
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket attacker = TestHarness.CreateRawSocket();

        attacker.SendTo(BuildHandshake(), target);
        Assert.True(TestHarness.PumpUntil(() => server.Connections.Count == 1, 2000, server), "the attacker never connected");

        SynapseConnection attackerConnection = server.Connections.Connections[0];
        int segmentPayloadLength = (int)server.MaximumTransmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented);
        PacedSender pacedSender = new(attacker, target, server);

        // Every assembly names sequence 0, the one the receiver waits on, under a segment id of its own.
        for (ushort segmentId = 1; segmentId <= AssemblyCount; segmentId++)
        {
            for (int segmentIndex = 0; segmentIndex < SentSegmentCount; segmentIndex++)
                pacedSender.Send(PacketType.ReliableSegmented, sequence: 0, segmentId, (byte)segmentIndex, SegmentCount, segmentPayloadLength);
        }

        TestHarness.PumpFor(200, server);

        long assemblyBytes = MeasureAssemblyBytes(attackerConnection, lastSegmentId: AssemblyCount, segmentPayloadLength);
        long byteAllowance = server.Config.Security.MaximumBytesPerSecond;

        Assert.True(assemblyBytes <= byteAllowance, $"[{assemblyBytes}] payload bytes held in assemblies on the awaited sequence against an allowance of [{byteAllowance}]");
    }

    /// <summary>
    /// SYNREL-1: an honest sender whose messages queue past a lost one beyond the receiver's byte allowance still delivers every message,
    /// in order, once the lost one arrives. What does not fit is dropped unacknowledged, and the sender's resends bring it back after the
    /// gap drains, which gives the whole budget back for the next gap.
    /// </summary>
    /// <remarks>
    /// This is the normal case, where the message the gap waits on fits: the first lost message is unsegmented, so it is delivered as it
    /// arrives and never held, and the second gap's segmented one fits beside a backlog under the budget. Each burst ends with an
    /// unsegmented message, and the second one lands past the budget: had it been acknowledged, the sender would have released it and it
    /// would never arrive. The sender is allowed more resends than the default because the test holds the gap open, on purpose, for
    /// longer than the default resends last.
    /// </remarks>
    [Fact]
    public void SYNREL1_HonestSenderPastTheBudget_DeliversEveryMessageInOrderOnceTheGapFills()
    {
        const int ByteAllowance = 128 * 1024;
        const int SegmentsPerMessage = 20;
        const int UnsegmentedMessageLength = 1000;
        const int MessagesPerBurst = 5;
        const int BurstCount = 2;
        const int SecondGapMessageCount = 3;

        GapTransform gapTransform = new();
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.Security.MaximumBytesPerSecond = ByteAllowance));
        using SynapseManager client = new(TestHarness.ClientConfig(config =>
        {
            config.PacketTransform = gapTransform;
            config.Reliable.MaximumRetries = 40;
        }));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        List<ViolationEventArgs> violations = RecordViolations(server);
        List<byte[]> deliveredPayloads = [];
        server.PacketReceived += packetReceivedEventArgs => deliveredPayloads.Add(packetReceivedEventArgs.Payload.ToArray());

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => clientConnection.State is ConnectionState.Connected && server.Connections.Count == 1, 3000, server, client), "the sender never connected");

        int segmentedMessageLength = SegmentsPerMessage * ((int)client.MaximumTransmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented));
        List<byte[]> sentMessages = [BuildFilledMessage(GapTransform.GapMessageFill, UnsegmentedMessageLength)];

        gapTransform.IsDroppingGapMessage = true;
        client.Send(clientConnection, sentMessages[0], isReliable: true);

        // One burst per rate window, each under the allowance, so nothing is rate limited and every drop is the budget's.
        for (int burst = 0; burst < BurstCount; burst++)
        {
            for (int burstIndex = 0; burstIndex < MessagesPerBurst; burstIndex++)
            {
                byte[] message = BuildFilledMessage((byte)sentMessages.Count, burstIndex == MessagesPerBurst - 1 ? UnsegmentedMessageLength : segmentedMessageLength);
                sentMessages.Add(message);
                client.Send(clientConnection, message, isReliable: true);
            }

            TestHarness.PumpFor(1100, server, client);
        }

        long droppedPastBudget = server.Telemetry.PacketsDroppedIn;
        gapTransform.IsDroppingGapMessage = false;

        Assert.True(
            TestHarness.PumpUntil(() => deliveredPayloads.Count == sentMessages.Count, 10_000, server, client),
            $"[{deliveredPayloads.Count}] of [{sentMessages.Count}] messages arrived once the gap filled, after [{droppedPastBudget}] datagrams were dropped past the budget");

        Assert.True(droppedPastBudget > 0, "the backlog never went past the budget, so the test proved nothing");
        Assert.Equal(0, serverEvents.ConnectionsClosed);
        AssertReportedAsRateLimited(violations, (int)server.Telemetry.PacketsDroppedIn);

        // The drained gap gives its bytes back: in a fresh rate window, a second gap holds a backlog under the budget without dropping any.
        TestHarness.PumpFor(1100, server, client);

        SynapseConnection serverConnection = server.Connections.Connections[0];
        long droppedBeforeSecondGap = server.Telemetry.PacketsDroppedIn;
        sentMessages.Add(BuildFilledMessage(GapTransform.GapMessageFill, segmentedMessageLength));

        gapTransform.IsDroppingGapMessage = true;
        client.Send(clientConnection, sentMessages[^1], isReliable: true);

        for (int i = 0; i < SecondGapMessageCount; i++)
        {
            sentMessages.Add(BuildFilledMessage((byte)sentMessages.Count, segmentedMessageLength));
            client.Send(clientConnection, sentMessages[^1], isReliable: true);
        }

        Assert.True(
            TestHarness.PumpUntil(() => serverConnection.ReorderBuffer.Count == SecondGapMessageCount, 2000, server, client),
            $"[{serverConnection.ReorderBuffer.Count}] of [{SecondGapMessageCount}] messages under the budget were held behind a second gap, with [{server.Telemetry.PacketsDroppedIn - droppedBeforeSecondGap}] dropped");

        gapTransform.IsDroppingGapMessage = false;

        Assert.True(TestHarness.PumpUntil(() => deliveredPayloads.Count == sentMessages.Count, 5000, server, client), $"[{deliveredPayloads.Count}] of [{sentMessages.Count}] messages arrived once the second gap filled");
        Assert.Equal(droppedBeforeSecondGap, server.Telemetry.PacketsDroppedIn);

        for (int i = 0; i < sentMessages.Count; i++)
            Assert.True(sentMessages[i].AsSpan().SequenceEqual(deliveredPayloads[i]), $"message [{i}] arrived out of order or altered");
    }

    /// <summary>
    /// SYNREL-1: a sender that fills the budget behind its own gap, while the message the gap waits on is segmented, stays stuck behind it,
    /// since nothing goes past the allowance. That is the accepted outcome, and it ends like any dead link: what the receiver holds never
    /// goes past the allowance, the sender's resends run out, and the receiver releases the connection on its timeout.
    /// </summary>
    [Fact]
    public void SYNREL1_SenderStuckBehindItsOwnGap_IsHeldToTheBudgetUntilTheConnectionTimesOut()
    {
        const int ByteAllowance = 128 * 1024;
        const int SegmentsPerMessage = 20;
        const int MessagesPerBurst = 5;
        const int BurstCount = 2;
        // Longer than the silence after the first burst, when everything sent is acknowledged and only the lost message is resent.
        const uint TimeoutMilliseconds = 2000;
        const int ReleaseWaitMilliseconds = 10_000;

        GapTransform gapTransform = new();
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            config.Security.MaximumBytesPerSecond = ByteAllowance;
            config.Connection.TimeoutMilliseconds = TimeoutMilliseconds;
        }));
        using SynapseManager client = new(TestHarness.ClientConfig(config => config.PacketTransform = gapTransform));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        TestHarness.EventRecorder clientEvents = new();
        clientEvents.Attach(client);

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => clientConnection.State is ConnectionState.Connected && server.Connections.Count == 1, 3000, server, client), "the sender never connected");

        SynapseConnection serverConnection = server.Connections.Connections[0];
        int segmentPayloadLength = (int)client.MaximumTransmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented);
        int segmentedMessageLength = SegmentsPerMessage * segmentPayloadLength;

        // The lost message is segmented, so it needs room in the budget that the backlog behind it is about to fill.
        gapTransform.IsDroppingGapMessage = true;
        client.Send(clientConnection, BuildFilledMessage(GapTransform.GapMessageFill, segmentedMessageLength), isReliable: true);

        // One burst per rate window, each under the allowance, so nothing is rate limited and every drop is the budget's.
        for (int burst = 0; burst < BurstCount; burst++)
        {
            for (int burstIndex = 0; burstIndex < MessagesPerBurst; burstIndex++)
                client.Send(clientConnection, BuildFilledMessage((byte)(1 + burst * MessagesPerBurst + burstIndex), segmentedMessageLength), isReliable: true);

            TestHarness.PumpFor(1100, server, client);
        }

        // The lost message's resends now reach the receiver, and find the budget full.
        gapTransform.IsDroppingGapMessage = false;

        long peakHeldBytes = 0;
        long deadlineMilliseconds = Environment.TickCount64 + ReleaseWaitMilliseconds;

        while (server.Connections.Count > 0 && Environment.TickCount64 < deadlineMilliseconds)
        {
            // Every message sent opened its own segment id, 1 to 11, so measuring those covers everything held.
            long heldBytes = MeasureAssemblyBytes(serverConnection, lastSegmentId: 1 + BurstCount * MessagesPerBurst, segmentPayloadLength);

            foreach (ArraySegment<byte> reorderedPayload in serverConnection.ReorderBuffer.Values)
                heldBytes += reorderedPayload.Count;

            peakHeldBytes = Math.Max(peakHeldBytes, heldBytes);

            server.Poll();
            client.Poll();
            Thread.Sleep(1);
        }

        Assert.True(server.Connections.Count == 0, $"the receiver still held the stuck connection [{ReleaseWaitMilliseconds}] ms after the gap's resends began");
        Assert.True(peakHeldBytes > ByteAllowance - segmentPayloadLength && peakHeldBytes <= ByteAllowance, $"the receiver held at most [{peakHeldBytes}] payload bytes against an allowance of [{ByteAllowance}]");
        Assert.True(serverEvents.PacketsReceived == 0, $"[{serverEvents.PacketsReceived}] messages were delivered past a gap that never filled");
        Assert.Contains(ViolationReason.ReliableExhausted, clientEvents.ViolationReasons);
        Assert.Contains(ViolationReason.Timeout, serverEvents.ViolationReasons);
        Assert.All(serverEvents.ViolationReasons, violationReason => Assert.True(violationReason is ViolationReason.RateLimitExceeded or ViolationReason.Timeout, $"the receiver raised [{violationReason}]"));
    }

    /// <summary>
    /// The budget is off wherever the per-connection limits it is sized from are off, so a receiver that applies no byte allowance, has
    /// security disabled, or exempts the connection from its rate limits holds a backlog past a lost message well beyond the default
    /// allowance, and delivers all of it once the gap fills.
    /// </summary>
    /// <param name="receiverLimits">How the receiver's limits are turned off.</param>
    [Theory]
    [InlineData(ReceiverLimits.RateLimitsDisabled)]
    [InlineData(ReceiverLimits.SecurityDisabled)]
    [InlineData(ReceiverLimits.ConnectionExempt)]
    public void SYNREL1_ReceiverWithItsLimitsOff_HoldsABacklogPastTheDefaultBudget(ReceiverLimits receiverLimits)
    {
        const byte SegmentCount = 255;
        const int BacklogMessageCount = 9;
        const int UnsegmentedMessageLength = 1000;

        GapTransform gapTransform = new();
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config =>
        {
            if (receiverLimits is ReceiverLimits.RateLimitsDisabled)
            {
                config.Security.MaximumPacketsPerSecond = SecurityConfig.DisabledMaximumPacketsPerSecond;
                config.Security.MaximumBytesPerSecond = SecurityConfig.DisabledMaximumBytesPerSecond;
            }
            else if (receiverLimits is ReceiverLimits.SecurityDisabled)
            {
                config.Security.Enabled = false;
            }
        }));
        using SynapseManager client = new(TestHarness.ClientConfig(config =>
        {
            config.PacketTransform = gapTransform;
            config.Reliable.MaximumRetries = 40;
        }));

        if (receiverLimits is ReceiverLimits.ConnectionExempt)
            server.ConnectionEstablished += connectionEventArgs => connectionEventArgs.Connection.IsRateLimitExempt = true;

        List<byte[]> deliveredPayloads = [];
        server.PacketReceived += packetReceivedEventArgs => deliveredPayloads.Add(packetReceivedEventArgs.Payload.ToArray());

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => clientConnection.State is ConnectionState.Connected && server.Connections.Count == 1, 3000, server, client), "the sender never connected");

        SynapseConnection serverConnection = server.Connections.Connections[0];
        int messageLength = SegmentCount * ((int)client.MaximumTransmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented));
        List<byte[]> sentMessages = [BuildFilledMessage(GapTransform.GapMessageFill, messageLength)];

        gapTransform.IsDroppingGapMessage = true;
        client.Send(clientConnection, sentMessages[0], isReliable: true);

        // Segmented messages past the default allowance, then one unsegmented, since each takes its own check.
        for (int i = 1; i <= BacklogMessageCount; i++)
        {
            sentMessages.Add(BuildFilledMessage((byte)i, i < BacklogMessageCount ? messageLength : UnsegmentedMessageLength));
            client.Send(clientConnection, sentMessages[i], isReliable: true);
            TestHarness.PumpFor(30, server, client);
        }

        Assert.True(TestHarness.PumpUntil(() => serverConnection.ReorderBuffer.Count == BacklogMessageCount, 5000, server, client), $"[{serverConnection.ReorderBuffer.Count}] of [{BacklogMessageCount}] messages were held behind the gap");

        long heldBytes = 0;

        foreach (ArraySegment<byte> reorderedPayload in serverConnection.ReorderBuffer.Values)
            heldBytes += reorderedPayload.Count;

        long defaultByteAllowance = new SecurityConfig().MaximumBytesPerSecond;
        long droppedPastBudget = server.Telemetry.PacketsDroppedIn;
        gapTransform.IsDroppingGapMessage = false;

        Assert.True(TestHarness.PumpUntil(() => deliveredPayloads.Count == sentMessages.Count, 10_000, server, client), $"[{deliveredPayloads.Count}] of [{sentMessages.Count}] messages arrived once the gap filled");
        Assert.True(heldBytes > defaultByteAllowance && droppedPastBudget == 0, $"[{heldBytes}] payload bytes held and [{droppedPastBudget}] datagrams dropped, where the default allowance is [{defaultByteAllowance}]");

        for (int i = 0; i < sentMessages.Count; i++)
            Assert.True(sentMessages[i].AsSpan().SequenceEqual(deliveredPayloads[i]), $"message [{i}] arrived out of order or altered");
    }

    /// <summary>
    /// SYNREL-3: duplicates of a segment the receiver already holds must not each draw a segment acknowledgement, which turns a 7-byte
    /// datagram into a 35-byte one.
    /// </summary>
    [Fact]
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
    /// SYNREL-3: segment acknowledgements are paced, not removed. A reliable message whose every acknowledgement is lost, along with the
    /// first segment acknowledgement it draws, still completes on the sender within its default resends, through the reports its resent
    /// segments draw once the interval has passed.
    /// </summary>
    [Fact]
    public void SYNREL3_MessageWhoseAcknowledgementIsLost_StillCompletes()
    {
        const int SegmentsPerMessage = 5;

        AcknowledgementDroppingTransform acknowledgementDroppingTransform = new();
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, config => config.PacketTransform = acknowledgementDroppingTransform));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder clientEvents = new();
        clientEvents.Attach(client);
        List<byte[]> deliveredPayloads = [];
        server.PacketReceived += packetReceivedEventArgs => deliveredPayloads.Add(packetReceivedEventArgs.Payload.ToArray());

        server.Start();
        client.Start();

        SynapseConnection clientConnection = client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => clientConnection.State is ConnectionState.Connected && server.Connections.Count == 1, 3000, server, client), "the sender never connected");

        byte[] message = BuildFilledMessage(fill: 1, SegmentsPerMessage * ((int)client.MaximumTransmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented)));

        acknowledgementDroppingTransform.IsDroppingAcknowledgements = true;
        client.Send(clientConnection, message, isReliable: true);

        // Released, not torn down: a sender that exhausts its resends closes the connection, which empties the queue as well.
        Assert.True(
            TestHarness.PumpUntil(() => clientConnection.PendingReliableQueue.Count == 0 || clientEvents.ConnectionsClosed > 0, 4000, server, client) && clientEvents.ConnectionsClosed == 0,
            $"the message was never released: [{acknowledgementDroppingTransform.DroppedAckCount}] acknowledgements lost, [{string.Join(", ", clientEvents.ViolationReasons)}] raised");

        Assert.True(acknowledgementDroppingTransform.DroppedAckCount > 0 && acknowledgementDroppingTransform.DroppedSegmentAckCount == 1, "no acknowledgement was lost, so the test proved nothing");
        Assert.True(deliveredPayloads.Count == 1 && message.AsSpan().SequenceEqual(deliveredPayloads[0]), $"the message was delivered [{deliveredPayloads.Count}] times");
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
    /// H8: with <see cref="SecurityConfig.DataSessionTokenEnabled"/> on at both ends, an unreliable data packet forged from a connected
    /// peer's address, from a bare type byte to one carrying a payload, is dropped and counted rather than delivered as the peer's, and
    /// raises no violation; the peer's own unreliable data still arrives.
    /// </summary>
    /// <param name="length">The forged datagram's length.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(64)]
    public void H8_DataSessionToken_ForgedUnreliablePacket_IsNotDeliveredAsThePeers(int length)
    {
        using ConnectedPair pair = new(EnableDataSessionToken, EnableDataSessionToken);
        List<byte[]> serverDeliveries = RecordDeliveries(pair.Server);
        long droppedBefore = pair.Server.Telemetry.PacketsDroppedIn;

        // PacketType.None followed by random bytes, which the receiver's token is never likely to match.
        byte[] forged = new byte[length];
        RandomNumberGenerator.Fill(forged.AsSpan(PacketHeader.TypeSize));

        pair.Forge(forged);
        TestHarness.PumpFor(200, pair.Server, pair.Client);

        Assert.True(serverDeliveries.Count == 0, $"a forged [{length}]-byte unreliable packet was delivered as the peer's");
        Assert.Equal(droppedBefore + 1, pair.Server.Telemetry.PacketsDroppedIn);
        Assert.True(pair.ServerEvents.Violations == 0, $"a forged unreliable packet raised [{string.Join(", ", pair.ServerEvents.ViolationReasons)}]");

        byte[] honestPayload = BuildFilledMessage(0x3C, 32);
        pair.Client.Send(pair.ClientConnection, new(honestPayload), isReliable: false);

        Assert.True(
            TestHarness.PumpUntil(() => serverDeliveries.Count == 1, 2000, pair.Server, pair.Client) && serverDeliveries[0].AsSpan().SequenceEqual(honestPayload),
            "the peer's own unreliable payload did not arrive intact after the forged one");
    }

    /// <summary>
    /// M7: with data session tokens on at both ends, a reliable data packet forged from a connected peer's address with the sequence the
    /// peer's first reliable message will carry is dropped and counted: it is neither delivered nor acknowledged, and the peer's own message
    /// under that sequence then arrives intact rather than being discarded as a resend.
    /// </summary>
    [Fact]
    public void M7_DataSessionToken_ForgedReliablePacket_IsNeitherDeliveredNorAcknowledged()
    {
        using ConnectedPair pair = new(EnableDataSessionToken, EnableDataSessionToken);
        List<byte[]> serverDeliveries = RecordDeliveries(pair.Server);
        int reliableHeaderSize = PacketHeader.ComputeHeaderSize(PacketType.Reliable);
        long droppedBefore = pair.Server.Telemetry.PacketsDroppedIn;
        long sentBefore = pair.Server.Telemetry.PacketsOut;

        byte[] forged = new byte[reliableHeaderSize + 16];
        PacketHeader.Write(forged, PacketType.Reliable, sequence: 0, segmentId: 0, segmentIndex: 0, segmentCount: 0);
        forged.AsSpan(reliableHeaderSize).Fill(0xEE);

        pair.Forge(forged);
        TestHarness.PumpFor(200, pair.Server, pair.Client);

        Assert.True(serverDeliveries.Count == 0, "a forged reliable packet was delivered as the peer's");
        Assert.True(pair.Server.Telemetry.PacketsOut == sentBefore, $"the server sent [{pair.Server.Telemetry.PacketsOut - sentBefore}] datagrams after a forged reliable packet, acknowledging it");
        Assert.Equal(droppedBefore + 1, pair.Server.Telemetry.PacketsDroppedIn);
        Assert.True(pair.ServerEvents.Violations == 0, $"a forged reliable packet raised [{string.Join(", ", pair.ServerEvents.ViolationReasons)}]");

        byte[] honestPayload = BuildFilledMessage(0x3C, 16);
        pair.Client.Send(pair.ClientConnection, new(honestPayload), isReliable: true);

        Assert.True(
            TestHarness.PumpUntil(() => serverDeliveries.Count == 1, 2000, pair.Server, pair.Client) && serverDeliveries[0].AsSpan().SequenceEqual(honestPayload),
            "the peer's own first reliable message did not arrive intact after a forged packet carrying its sequence");
    }

    /// <summary>
    /// SYNCONN-1: with data session tokens on at both ends, a segment forged from a connected peer's address that would be a violation from
    /// the peer itself is dropped and counted instead, and the peer's session goes on untouched: a segment larger than the transmission unit
    /// on either channel, one contradicting the segment count its message opened with, and one declaring an assembly over
    /// <see cref="SecurityConfig.MaximumReassembledPacketSize"/>.
    /// </summary>
    /// <param name="malformedSegment">Which malformed segment is forged.</param>
    [Theory]
    [InlineData(MalformedSegment.UnreliableLargerThanTransmissionUnit)]
    [InlineData(MalformedSegment.ReliableLargerThanTransmissionUnit)]
    [InlineData(MalformedSegment.ContradictingSegmentCount)]
    [InlineData(MalformedSegment.DeclaredAssemblyOversized)]
    public void SYNCONN1_DataSessionToken_ForgedMalformedSegment_DoesNotEndThePeersSession(MalformedSegment malformedSegment)
    {
        const ushort SegmentId = 900;

        using ConnectedPair pair = new(
            synapseConfig =>
            {
                EnableDataSessionToken(synapseConfig);
                synapseConfig.Security.MaximumReassembledPacketSize = 100_000;
            },
            EnableDataSessionToken);

        SynapseConnection serverConnection = pair.ServerConnection;
        int oversizedLength = (int)pair.Server.MaximumTransmissionUnit + 1;
        long droppedBefore = pair.Server.Telemetry.PacketsDroppedIn;

        List<byte[]> forgeries = malformedSegment switch
        {
            MalformedSegment.UnreliableLargerThanTransmissionUnit => [BuildSegment(PacketType.Segmented, SegmentId, segmentIndex: 0, segmentCount: 2, oversizedLength)],
            MalformedSegment.ReliableLargerThanTransmissionUnit => [BuildSegment(PacketType.ReliableSegmented, SegmentId, segmentIndex: 0, segmentCount: 2, oversizedLength)],
            MalformedSegment.ContradictingSegmentCount => [BuildSegment(PacketType.Segmented, SegmentId, segmentIndex: 0, segmentCount: 2, 16), BuildSegment(PacketType.Segmented, SegmentId, segmentIndex: 1, segmentCount: 3, 16)],
            _ => [BuildSegment(PacketType.ReliableSegmented, SegmentId, segmentIndex: 0, segmentCount: byte.MaxValue, 16)]
        };

        foreach (byte[] forgery in forgeries)
            pair.Forge(forgery);

        TestHarness.PumpFor(300, pair.Server, pair.Client);

        AssertSessionIntact(pair, serverConnection, $"a forged [{malformedSegment}] segment");
        Assert.Equal(droppedBefore + forgeries.Count, pair.Server.Telemetry.PacketsDroppedIn);
        Assert.True(pair.ServerEvents.Violations == 0, $"a forged [{malformedSegment}] segment raised [{string.Join(", ", pair.ServerEvents.ViolationReasons)}]");
    }

    /// <summary>
    /// M7: with data session tokens on at both ends, reliable packets forged from a connected peer's address ahead of the sequence it
    /// expects, one more than the reorder buffer holds, are dropped and counted rather than held, so they cannot overflow the buffer and end
    /// the peer's session; the peer's own reliable messages then arrive in order.
    /// </summary>
    [Fact]
    public void M7_DataSessionToken_ForgedOutOfOrderReliablePackets_DoNotOverflowThePeersReorderBuffer()
    {
        using ConnectedPair pair = new(EnableDataSessionToken, EnableDataSessionToken);
        SynapseConnection serverConnection = pair.ServerConnection;
        List<byte[]> serverDeliveries = RecordDeliveries(pair.Server);
        int forgedCount = (int)pair.Server.Config.Security.MaximumOutOfOrderReliablePackets + 1;
        long droppedBefore = pair.Server.Telemetry.PacketsDroppedIn;

        // Sequences 1 onward, never 0, so each would wait in the reorder buffer for a gap that never fills.
        for (int sequence = 1; sequence <= forgedCount; sequence++)
        {
            byte[] forged = new byte[PacketHeader.ComputeHeaderSize(PacketType.Reliable) + 1];
            PacketHeader.Write(forged, PacketType.Reliable, (ushort)sequence, segmentId: 0, segmentIndex: 0, segmentCount: 0);
            pair.Forge(forged);
        }

        TestHarness.PumpFor(300, pair.Server, pair.Client);

        AssertSessionIntact(pair, serverConnection, $"[{forgedCount}] forged out-of-order reliable packets");
        Assert.True(serverConnection.ReorderBuffer.Count == 0, $"[{serverConnection.ReorderBuffer.Count}] forged reliable packets wait in the reorder buffer");
        Assert.Equal(droppedBefore + forgedCount, pair.Server.Telemetry.PacketsDroppedIn);
        Assert.True(pair.ServerEvents.Violations == 0, $"forged reliable packets raised [{string.Join(", ", pair.ServerEvents.ViolationReasons)}]");

        for (byte fill = 1; fill <= 3; fill++)
            pair.Client.Send(pair.ClientConnection, new(BuildFilledMessage(fill, 8)), isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => serverDeliveries.Count == 3, 2000, pair.Server, pair.Client), $"[{serverDeliveries.Count}] of the peer's 3 reliable messages arrived");

        for (byte fill = 1; fill <= 3; fill++)
            Assert.True(serverDeliveries[fill - 1].AsSpan().SequenceEqual(BuildFilledMessage(fill, 8)), $"the peer's reliable message [{fill}] arrived out of order or damaged");
    }

    /// <summary>
    /// With data session tokens on at both ends, honest traffic of every kind arrives intact in both directions: unreliable and reliable
    /// payloads up to the largest one packet carries with the token, and split ones just past it and around segment boundaries. Every data
    /// packet ends with its receiver's session token, and none is larger than the transmission unit, which the largest fill exactly.
    /// </summary>
    [Fact]
    public void DataSessionToken_HonestTrafficOfEveryKind_ArrivesIntactWithinTheTransmissionUnit()
    {
        RecordingTransform serverTransform = new();
        RecordingTransform clientTransform = new();

        using ConnectedPair pair = new(
            synapseConfig =>
            {
                EnableDataSessionToken(synapseConfig);
                synapseConfig.PacketTransform = serverTransform;
            },
            synapseConfig =>
            {
                EnableDataSessionToken(synapseConfig);
                synapseConfig.PacketTransform = clientTransform;
            });

        List<byte[]> serverDeliveries = RecordDeliveries(pair.Server);
        List<byte[]> clientDeliveries = RecordDeliveries(pair.Client);
        int transmissionUnit = (int)pair.Server.MaximumTransmissionUnit;
        int largestWholePayload = pair.Server.MaximumPayloadSize - SessionTokenSize;
        int reliableSegmentPayload = transmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented) - SessionTokenSize;
        int unreliableSegmentPayload = transmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.Segmented) - SessionTokenSize;
        int[] payloadLengths = [0, 1, largestWholePayload - 1, largestWholePayload, largestWholePayload + 1, pair.Server.MaximumPayloadSize, reliableSegmentPayload * 2, reliableSegmentPayload * 2 + 1, unreliableSegmentPayload * 2, unreliableSegmentPayload * 2 + 1, 40_000];
        bool[] reliabilities = [false, true];

        foreach (bool isReliable in reliabilities)
        {
            foreach (int payloadLength in payloadLengths)
            {
                byte[] payload = new byte[payloadLength];
                RandomNumberGenerator.Fill(payload);

                int expectedCount = serverDeliveries.Count + 1;
                string channel = isReliable ? "reliable" : "unreliable";

                pair.Client.Send(pair.ClientConnection, new(payload), isReliable);
                pair.Server.Send(pair.ServerConnection, new(payload), isReliable);

                Assert.True(
                    TestHarness.PumpUntil(() => serverDeliveries.Count == expectedCount && clientDeliveries.Count == expectedCount, 3000, pair.Server, pair.Client),
                    $"a [{payloadLength}]-byte {channel} payload did not arrive in both directions");
                Assert.True(serverDeliveries[^1].AsSpan().SequenceEqual(payload) && clientDeliveries[^1].AsSpan().SequenceEqual(payload), $"a [{payloadLength}]-byte {channel} payload arrived damaged");
            }
        }

        Assert.Equal(2 * payloadLengths.Length, serverDeliveries.Count);
        Assert.Equal(transmissionUnit, clientTransform.LargestDatagramLength);
        Assert.Equal(transmissionUnit, serverTransform.LargestDatagramLength);
        Assert.All(clientTransform.DataPacketTrailers, dataPacketTrailer => Assert.Equal(pair.ServerConnection.LocalSessionToken, dataPacketTrailer));
        Assert.All(serverTransform.DataPacketTrailers, dataPacketTrailer => Assert.Equal(pair.ClientConnection.LocalSessionToken, dataPacketTrailer));
    }

    /// <summary>
    /// With data session tokens on at both ends, reliable messages a client sends before its handshake is answered, one whole and one split,
    /// leave without the token, since the server's is not known yet, and the server drops them; once the session is established they are
    /// resent carrying it, and arrive intact and in order.
    /// </summary>
    [Fact]
    public void DataSessionToken_ReliableMessagesSentBeforeTheHandshakeIsAnswered_ArriveOnceEstablished()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, EnableDataSessionToken));
        using SynapseManager client = new(TestHarness.ClientConfig(EnableDataSessionToken));
        List<byte[]> serverDeliveries = RecordDeliveries(server);

        server.Start();
        client.Start();

        SynapseConnection connection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));
        // 125 bytes and the 3-byte header fill a 128-byte pooled array exactly, so the token fits after them only in the room left for it.
        byte[] wholeMessage = BuildFilledMessage(0x11, 125);
        byte[] splitMessage = new byte[client.MaximumPayloadSize * 4];
        RandomNumberGenerator.Fill(splitMessage);

        // Neither engine has been polled, so the connection is still waiting on its handshake.
        Assert.Equal(ConnectionState.Pending, connection.State);
        client.Send(connection, new(wholeMessage), isReliable: true);
        client.Send(connection, new(splitMessage), isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => serverDeliveries.Count == 2, 5000, server, client), $"[{serverDeliveries.Count}] of 2 reliable messages sent before the handshake was answered arrived");
        Assert.True(serverDeliveries[0].AsSpan().SequenceEqual(wholeMessage) && serverDeliveries[1].AsSpan().SequenceEqual(splitMessage), "the reliable messages sent before the handshake was answered arrived damaged or out of order");
    }

    /// <summary>
    /// A client with data session tokens on asks for them in its handshake, and while that handshake waits for an answer it requires its
    /// token on the data it receives: data ending with it is delivered without it, and data without it is dropped. The answer then decides:
    /// the session keeps the token only when the answer asked for it too, and otherwise delivers data exactly as sent.
    /// </summary>
    /// <param name="isAnswerAsking">True when the answering handshake asks for data session tokens.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DataSessionToken_PendingConnection_RequiresTheTokenOnTheDataItReceives(bool isAnswerAsking)
    {
        using Socket peer = TestHarness.CreateRawSocket();
        using SynapseManager client = new(TestHarness.ClientConfig(EnableDataSessionToken));
        List<byte[]> clientDeliveries = RecordDeliveries(client);

        client.Start();

        IPEndPoint clientEndPoint = client.BoundEndPoints[0];
        SynapseConnection connection = client.Connect((IPEndPoint)peer.LocalEndPoint!);
        byte[] receiveBuffer = new byte[64];

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, client, PacketType.Handshake, out int handshakeLength), "the client sent no handshake");
        Assert.True(
            handshakeLength == PacketHeader.TypeSize + PacketHeader.HandshakeNonceSize + 1 && receiveBuffer[handshakeLength - 1] == DataSessionTokenVersion,
            $"the client's [{handshakeLength}]-byte handshake ending in [{receiveBuffer[handshakeLength - 1]}] does not ask for data session tokens");

        byte[] payload = BuildFilledMessage(0x42, 20);
        byte[] untokened = [(byte)PacketType.None, .. payload];
        byte[] tokened = BuildTokenedPacket(untokened, receiveBuffer.AsSpan(PacketHeader.TypeSize, SessionTokenSize));

        peer.SendTo(untokened, clientEndPoint);
        peer.SendTo(tokened, clientEndPoint);
        TestHarness.PumpFor(100, client);

        Assert.Equal(ConnectionState.Pending, connection.State);
        Assert.True(
            clientDeliveries.Count == 1 && clientDeliveries[0].AsSpan().SequenceEqual(payload),
            $"a pending connection asking for data session tokens delivered [{clientDeliveries.Count}] payloads, not just the tokened one without its token");

        peer.SendTo(BuildHandshake(PacketHeader.HandshakeNonceSize + 1, isAnswerAsking ? DataSessionTokenVersion : SessionTokenVersion), clientEndPoint);
        Assert.True(TestHarness.PumpUntil(() => connection.State is ConnectionState.Connected, 2000, client), "the peer's answer did not connect the client");

        peer.SendTo(untokened, clientEndPoint);
        peer.SendTo(tokened, clientEndPoint);
        TestHarness.PumpFor(100, client);

        if (isAnswerAsking)
            Assert.True(clientDeliveries.Count == 2 && clientDeliveries[1].AsSpan().SequenceEqual(payload), $"a session with data session tokens delivered [{clientDeliveries.Count - 1}] payloads, not just the tokened one without its token");
        else
            Assert.True(
                clientDeliveries.Count == 3 && clientDeliveries[1].AsSpan().SequenceEqual(untokened.AsSpan(PacketHeader.TypeSize)) && clientDeliveries[2].AsSpan().SequenceEqual(tokened.AsSpan(PacketHeader.TypeSize)),
                $"a session without data session tokens delivered [{clientDeliveries.Count - 1}] payloads, not both exactly as sent");
    }

    /// <summary>
    /// Data session tokens are used only between two engines that both have <see cref="SecurityConfig.DataSessionTokenEnabled"/> on: each
    /// side's handshake asks for them exactly when it has them on, and only when both do does every data packet grow by the 4-byte token and
    /// a data packet forged from the peer's address stop being delivered. Any other pairing puts on the wire exactly what it always did.
    /// </summary>
    /// <param name="isServerEnabled">True when the server has data session tokens on.</param>
    /// <param name="isClientEnabled">True when the client has data session tokens on.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void DataSessionToken_IsUsedOnlyWhenBothEnginesEnableIt(bool isServerEnabled, bool isClientEnabled)
    {
        const int PayloadLength = 100;

        RecordingTransform serverTransform = new();
        RecordingTransform clientTransform = new();

        using ConnectedPair pair = new(
            synapseConfig =>
            {
                synapseConfig.Security.DataSessionTokenEnabled = isServerEnabled;
                synapseConfig.PacketTransform = serverTransform;
            },
            synapseConfig =>
            {
                synapseConfig.Security.DataSessionTokenEnabled = isClientEnabled;
                synapseConfig.PacketTransform = clientTransform;
            });

        List<byte[]> serverDeliveries = RecordDeliveries(pair.Server);
        List<byte[]> clientDeliveries = RecordDeliveries(pair.Client);
        bool isUsed = isServerEnabled && isClientEnabled;
        byte[] payload = BuildFilledMessage(0x5A, PayloadLength);

        pair.Client.Send(pair.ClientConnection, new(payload), isReliable: false);
        pair.Client.Send(pair.ClientConnection, new(payload), isReliable: true);
        pair.Server.Send(pair.ServerConnection, new(payload), isReliable: false);
        pair.Server.Send(pair.ServerConnection, new(payload), isReliable: true);

        Assert.True(TestHarness.PumpUntil(() => serverDeliveries.Count == 2 && clientDeliveries.Count == 2, 2000, pair.Server, pair.Client), $"[{serverDeliveries.Count}] and [{clientDeliveries.Count}] of 2 payloads each way arrived");
        Assert.All(serverDeliveries, serverDelivery => Assert.Equal(payload, serverDelivery));
        Assert.All(clientDeliveries, clientDelivery => Assert.Equal(payload, clientDelivery));

        // An unreliable packet is the type byte and the payload, a reliable one adds its sequence, and either ends in the token only when both sides use it.
        int sessionTokenLength = isUsed ? SessionTokenSize : 0;
        int[] expectedLengths = [PacketHeader.TypeSize + PayloadLength + sessionTokenLength, PacketHeader.ComputeHeaderSize(PacketType.Reliable) + PayloadLength + sessionTokenLength];

        Assert.NotEmpty(serverTransform.HandshakeVersions);
        Assert.NotEmpty(clientTransform.HandshakeVersions);
        Assert.All(serverTransform.HandshakeVersions, handshakeVersion => Assert.Equal(isServerEnabled ? DataSessionTokenVersion : SessionTokenVersion, handshakeVersion));
        Assert.All(clientTransform.HandshakeVersions, handshakeVersion => Assert.Equal(isClientEnabled ? DataSessionTokenVersion : SessionTokenVersion, handshakeVersion));
        Assert.Equal(expectedLengths, serverTransform.DataPacketLengths);
        Assert.Equal(expectedLengths, clientTransform.DataPacketLengths);

        // An unreliable packet forged from the client's address is delivered exactly when no token is used, as it always was.
        pair.Forge([(byte)PacketType.None, 0x01, 0x02, 0x03, 0x04, 0x05]);
        TestHarness.PumpFor(200, pair.Server, pair.Client);

        Assert.True(serverDeliveries.Count == (isUsed ? 2 : 3), $"a forged unreliable packet was [{(serverDeliveries.Count == 3 ? "delivered" : "dropped")}] where data session tokens are [{(isUsed ? "used" : "not used")}]");
    }

    /// <summary>
    /// A server answers every handshake asking for data session tokens exactly when it has them on, and carries the token on data packets,
    /// in both directions, only when the peer's handshake asked for them too. A peer whose handshake predates data session tokens, or session
    /// tokens altogether, even one whose random nonce happens to end in the byte that asks for them, is served exactly as before.
    /// </summary>
    /// <param name="isServerEnabled">True when the server has data session tokens on.</param>
    /// <param name="handshakePayloadLength">The length of the peer's handshake payload: a bare nonce, or a nonce and a version.</param>
    /// <param name="lastHandshakeByte">The last byte of the peer's handshake: its version, or the last byte of a bare nonce.</param>
    [Theory]
    [InlineData(true, PacketHeader.HandshakeNonceSize + 1, DataSessionTokenVersion)]
    [InlineData(true, PacketHeader.HandshakeNonceSize + 1, SessionTokenVersion)]
    [InlineData(true, PacketHeader.HandshakeNonceSize, DataSessionTokenVersion)]
    [InlineData(false, PacketHeader.HandshakeNonceSize + 1, DataSessionTokenVersion)]
    [InlineData(false, PacketHeader.HandshakeNonceSize + 1, SessionTokenVersion)]
    public void DataSessionToken_IsUsedWithAPeerOnlyWhenItsHandshakeAsksForIt(bool isServerEnabled, int handshakePayloadLength, byte lastHandshakeByte)
    {
        const int PayloadLength = 50;

        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, synapseConfig => synapseConfig.Security.DataSessionTokenEnabled = isServerEnabled));
        List<byte[]> serverDeliveries = RecordDeliveries(server);

        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();
        byte[] receiveBuffer = new byte[256];
        byte[] handshake = BuildHandshake(handshakePayloadLength);
        handshake[^1] = lastHandshakeByte;

        peer.SendTo(handshake, target);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Handshake, out int answerLength), "the server never answered the handshake");
        Assert.True(
            answerLength == PacketHeader.TypeSize + PacketHeader.HandshakeNonceSize + 1 && receiveBuffer[answerLength - 1] == (isServerEnabled ? DataSessionTokenVersion : SessionTokenVersion),
            $"the server's [{answerLength}]-byte answer ends in [{receiveBuffer[answerLength - 1]}]");

        byte[] serverToken = receiveBuffer.AsSpan(PacketHeader.TypeSize, SessionTokenSize).ToArray();
        bool isUsed = isServerEnabled && handshakePayloadLength == PacketHeader.HandshakeNonceSize + 1 && lastHandshakeByte == DataSessionTokenVersion;

        // The server's data is the type byte and the payload, ending in the first bytes of the peer's nonce only when the token is used.
        server.Send(server.Connections.Connections[0], new(BuildFilledMessage(0x5A, PayloadLength)), isReliable: false);

        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.None, out int dataLength), "the server's data never arrived");
        Assert.Equal(PacketHeader.TypeSize + PayloadLength + (isUsed ? SessionTokenSize : 0), dataLength);

        if (isUsed)
            Assert.True(receiveBuffer.AsSpan(dataLength - SessionTokenSize, SessionTokenSize).SequenceEqual(handshake.AsSpan(PacketHeader.TypeSize, SessionTokenSize)), "the server's data does not end with the session token the peer's handshake opened with");

        // The peer's data without the token is delivered exactly when the token is not used; with the server's token, it is delivered when it is.
        byte[] peerPayload = BuildFilledMessage(0x6B, PayloadLength);
        byte[] untokened = [(byte)PacketType.None, .. peerPayload];

        peer.SendTo(untokened, target);
        TestHarness.PumpFor(100, server);

        Assert.True(serverDeliveries.Count == (isUsed ? 0 : 1), $"the peer's data without the token was [{(serverDeliveries.Count == 0 ? "dropped" : "delivered")}] where the token is [{(isUsed ? "used" : "not used")}]");

        if (isUsed)
            peer.SendTo(BuildTokenedPacket(untokened, serverToken), target);

        Assert.True(TestHarness.PumpUntil(() => serverDeliveries.Count == 1, 2000, server) && serverDeliveries[0].AsSpan().SequenceEqual(peerPayload), "the peer's data did not arrive intact");
    }

    /// <summary>
    /// With data session tokens in use, a data packet too short to carry the token after its header is dropped and counted, even when its
    /// last four bytes, which then overlap the header, equal the receiver's token. It is not read as a packet with a negative payload
    /// length, which from the peer is a violation that ends its session.
    /// </summary>
    [Fact]
    public void DataSessionToken_PacketTooShortForTheToken_IsDroppedEvenWhenItsLastBytesMatch()
    {
        int port = TestHarness.GetFreePort();
        using SynapseManager server = new(TestHarness.ServerConfig(port, EnableDataSessionToken));

        TestHarness.EventRecorder serverEvents = new();
        serverEvents.Attach(server);
        server.Start();

        IPEndPoint target = new(IPAddress.Loopback, port);
        using Socket peer = TestHarness.CreateRawSocket();
        byte[] receiveBuffer = new byte[64];

        peer.SendTo(BuildHandshake(PacketHeader.HandshakeNonceSize + 1, DataSessionTokenVersion), target);
        Assert.True(TryReceiveWhilePumping(peer, receiveBuffer, server, PacketType.Handshake, out _), "the server never answered the handshake");

        long droppedBefore = server.Telemetry.PacketsDroppedIn;

        // The type byte and the server's token: five bytes, two short of a reliable header and the token.
        byte[] tooShort = BuildTokenedPacket([(byte)PacketType.Reliable], receiveBuffer.AsSpan(PacketHeader.TypeSize, SessionTokenSize));

        peer.SendTo(tooShort, target);
        TestHarness.PumpFor(100, server);

        Assert.True(
            serverEvents.Violations == 0 && serverEvents.ConnectionsClosed == 0 && server.Connections.Count == 1,
            $"a [{tooShort.Length}]-byte reliable packet ending in the token raised [{string.Join(", ", serverEvents.ViolationReasons)}] and closed [{serverEvents.ConnectionsClosed}] sessions");
        Assert.Equal(0, serverEvents.PacketsReceived);
        Assert.Equal(droppedBefore + 1, server.Telemetry.PacketsDroppedIn);
    }

    /// <summary>
    /// With data session tokens on at both ends, a reliable message the server sends from its <see cref="SynapseManager.ConnectionClosed"/>
    /// handler while a restarted client's proof replaces the session reaches the new session: like a message sent before a handshake is
    /// answered, it waits for the new session's token rather than carrying the old session's.
    /// </summary>
    [Fact]
    public void DataSessionToken_ReliableMessageSentWhileASessionIsReplaced_ReachesTheNewSession()
    {
        using ConnectedPair pair = new(EnableDataSessionToken, EnableDataSessionToken);
        List<byte[]> clientDeliveries = RecordDeliveries(pair.Client);
        byte[] message = BuildFilledMessage(0x77, 64);

        pair.Server.ConnectionClosed += connectionEventArgs => pair.Server.Send(connectionEventArgs.Connection, new(message), isReliable: true);

        // Past IngressEngine.HandshakeAnswerWindowTicks, the client restarts and connects again; the server challenges it, and its proof replaces the session.
        TestHarness.PumpFor(1200, pair.Server, pair.Client);
        pair.Reconnect();

        Assert.True(pair.ServerEvents.ConnectionsClosed == 1 && pair.ClientConnection.State is ConnectionState.Connected, $"the reconnect left [{pair.ServerEvents.ConnectionsClosed}] closed sessions and the client [{pair.ClientConnection.State}]");
        Assert.True(
            TestHarness.PumpUntil(() => clientDeliveries.Count == 1, 3000, pair.Server, pair.Client) && clientDeliveries[0].AsSpan().SequenceEqual(message),
            "a reliable message sent while the session was replaced never reached the new session");
    }

    /// <summary>
    /// Once the pools are warm, data packets in both directions, unreliable, reliable and split, with forged ones mixed in, allocate nothing
    /// on the thread that sends and polls, whether or not they carry the session token.
    /// </summary>
    /// <param name="isDataSessionTokenEnabled">True to turn data session tokens on at both ends.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DataSessionToken_WarmDataTraffic_AllocatesNothing(bool isDataSessionTokenEnabled)
    {
        const int ExchangeCount = 50;

        int port = TestHarness.GetFreePort();

        // The rate limits are off, since the exchanges are not paced and a refused one would raise a violation.
        void Configure(SynapseConfig synapseConfig)
        {
            synapseConfig.Security.DataSessionTokenEnabled = isDataSessionTokenEnabled;
            synapseConfig.Security.MaximumPacketsPerSecond = SecurityConfig.DisabledMaximumPacketsPerSecond;
            synapseConfig.Security.MaximumBytesPerSecond = SecurityConfig.DisabledMaximumBytesPerSecond;
        }

        using SynapseManager server = new(TestHarness.ServerConfig(port, Configure));
        using SynapseManager client = new(TestHarness.ClientConfig(Configure));

        // A one-element box the handlers count into, so no delivery allocates.
        int[] deliveredCount = [0];
        server.PacketReceived += _ => deliveredCount[0]++;
        client.PacketReceived += _ => deliveredCount[0]++;

        server.Start();
        client.Start();

        IPEndPoint serverEndPoint = new(IPAddress.Loopback, port);
        SynapseConnection clientConnection = client.Connect(serverEndPoint);

        Assert.True(TestHarness.PumpUntil(() => clientConnection.State is ConnectionState.Connected && server.Connections.Count == 1, 3000, server, client), "the client never connected");

        SynapseConnection serverConnection = server.Connections.Connections[0];
        ArraySegment<byte> payload = new(new byte[100]);
        ArraySegment<byte> splitPayload = new(new byte[server.MaximumPayloadSize * 3]);
        ArraySegment<byte> forged = new([(byte)PacketType.None, 1, 2, 3, 4, 5, 6]);
        // Four honest payloads arrive per exchange, and the forged one too where no token is carried.
        int deliveriesPerExchange = isDataSessionTokenEnabled ? 4 : 5;
        long droppedBefore = server.Telemetry.PacketsDroppedIn;

        // The first round grows the pools and the tables; the second is measured.
        for (int round = 0; round < 2; round++)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int exchange = 0; exchange < ExchangeCount; exchange++)
            {
                int expectedCount = deliveredCount[0] + deliveriesPerExchange;

                client.Send(clientConnection, payload, isReliable: false);
                client.Send(clientConnection, splitPayload, isReliable: true);
                server.Send(serverConnection, payload, isReliable: true);
                server.Send(serverConnection, splitPayload, isReliable: false);
                client.SendRaw(serverEndPoint, forged);

                long deadlineMilliseconds = Environment.TickCount64 + 2000;

                while (deliveredCount[0] < expectedCount && Environment.TickCount64 < deadlineMilliseconds)
                {
                    server.Poll();
                    client.Poll();
                }
            }

            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.Equal((round + 1) * ExchangeCount * deliveriesPerExchange, deliveredCount[0]);

            if (round == 1)
                Assert.Equal(0, allocatedBytes);
        }

        Assert.Equal(droppedBefore + (isDataSessionTokenEnabled ? 2 * ExchangeCount : 0), server.Telemetry.PacketsDroppedIn);
    }

    /// <summary>
    /// Builds a wire-format handshake datagram: the type byte and a random payload, by default the 8-byte nonce of a peer that predates
    /// session tokens. A payload one byte longer than a nonce or a challenge ends in <paramref name="version"/> instead of a random byte.
    /// </summary>
    /// <param name="payloadLength">The payload's length.</param>
    /// <param name="version">The protocol version byte a versioned payload ends with.</param>
    /// <returns>The datagram.</returns>
    private static byte[] BuildHandshake(int payloadLength = PacketHeader.HandshakeNonceSize, byte version = SessionTokenVersion)
    {
        byte[] datagram = new byte[PacketHeader.TypeSize + payloadLength];
        datagram[0] = (byte)PacketType.Handshake;
        RandomNumberGenerator.Fill(datagram.AsSpan(PacketHeader.TypeSize));

        if (payloadLength is PacketHeader.HandshakeNonceSize + 1 or PacketHeader.HandshakeChallengeSize + 1)
            datagram[^1] = version;

        return datagram;
    }

    /// <summary>
    /// Builds a wire-format segment of <paramref name="packetType"/> carrying <paramref name="payloadLength"/> zero bytes, under reliable
    /// sequence 0 when the type carries one.
    /// </summary>
    /// <param name="packetType"><see cref="PacketType.Segmented"/> or <see cref="PacketType.ReliableSegmented"/>.</param>
    /// <param name="segmentId">The message's segment id.</param>
    /// <param name="segmentIndex">The segment's index.</param>
    /// <param name="segmentCount">The segments the message declares.</param>
    /// <param name="payloadLength">The bytes after the header.</param>
    /// <returns>The datagram.</returns>
    private static byte[] BuildSegment(PacketType packetType, ushort segmentId, byte segmentIndex, byte segmentCount, int payloadLength)
    {
        byte[] datagram = new byte[PacketHeader.ComputeHeaderSize(packetType) + payloadLength];
        PacketHeader.Write(datagram, packetType, sequence: 0, segmentId, segmentIndex, segmentCount);

        return datagram;
    }

    /// <summary>
    /// Turns <see cref="SecurityConfig.DataSessionTokenEnabled"/> on.
    /// </summary>
    /// <param name="synapseConfig">The configuration to change.</param>
    private static void EnableDataSessionToken(SynapseConfig synapseConfig) => synapseConfig.Security.DataSessionTokenEnabled = true;

    /// <summary>
    /// Records a copy of every payload <paramref name="synapseManager"/> delivers, in arrival order.
    /// </summary>
    /// <param name="synapseManager">The engine whose deliveries are recorded.</param>
    /// <returns>The delivered payloads.</returns>
    private static List<byte[]> RecordDeliveries(SynapseManager synapseManager)
    {
        List<byte[]> deliveredPayloads = [];
        synapseManager.PacketReceived += packetReceivedEventArgs => deliveredPayloads.Add(packetReceivedEventArgs.Payload.ToArray());

        return deliveredPayloads;
    }

    /// <summary>
    /// Builds a packet followed by a session token.
    /// </summary>
    /// <param name="packet">The packet without its token.</param>
    /// <param name="sessionToken">The token to append.</param>
    /// <returns>The datagram.</returns>
    private static byte[] BuildTokenedPacket(byte[] packet, ReadOnlySpan<byte> sessionToken)
    {
        byte[] datagram = new byte[packet.Length + SessionTokenSize];
        packet.CopyTo(datagram, 0);
        sessionToken[..SessionTokenSize].CopyTo(datagram.AsSpan(packet.Length));

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
    /// Polls <paramref name="engine"/> until a datagram of <paramref name="packetType"/> arrives on <paramref name="socket"/>, discarding
    /// any other, for up to two seconds.
    /// </summary>
    /// <param name="socket">The socket to read.</param>
    /// <param name="buffer">Receives the datagram.</param>
    /// <param name="engine">The engine to poll while waiting.</param>
    /// <param name="packetType">The type of datagram awaited.</param>
    /// <param name="receivedLength">The datagram's length.</param>
    /// <returns>False when none arrived in time.</returns>
    private static bool TryReceiveWhilePumping(Socket socket, byte[] buffer, SynapseManager engine, PacketType packetType, out int receivedLength)
    {
        long deadlineMilliseconds = Environment.TickCount64 + 2000;

        while (Environment.TickCount64 < deadlineMilliseconds)
        {
            engine.Poll();

            while (TryReceive(socket, buffer, out receivedLength))
            {
                if (receivedLength > 0 && buffer[0] == (byte)packetType)
                    return true;
            }

            Thread.Sleep(1);
        }

        receivedLength = 0;

        return false;
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
    /// Reads every datagram queued on <paramref name="socket"/> and collects the sequences its acknowledgements name.
    /// </summary>
    /// <param name="socket">The socket the acknowledgements arrive on.</param>
    /// <param name="buffer">Scratch buffer the datagrams are read into.</param>
    /// <returns>The acknowledged sequences.</returns>
    private static HashSet<ushort> ReceiveAckedSequences(Socket socket, byte[] buffer)
    {
        HashSet<ushort> ackedSequences = [];

        while (TryReceive(socket, buffer, out int receivedLength))
        {
            if (receivedLength == 0 || buffer[0] != (byte)PacketType.Ack)
                continue;

            for (int offset = PacketHeader.TypeSize; offset + PacketHeader.SequenceSize <= receivedLength; offset += PacketHeader.SequenceSize)
                ackedSequences.Add((ushort)(buffer[offset] | (buffer[offset + 1] << 8)));
        }

        return ackedSequences;
    }

    /// <summary>
    /// Counts the payload bytes <paramref name="synapseConnection"/>'s assemblies hold under segment ids 1 to
    /// <paramref name="lastSegmentId"/>, from the segments each reports holding.
    /// </summary>
    /// <param name="synapseConnection">The connection whose assemblies are measured.</param>
    /// <param name="lastSegmentId">The last segment id measured.</param>
    /// <param name="segmentPayloadLength">The payload every held segment carries.</param>
    /// <returns>The payload bytes held.</returns>
    private static long MeasureAssemblyBytes(SynapseConnection synapseConnection, ushort lastSegmentId, int segmentPayloadLength)
    {
        long assemblyBytes = 0;
        byte[] receivedBitmap = new byte[PacketHeader.SegmentAckBitmapSize];

        for (ushort segmentId = 1; segmentId <= lastSegmentId; segmentId++)
        {
            if (synapseConnection.Reassembler is null || !synapseConnection.Reassembler.TryWriteReceivedBitmap(segmentId, new(receivedBitmap), out _))
                continue;

            foreach (byte bitmapByte in receivedBitmap)
                assemblyBytes += BitOperations.PopCount(bitmapByte) * (long)segmentPayloadLength;
        }

        return assemblyBytes;
    }

    /// <summary>
    /// Records every violation <paramref name="synapseManager"/> raises, leaving each one's action as the engine settled it.
    /// </summary>
    /// <param name="synapseManager">The engine whose violations are recorded.</param>
    /// <returns>The violations, in the order they were raised.</returns>
    private static List<ViolationEventArgs> RecordViolations(SynapseManager synapseManager)
    {
        List<ViolationEventArgs> violations = [];

        synapseManager.ViolationDetected += (ref ViolationEventArgs violationEventArgs) =>
        {
            violations.Add(violationEventArgs);

            return violationEventArgs.Action;
        };

        return violations;
    }

    /// <summary>
    /// Asserts <paramref name="violations"/> holds exactly <paramref name="expectedCount"/> reports, each a drop past the held-byte budget
    /// reported as the per-second rate limit reports one: <see cref="ViolationReason.RateLimitExceeded"/>, settled as
    /// <see cref="ViolationAction.Drop"/>, with no log entry.
    /// </summary>
    /// <param name="violations">The violations raised.</param>
    /// <param name="expectedCount">How many datagrams were dropped past the budget.</param>
    private static void AssertReportedAsRateLimited(List<ViolationEventArgs> violations, int expectedCount)
    {
        Assert.True(violations.Count == expectedCount, $"[{violations.Count}] violations [{string.Join(", ", violations.Select(violationEventArgs => violationEventArgs.Reason))}] where [{expectedCount}] datagrams were dropped past the budget");

        foreach (ViolationEventArgs violationEventArgs in violations)
            Assert.True(violationEventArgs is { Reason: ViolationReason.RateLimitExceeded, Action: ViolationAction.Drop, Log.IsSet: false }, $"a drop past the budget was reported as [{violationEventArgs.Reason}] settled as [{violationEventArgs.Action}]");
    }

    /// <summary>
    /// Builds a message of <paramref name="length"/> bytes, every one of them <paramref name="fill"/>, so it is told apart from any
    /// other message on arrival.
    /// </summary>
    /// <param name="fill">The byte the message is filled with.</param>
    /// <param name="length">The message's length.</param>
    /// <returns>The message.</returns>
    private static byte[] BuildFilledMessage(byte fill, int length)
    {
        byte[] message = new byte[length];
        Array.Fill(message, fill);

        return message;
    }

    /// <summary>
    /// How a receiver's per-connection limits are turned off.
    /// </summary>
    public enum ReceiverLimits : byte
    {
        /// <summary>
        /// The packet and byte rate limits are set to 0, as a client sets them against its server.
        /// </summary>
        RateLimitsDisabled,
        /// <summary>
        /// Security is disabled, which skips every per-connection limit.
        /// </summary>
        SecurityDisabled,
        /// <summary>
        /// The connection is marked exempt from the rate limits.
        /// </summary>
        ConnectionExempt
    }

    /// <summary>
    /// A segment that is a violation when its sender's session sends it.
    /// </summary>
    public enum MalformedSegment : byte
    {
        /// <summary>
        /// An unreliable segment carrying one byte more than the transmission unit.
        /// </summary>
        UnreliableLargerThanTransmissionUnit,
        /// <summary>
        /// A reliable segment, not the last of its message, carrying one byte more than the transmission unit.
        /// </summary>
        ReliableLargerThanTransmissionUnit,
        /// <summary>
        /// Two unreliable segments of one message, the second declaring a different segment count than the first.
        /// </summary>
        ContradictingSegmentCount,
        /// <summary>
        /// A reliable segment declaring 255 segments, which at the transmission unit is more than the server's
        /// <see cref="SecurityConfig.MaximumReassembledPacketSize"/> allows.
        /// </summary>
        DeclaredAssemblyOversized
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
        /// <param name="tweakServerConfig">Adjusts the server's configuration before it starts, or null to keep the defaults.</param>
        /// <param name="tweakClientConfig">Adjusts the victim's configuration before it starts, or null to keep the defaults.</param>
        public ConnectedPair(Action<SynapseConfig>? tweakServerConfig = null, Action<SynapseConfig>? tweakClientConfig = null)
        {
            int port = TestHarness.GetFreePort();

            Server = new(TestHarness.ServerConfig(port, tweakServerConfig));
            Client = new(TestHarness.ClientConfig(tweakClientConfig));
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
    /// Sends hand-built datagrams from a raw socket, paced under the server's per-connection rate limits so that none is shed, and polls
    /// the server as it goes so its socket buffer never overflows.
    /// </summary>
    private sealed class PacedSender
    {
        /// <summary>
        /// The socket the datagrams leave from.
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
        /// One datagram of up to the server's transmission unit, rewritten for each send.
        /// </summary>
        private readonly byte[] _datagram;
        /// <summary>
        /// Datagrams sent per window, kept under the server's packet and byte allowance a second.
        /// </summary>
        private readonly int _datagramsPerWindow;
        /// <summary>
        /// Datagrams sent in the current window.
        /// </summary>
        private int _windowDatagramCount;
        /// <summary>
        /// When the current window opened, in <see cref="Environment.TickCount64"/> milliseconds.
        /// </summary>
        private long _windowStartMilliseconds = Environment.TickCount64;
        /// <summary>
        /// Datagrams sent per window at the default limits: 1.8 MB of full-size segments, under both the 2000 packet and the 2 MiB byte
        /// allowance a second.
        /// </summary>
        public const int DefaultDatagramsPerWindow = 1500;
        /// <summary>
        /// How long a window lasts. The server's rate window is a second measured between its own polls, so a window any shorter can
        /// leave one of the server's holding the tail of one burst and the head of the next.
        /// </summary>
        private const long WindowMilliseconds = 1100;

        /// <summary>
        /// Creates a sender of datagrams up to the server's transmission unit.
        /// </summary>
        /// <param name="socket">The socket the datagrams leave from.</param>
        /// <param name="target">The server's address.</param>
        /// <param name="server">The server, polled between bursts.</param>
        /// <param name="datagramsPerWindow">How many datagrams to send per window, kept under the server's allowance a second.</param>
        public PacedSender(Socket socket, IPEndPoint target, SynapseManager server, int datagramsPerWindow = DefaultDatagramsPerWindow)
        {
            _socket = socket;
            _target = target;
            _server = server;
            _datagram = new byte[server.MaximumTransmissionUnit];
            _datagramsPerWindow = datagramsPerWindow;
        }

        /// <summary>
        /// Sends the first <paramref name="sentSegmentCount"/> full-size segments of a reliable message declaring
        /// <paramref name="segmentCount"/>, using the sequence as its segment id.
        /// </summary>
        /// <param name="sequence">The message's reliable sequence and segment id.</param>
        /// <param name="segmentCount">The segments the message declares.</param>
        /// <param name="sentSegmentCount">How many of them to send, from index 0.</param>
        public void SendMessage(ushort sequence, byte segmentCount, int sentSegmentCount)
        {
            int segmentPayloadLength = _datagram.Length - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented);

            for (int segmentIndex = 0; segmentIndex < sentSegmentCount; segmentIndex++)
                Send(PacketType.ReliableSegmented, sequence, segmentId: sequence, (byte)segmentIndex, segmentCount, segmentPayloadLength);

            _server.Poll();
        }

        /// <summary>
        /// Sends one datagram of <paramref name="packetType"/> carrying <paramref name="payloadLength"/> zero bytes, opening the next window
        /// first when the current one is full.
        /// </summary>
        /// <param name="packetType">The datagram's packet type.</param>
        /// <param name="sequence">The reliable sequence, written when the type carries one.</param>
        /// <param name="segmentId">The segment id, written when the type carries one.</param>
        /// <param name="segmentIndex">The segment index, written when the type carries one.</param>
        /// <param name="segmentCount">The segment count, written when the type carries one.</param>
        /// <param name="payloadLength">The bytes after the header.</param>
        public void Send(PacketType packetType, ushort sequence, ushort segmentId, byte segmentIndex, byte segmentCount, int payloadLength)
        {
            if (_windowDatagramCount >= _datagramsPerWindow)
                StartNextWindow();

            int headerSize = PacketHeader.Write(_datagram, packetType, sequence, segmentId, segmentIndex, segmentCount);
            _socket.SendTo(_datagram, 0, headerSize + payloadLength, SocketFlags.None, _target);
            _windowDatagramCount++;

            if (_windowDatagramCount % 64 == 0)
                _server.Poll();
        }

        /// <summary>
        /// Waits out the rest of the current window, polling so nothing waits in the socket buffer, and opens the next.
        /// </summary>
        public void StartNextWindow()
        {
            while (Environment.TickCount64 - _windowStartMilliseconds < WindowMilliseconds)
            {
                _server.Poll();
                Thread.Sleep(1);
            }

            _windowStartMilliseconds = Environment.TickCount64;
            _windowDatagramCount = 0;
        }
    }

    /// <summary>
    /// A pass-through transform for a sender that loses the gap message, segmented or not, while armed, standing in for a message lost on
    /// the network for as long as a test needs the backlog behind it to build.
    /// </summary>
    private sealed class GapTransform : IPacketTransform
    {
        /// <summary>
        /// True while the gap message's segments are lost.
        /// </summary>
        public bool IsDroppingGapMessage;
        /// <inheritdoc/>
        public uint ReservedBytes => 0;
        /// <summary>
        /// The byte the gap message is filled with, which no other message in these tests carries.
        /// </summary>
        public const byte GapMessageFill = 0xEE;

        /// <inheritdoc/>
        public bool TryTransform(PacketTransformDirection packetTransformDirection, PacketType packetType, IPEndPoint endPoint, ReadOnlySpan<byte> source, Span<byte> destination, out int writtenLength)
        {
            writtenLength = source.Length;

            if (IsDroppingGapMessage && packetTransformDirection == PacketTransformDirection.Outbound && packetType is PacketType.Reliable or PacketType.ReliableSegmented && source.Length > 0 && source[0] == GapMessageFill)
                return false;

            source.CopyTo(destination);

            return true;
        }
    }

    /// <summary>
    /// A pass-through transform for a receiver that, while armed, loses every acknowledgement it sends and the first segment
    /// acknowledgement, standing in for a network that keeps losing the receiver's answers.
    /// </summary>
    private sealed class AcknowledgementDroppingTransform : IPacketTransform
    {
        /// <summary>
        /// True while acknowledgements are lost.
        /// </summary>
        public bool IsDroppingAcknowledgements;
        /// <summary>
        /// The acknowledgements lost so far.
        /// </summary>
        public int DroppedAckCount;
        /// <summary>
        /// The segment acknowledgements lost so far, which is never more than one.
        /// </summary>
        public int DroppedSegmentAckCount;
        /// <inheritdoc/>
        public uint ReservedBytes => 0;

        /// <inheritdoc/>
        public bool TryTransform(PacketTransformDirection packetTransformDirection, PacketType packetType, IPEndPoint endPoint, ReadOnlySpan<byte> source, Span<byte> destination, out int writtenLength)
        {
            writtenLength = source.Length;

            if (IsDroppingAcknowledgements && packetTransformDirection == PacketTransformDirection.Outbound)
            {
                if (packetType == PacketType.Ack)
                {
                    DroppedAckCount++;

                    return false;
                }

                if (packetType == PacketType.SegmentAck && DroppedSegmentAckCount == 0)
                {
                    DroppedSegmentAckCount++;

                    return false;
                }
            }

            source.CopyTo(destination);

            return true;
        }
    }

    /// <summary>
    /// A pass-through transform that records what its engine sends: the protocol version each handshake ends with, the length and the last
    /// four bytes of each data packet, and the largest datagram of any kind.
    /// </summary>
    private sealed class RecordingTransform : IPacketTransform
    {
        /// <summary>
        /// The last byte of each handshake sent, which is its protocol version.
        /// </summary>
        public readonly List<byte> HandshakeVersions = [];
        /// <summary>
        /// The length of each data packet sent, header included.
        /// </summary>
        public readonly List<int> DataPacketLengths = [];
        /// <summary>
        /// The last four bytes of each data packet sent, read as a little-endian session token.
        /// </summary>
        public readonly List<uint> DataPacketTrailers = [];
        /// <summary>
        /// The length of the largest datagram sent, header included.
        /// </summary>
        public int LargestDatagramLength;
        /// <inheritdoc/>
        public uint ReservedBytes => 0;

        /// <inheritdoc/>
        public bool TryTransform(PacketTransformDirection packetTransformDirection, PacketType packetType, IPEndPoint endPoint, ReadOnlySpan<byte> source, Span<byte> destination, out int writtenLength)
        {
            source.CopyTo(destination);
            writtenLength = source.Length;

            if (packetTransformDirection != PacketTransformDirection.Outbound)
                return true;

            int datagramLength = PacketHeader.ComputeHeaderSize(packetType) + source.Length;
            LargestDatagramLength = Math.Max(LargestDatagramLength, datagramLength);

            if (packetType == PacketType.Handshake)
            {
                HandshakeVersions.Add(source[^1]);
            }
            else if (packetType is PacketType.None or PacketType.Reliable or PacketType.Segmented or PacketType.ReliableSegmented)
            {
                DataPacketLengths.Add(datagramLength);

                if (source.Length >= SessionTokenSize)
                    DataPacketTrailers.Add(BinaryPrimitives.ReadUInt32LittleEndian(source[^SessionTokenSize..]));
            }

            return true;
        }
    }
}
