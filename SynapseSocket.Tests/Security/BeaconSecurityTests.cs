using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SynapseBeacon.Server;
using SynapseBeacon.Wire;
using Xunit;

namespace SynapseSocket.Tests.Security;

/// <summary>
/// Live coverage of the beacon server's admission rules, driven by raw sockets speaking the wire protocol.
/// <para>
/// The interesting property is not what a well-behaved joiner gets, but what an address that never proves it can
/// receive gets: nothing that discloses an endpoint, and nothing that makes a host aim a hole-punch burst
/// somewhere. That is asserted here by checking the host socket stays silent, which is the difference between a
/// rendezvous service and a packet reflector.
/// </para>
/// </summary>
public sealed class BeaconSecurityTests : IDisposable
{
    /// <summary>
    /// Milliseconds allowed for a reply the server owes us.
    /// </summary>
    private const int ReplyTimeoutMilliseconds = 1500;

    /// <summary>
    /// Milliseconds waited when asserting that no reply arrives at all.
    /// </summary>
    private const int SilenceWindowMilliseconds = 500;

    /// <summary>
    /// Stops the server's receive loop on disposal.
    /// </summary>
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// The server under test.
    /// </summary>
    private readonly BeaconServer _server;

    /// <summary>
    /// Endpoint the server listens on.
    /// </summary>
    private readonly IPEndPoint _serverEndPoint;

    /// <summary>
    /// Starts a beacon server on a free port with its receive loop running on a background task.
    /// </summary>
    public BeaconSecurityTests()
    {
        int port = TestHarness.GetFreePort();
        _server = new(port, BeaconServer.DefaultSessionTimeoutMilliseconds, BeaconServer.UnlimitedConcurrentSessions, null);
        _serverEndPoint = new(IPAddress.Loopback, port);
        _ = Task.Run(() => _server.RunAsync(_cancellationTokenSource.Token));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _server.Dispose();
        _cancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Receives one datagram, or returns null when nothing arrives within <paramref name="timeoutMilliseconds"/>.
    /// </summary>
    private static byte[]? Receive(Socket socket, int timeoutMilliseconds)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (socket.Available > 0)
            {
                byte[] buffer = new byte[128];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int received = socket.ReceiveFrom(buffer, ref from);

                return buffer.AsSpan(0, received).ToArray();
            }

            Thread.Sleep(5);
        }

        return null;
    }

    /// <summary>
    /// Builds a first-hand join: type, session ID and an 8-byte nonce, with no cookie.
    /// </summary>
    private static byte[] BuildUnprovenJoin(uint sessionId, byte nonceFill)
    {
        byte[] datagram = new byte[BeaconWireFormat.UnprovenJoinBytes];
        BeaconWireFormat.WriteTypeAndSessionId(datagram, BeaconPacketType.JoinSession, sessionId);
        datagram.AsSpan(1 + BeaconWireFormat.SessionIdBytes).Fill(nonceFill);
        return datagram;
    }

    /// <summary>
    /// Registers a host session through the wire protocol and returns its ID with the host's socket.
    /// </summary>
    private (Socket host, uint sessionId) CreateHostSession()
    {
        Socket host = TestHarness.CreateRawSocket();

        byte[] request = new byte[1 + BeaconWireFormat.NonceBytes];
        request[0] = (byte)BeaconPacketType.RequestSession;
        request.AsSpan(1).Fill(0x11);
        host.SendTo(request, _serverEndPoint);

        byte[]? created = Receive(host, ReplyTimeoutMilliseconds);
        Assert.True(created is not null, "the server never answered RequestSession");
        Assert.Equal((byte)BeaconPacketType.SessionCreated, created![0]);

        Assert.True(BeaconWireFormat.TryReadSessionId(created.AsSpan(1), out uint sessionId));

        return (host, sessionId);
    }

    /// <summary>
    /// A: an unproven join is answered with a challenge, and crucially the host hears nothing. Until the joiner
    /// proves it receives at the address it claimed, a forged join cannot make the host punch at that address.
    /// </summary>
    [Fact]
    public void UnprovenJoin_DrawsAChallengeAndLeavesTheHostUntouched()
    {
        (Socket host, uint sessionId) = CreateHostSession();
        using Socket hostSocket = host;
        using Socket joiner = TestHarness.CreateRawSocket();

        joiner.SendTo(BuildUnprovenJoin(sessionId, 0x22), _serverEndPoint);

        byte[]? challenge = Receive(joiner, ReplyTimeoutMilliseconds);
        Assert.True(challenge is not null, "a join drew no reply at all");
        Assert.Equal((byte)BeaconPacketType.JoinChallenge, challenge![0]);

        byte[]? hostTraffic = Receive(hostSocket, SilenceWindowMilliseconds);
        Assert.True(hostTraffic is null, $"an unproven join reached the host as type [{hostTraffic?[0]}], so a forged join can still aim a punch at a third party");
    }

    /// <summary>
    /// A: the same challenge, answered. The cookie completes the join and both sides are matched, so the extra
    /// round trip closes the reflection vector without closing the feature.
    /// </summary>
    [Fact]
    public void ProvenJoin_CompletesTheMatchForBothSides()
    {
        (Socket host, uint sessionId) = CreateHostSession();
        using Socket hostSocket = host;
        using Socket joiner = TestHarness.CreateRawSocket();

        joiner.SendTo(BuildUnprovenJoin(sessionId, 0x22), _serverEndPoint);

        byte[]? challenge = Receive(joiner, ReplyTimeoutMilliseconds);
        Assert.True(challenge is not null, "a join drew no challenge");

        byte[] provenJoin = new byte[BeaconWireFormat.ProvenJoinBytes];
        BeaconWireFormat.WriteTypeAndSessionId(provenJoin, BeaconPacketType.JoinSession, sessionId);
        provenJoin.AsSpan(1 + BeaconWireFormat.SessionIdBytes, BeaconWireFormat.NonceBytes).Fill(0x22);
        challenge.AsSpan(1, BeaconWireFormat.CookieBytes).CopyTo(provenJoin.AsSpan(BeaconWireFormat.UnprovenJoinBytes));

        joiner.SendTo(provenJoin, _serverEndPoint);

        byte[]? joinerReply = Receive(joiner, ReplyTimeoutMilliseconds);
        Assert.True(joinerReply is not null, "a proven join drew no reply");
        Assert.Equal((byte)BeaconPacketType.PeerReady, joinerReply![0]);

        byte[]? hostReply = Receive(hostSocket, ReplyTimeoutMilliseconds);
        Assert.True(hostReply is not null, "the host was never told about a proven joiner");
        Assert.Equal((byte)BeaconPacketType.PeerReady, hostReply![0]);
    }

    /// <summary>
    /// A: a join carrying a cookie the server never issued is dropped outright. It must not be treated as a first
    /// hand join and re-challenged either, or a forgery simply loops the server.
    /// </summary>
    [Fact]
    public void JoinWithAForgedCookie_IsDroppedSilently()
    {
        (Socket host, uint sessionId) = CreateHostSession();
        using Socket hostSocket = host;
        using Socket joiner = TestHarness.CreateRawSocket();

        byte[] forged = new byte[BeaconWireFormat.ProvenJoinBytes];
        BeaconWireFormat.WriteTypeAndSessionId(forged, BeaconPacketType.JoinSession, sessionId);
        forged.AsSpan(1 + BeaconWireFormat.SessionIdBytes, BeaconWireFormat.NonceBytes).Fill(0x22);
        forged.AsSpan(BeaconWireFormat.UnprovenJoinBytes).Fill(0xEE);

        joiner.SendTo(forged, _serverEndPoint);

        Assert.True(Receive(joiner, SilenceWindowMilliseconds) is null, "a forged cookie drew a reply");
        Assert.True(Receive(hostSocket, SilenceWindowMilliseconds) is null, "a forged cookie reached the host");
    }

    /// <summary>
    /// B: a heartbeat naming someone else's session must not be acknowledged. The registry always ignored the
    /// refresh; the acknowledgement did not, which left the server answering any address that named a number.
    /// </summary>
    [Fact]
    public void HeartbeatFromANonHost_IsNotAcknowledged()
    {
        (Socket host, uint sessionId) = CreateHostSession();
        using Socket hostSocket = host;
        using Socket stranger = TestHarness.CreateRawSocket();

        byte[] heartbeat = new byte[1 + BeaconWireFormat.SessionIdBytes];
        BeaconWireFormat.WriteTypeAndSessionId(heartbeat, BeaconPacketType.Heartbeat, sessionId);

        stranger.SendTo(heartbeat, _serverEndPoint);
        Assert.True(Receive(stranger, SilenceWindowMilliseconds) is null, "the server acknowledged a heartbeat from a non-host, so it is still a reflector");

        // The real host must still be acknowledged, or sessions would expire under a working client.
        hostSocket.SendTo(heartbeat, _serverEndPoint);
        byte[]? ack = Receive(hostSocket, ReplyTimeoutMilliseconds);
        Assert.True(ack is not null && ack[0] == (byte)BeaconPacketType.HeartbeatAck, "the session host stopped receiving heartbeat acknowledgements");
    }

    /// <summary>
    /// C: a flood from one address must stop drawing replies. Without this the challenge costs an attacker one
    /// datagram and the server one MAC plus one send, for as long as the attacker keeps sending.
    /// </summary>
    [Fact]
    public void RequestFlood_FromOneAddress_StopsDrawingReplies()
    {
        const int FloodSize = 200;

        using Socket flooder = TestHarness.CreateRawSocket();

        byte[] request = new byte[1 + BeaconWireFormat.NonceBytes];
        request[0] = (byte)BeaconPacketType.RequestSession;

        for (int i = 0; i < FloodSize; i++)
            flooder.SendTo(request, _serverEndPoint);

        Thread.Sleep(400);

        int replies = 0;
        byte[] buffer = new byte[128];

        while (flooder.Available > 0)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            flooder.ReceiveFrom(buffer, ref from);
            replies++;
        }

        Assert.True(replies < FloodSize, $"all [{FloodSize}] flooded requests drew a reply, so the per-address limit is not engaged");
    }
}
