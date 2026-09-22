using System;
using System.Net;
using System.Net.Sockets;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Packets;
using Xunit;

namespace SynapseSocket.Tests.Transport;

/// <summary>
/// Live coverage of the full-cone NAT hole-punch exchange, driven over real loopback UDP sockets.
/// <para>
/// The peer on the other side of each punch is an ordinary <see cref="Socket"/> sending hand-built datagrams,
/// which is what lets these tests assert the wire exchange itself rather than the engine's opinion of it: the
/// probe, the challenge that answers it, the echo that carries the token back, and the handshake that echo is
/// supposed to draw. An engine-to-engine test cannot distinguish that sequence completing from the handshake
/// bundled into every probe burst arriving first, and it is the sequence that was never covered.
/// </para>
/// </summary>
public sealed class NatTraversalTests
{
    /// <summary>
    /// Bytes of the truncated HMAC token a challenge carries.
    /// </summary>
    private const int NatTokenSize = 8;

    /// <summary>
    /// Milliseconds allowed for a datagram the engine owes us to arrive.
    /// </summary>
    private const int ReplyTimeoutMilliseconds = 2000;

    /// <summary>
    /// Builds a config with full-cone traversal engaged on an ephemeral loopback port.
    /// </summary>
    private static SynapseConfig NatConfig(Action<SynapseConfig>? tweak = null)
    {
        return TestHarness.ClientConfig(config =>
        {
            config.NatTraversal.Mode = NatTraversalMode.FullCone;
            tweak?.Invoke(config);
        });
    }

    /// <summary>
    /// Receives one datagram from <paramref name="socket"/>, pumping <paramref name="engine"/> so the poll-driven
    /// engine can actually produce it. Returns null when nothing arrives within
    /// <see cref="ReplyTimeoutMilliseconds"/>.
    /// </summary>
    /// <param name="socket">Socket awaiting the engine's reply.</param>
    /// <param name="engine">Engine to poll while waiting.</param>
    private static byte[]? ReceiveWhilePumping(Socket socket, SynapseManager engine)
    {
        byte[] buffer = new byte[64];
        long deadline = Environment.TickCount64 + ReplyTimeoutMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            engine.Poll();

            if (socket.Available > 0)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int received = socket.ReceiveFrom(buffer, ref from);
                return buffer.AsSpan(0, received).ToArray();
            }

            System.Threading.Thread.Sleep(1);
        }

        return null;
    }

    /// <summary>
    /// A probe from an unknown endpoint must be answered with a challenge rather than a handshake: answering with
    /// a handshake would send it to an address that has not proven it can receive, which is the amplification
    /// shape the challenge exists to close.
    /// </summary>
    [Fact]
    public void NatProbe_FromUnknownEndpoint_IsAnsweredWithAChallengeNotAHandshake()
    {
        using SynapseManager engine = new(NatConfig());
        engine.Start();

        using Socket peer = TestHarness.CreateRawSocket();
        IPEndPoint engineEndPoint = engine.BoundEndPoints[0];

        peer.SendTo([(byte)PacketType.NatProbe], engineEndPoint);

        byte[]? reply = ReceiveWhilePumping(peer, engine);

        Assert.True(reply is not null, "a NAT probe drew no reply at all");
        Assert.Equal((byte)PacketType.NatChallenge, reply![0]);
        Assert.Equal(1 + NatTokenSize, reply.Length);
    }

    /// <summary>
    /// The exchange the punch depends on, end to end: probe, challenge, echo the token back, handshake. The echo
    /// is what proves the peer received the challenge at the address it claimed, so it is what the engine may
    /// safely answer with a handshake.
    /// </summary>
    /// <remarks>
    /// This is the case an engine-to-engine test cannot isolate. Every probe burst also carries a handshake, so a
    /// connection still forms when this exchange is broken, and the punch looks healthy from the outside while
    /// the token round trip contributes nothing.
    /// </remarks>
    [Fact]
    public void NatChallengeEcho_CarryingTheIssuedToken_DrawsAHandshake()
    {
        using SynapseManager engine = new(NatConfig());
        engine.Start();

        using Socket peer = TestHarness.CreateRawSocket();
        IPEndPoint engineEndPoint = engine.BoundEndPoints[0];

        peer.SendTo([(byte)PacketType.NatProbe], engineEndPoint);

        byte[]? challenge = ReceiveWhilePumping(peer, engine);
        Assert.True(challenge is not null, "a NAT probe drew no challenge");
        Assert.Equal((byte)PacketType.NatChallenge, challenge![0]);

        /* Echo the token straight back, stamped as an echo exactly as a peer engine would. The engine minted this
         * token for this address, so it verifies, and a verified token is the peer proving return-routability. */
        byte[] echo = new byte[1 + NatTokenSize + 1];
        echo[0] = (byte)PacketType.NatChallenge;
        challenge.AsSpan(1, NatTokenSize).CopyTo(echo.AsSpan(1));
        echo[1 + NatTokenSize] = 1;

        peer.SendTo(echo, engineEndPoint);

        byte[]? handshake = ReceiveWhilePumping(peer, engine);

        Assert.True(handshake is not null, "the echoed challenge drew no reply, so the token round trip never completes");
        Assert.Equal((byte)PacketType.Handshake, handshake![0]);
    }

    /// <summary>
    /// A challenge carrying a token this engine never issued must not draw a handshake. It is answered with a
    /// first-hand echo instead, which is the initiator side of a simultaneous punch.
    /// </summary>
    [Fact]
    public void NatChallenge_CarryingAForgedToken_DrawsAnEchoNotAHandshake()
    {
        using SynapseManager engine = new(NatConfig());
        engine.Start();

        using Socket peer = TestHarness.CreateRawSocket();
        IPEndPoint engineEndPoint = engine.BoundEndPoints[0];

        byte[] forged = new byte[1 + NatTokenSize];
        forged[0] = (byte)PacketType.NatChallenge;
        // Deliberately not a token this engine minted.
        forged.AsSpan(1).Fill(0xAB);

        peer.SendTo(forged, engineEndPoint);

        byte[]? reply = ReceiveWhilePumping(peer, engine);

        Assert.True(reply is not null, "an unrecognised challenge drew no reply, so a simultaneous punch cannot start");
        Assert.Equal((byte)PacketType.NatChallenge, reply![0]);
        Assert.Equal(1 + NatTokenSize + 1, reply.Length);
        Assert.Equal(1, reply[1 + NatTokenSize]);
    }

    /// <summary>
    /// An echo that is already stamped must never draw another echo. Two engines that cannot verify each other's
    /// tokens would otherwise bounce the same bytes forever, and one forged datagram naming two victims would
    /// start exactly that between them.
    /// </summary>
    [Fact]
    public void NatChallenge_AlreadyEchoed_IsNotEchoedAgain()
    {
        using SynapseManager engine = new(NatConfig());
        engine.Start();

        using Socket peer = TestHarness.CreateRawSocket();
        IPEndPoint engineEndPoint = engine.BoundEndPoints[0];

        byte[] stampedForgery = new byte[1 + NatTokenSize + 1];
        stampedForgery[0] = (byte)PacketType.NatChallenge;
        stampedForgery.AsSpan(1, NatTokenSize).Fill(0xCD);
        stampedForgery[1 + NatTokenSize] = 1;

        peer.SendTo(stampedForgery, engineEndPoint);

        byte[]? reply = ReceiveWhilePumping(peer, engine);

        Assert.True(reply is null, $"an already-echoed challenge drew a reply of type [{reply?[0]}], so two engines can bounce one token forever");
    }

    /// <summary>
    /// Two engines punching at each other simultaneously, which is what a rendezvous service sets up once it has
    /// told each peer the other's external endpoint. Both sides must end up connected.
    /// </summary>
    [Fact]
    public void SimultaneousPunch_BetweenTwoEngines_ConnectsBothSides()
    {
        using SynapseManager first = new(NatConfig());
        using SynapseManager second = new(NatConfig());

        TestHarness.EventRecorder firstEvents = new();
        TestHarness.EventRecorder secondEvents = new();
        firstEvents.Attach(first);
        secondEvents.Attach(second);

        first.Start();
        second.Start();

        first.Connect(second.BoundEndPoints[0]);
        second.Connect(first.BoundEndPoints[0]);

        Assert.True(
            TestHarness.PumpUntil(() => firstEvents.ConnectionsEstablished == 1 && secondEvents.ConnectionsEstablished == 1, 5000, first, second),
            $"a simultaneous punch left the sides at [{firstEvents.ConnectionsEstablished}] and [{secondEvents.ConnectionsEstablished}] connections");
    }

    /// <summary>
    /// Probes from one address must not draw an unbounded stream of challenges: an attacker spoofing a victim's
    /// address as the source would otherwise aim that stream at the victim.
    /// </summary>
    [Fact]
    public void NatProbes_Flooded_AreRateLimitedPerSourceAddress()
    {
        const int ProbeCount = 20;

        using SynapseManager engine = new(NatConfig(config => config.NatTraversal.IntervalMilliseconds = 500));
        engine.Start();

        using Socket peer = TestHarness.CreateRawSocket();
        IPEndPoint engineEndPoint = engine.BoundEndPoints[0];

        for (int i = 0; i < ProbeCount; i++)
            peer.SendTo([(byte)PacketType.NatProbe], engineEndPoint);

        TestHarness.PumpFor(300, engine);

        int replies = 0;
        byte[] buffer = new byte[64];

        while (peer.Available > 0)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            peer.ReceiveFrom(buffer, ref from);
            replies++;
        }

        Assert.True(replies < ProbeCount, $"every one of [{ProbeCount}] probes drew a reply, so the per-address rate limit is not engaged");
    }
}
