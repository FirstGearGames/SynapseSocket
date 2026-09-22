using System;
using System.Net;
using System.Security.Cryptography;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Packets;

namespace SynapseSocket.Transport;

internal sealed partial class IngressEngine
{
    /// <summary>
    /// True if NAT is enabled for any configuration.
    /// </summary>
    private bool _isNatEnabled;
    /// <summary>
    /// Size of the HMAC-SHA256 token truncated to this many bytes for NAT challenge packets.
    /// </summary>
    private const int NatTokenSize = 8;
    /// <summary>
    /// Duration of a single time bucket in ticks. Tokens are valid for the current bucket and the previous one (~60 seconds total).
    /// </summary>
    private const long NatTokenTimeBucketTicks = 30 * TimeSpan.TicksPerSecond;

    /// <summary>
    /// Handles an inbound NAT probe from an unrecognised endpoint.
    /// Responds with a challenge token instead of a handshake, subject to per-IP rate limiting.
    /// </summary>
    private void ProcessNatProbe(IPEndPoint fromEndPoint)
    {
        if (!_isNatEnabled)
            return;

        // Never respond to blacklisted addresses.
        ulong signature = _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);

        if (_security.IsBlacklisted(signature))
            return;

        // Only respond to unrecognised endpoints; established peers do not need probes.
        if (_connections.ConnectionsByEndPoint.TryGetValue(fromEndPoint, out SynapseConnection? _))
            return;

        // Rate-limit outbound challenge responses per source IP.
        long nowTicks = Clock.Ticks;
        long minIntervalTicks = _config.NatTraversal.IntervalMilliseconds * TimeSpan.TicksPerMillisecond;
        IpKey addressKey = IpKey.From(fromEndPoint.Address);

        /* The probe table is swept from RunMaintenance, not from here. Sweeping inline put an O(n) scan over a
         * table the attacker sizes onto a timer the attacker also chooses, which is the pause it was meant to
         * avoid. */
        long lastTicks = _natProbeLastResponseTicks.GetOrAdd(addressKey, 0L);

        if (nowTicks - lastTicks < minIntervalTicks)
            return;

        _natProbeLastResponseTicks[addressKey] = nowTicks;

        Span<byte> token = stackalloc byte[NatTokenSize];
        ComputeNatToken(fromEndPoint, nowTicks / NatTokenTimeBucketTicks, token);
        _sender.SendNatChallenge(fromEndPoint, token);
    }

    /// <summary>
    /// Handles an inbound NatChallenge packet from an unrecognised endpoint.
    /// If the payload matches a token this engine issued, sends a handshake (completing the probe exchange).
    /// Otherwise echoes a first-hand challenge back, stamped so the far side never echoes it again. That echo is
    /// the initiator side of a simultaneous P2P probe; the stamp is what stops two engines that cannot verify each
    /// other's tokens from bouncing the same bytes forever.
    /// </summary>
    private void ProcessNatChallengeExchange(IPEndPoint fromEndPoint, ReadOnlySpan<byte> payload)
    {
        if (!_isNatEnabled)
            return;

        /* A fresh challenge is NatTokenSize; one that has already been bounced back carries a trailing marker.
         * Anything else is malformed. */
        bool isEchoedChallenge = payload.Length == NatTokenSize + 1;

        if (payload.Length != NatTokenSize && !isEchoedChallenge)
            return;

        ulong signature = _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);

        if (_security.IsBlacklisted(signature))
            return;

        if (_connections.ConnectionsByEndPoint.TryGetValue(fromEndPoint, out SynapseConnection? _))
            return;

        long nowTicks = Clock.Ticks;
        long minIntervalTicks = _config.NatTraversal.IntervalMilliseconds * TimeSpan.TicksPerMillisecond;
        IpKey addressKey = IpKey.From(fromEndPoint.Address);

        long lastTicks = _natProbeLastResponseTicks.GetOrAdd(addressKey, 0L);

        if (nowTicks - lastTicks < minIntervalTicks)
            return;

        _natProbeLastResponseTicks[addressKey] = nowTicks;

        if (VerifyEndpointToken(_natChallengeHmac, fromEndPoint, payload[..NatTokenSize]))
        {
            _sender.SendHandshake(fromEndPoint);
            return;
        }

        /* Unrecognised token. Echoing it back is what lets the initiator side of a simultaneous P2P probe complete,
         * but an unconditional echo means two engines that cannot verify each other's tokens bounce the same bytes
         * forever, and one forged datagram naming two victims starts exactly that. Echo only a first-hand
         * challenge, and mark it so the far side knows not to echo again. */
        if (isEchoedChallenge)
            return;

        Span<byte> echo = stackalloc byte[NatTokenSize + 1];
        payload[..NatTokenSize].CopyTo(echo);
        echo[NatTokenSize] = 1;

        _sender.SendNatChallenge(fromEndPoint, echo);
    }

    /// <summary>
    /// Computes a truncated HMAC-SHA256 token bound to <paramref name="endPoint"/> and <paramref name="timeBucket"/>.
    /// Writes exactly <see cref="NatTokenSize"/> bytes into <paramref name="destination"/>.
    /// </summary>
    private void ComputeNatToken(IPEndPoint endPoint, long timeBucket, Span<byte> destination)
        => ComputeEndpointToken(_natChallengeHmac, endPoint, timeBucket, destination);

    /// <summary>
    /// Computes a truncated HMAC-SHA256 token binding <paramref name="endPoint"/> to <paramref name="timeBucket"/>.
    /// Shared by the NAT challenge and the handshake return-routability challenge, which differ only in the keyed
    /// <paramref name="hmac"/> so a token minted for one purpose can never be replayed into the other.
    /// <para>
    /// The instance is reused rather than constructed per call: both callers sit on unauthenticated receive paths,
    /// where a fresh <see cref="HMACSHA256"/> and its key schedule per datagram is exactly the cost an attacker
    /// would like to impose. The engine is single-threaded, so reuse is safe.
    /// </para>
    /// </summary>
    /// <param name="hmac">Keyed HMAC for the token's purpose.</param>
    /// <param name="endPoint">Endpoint the token is bound to.</param>
    /// <param name="timeBucket">Coarse time bucket the token is bound to.</param>
    /// <param name="destination">Receives exactly <see cref="NatTokenSize"/> bytes.</param>
    private static void ComputeEndpointToken(HMACSHA256 hmac, IPEndPoint endPoint, long timeBucket, Span<byte> destination)
    {
        Span<byte> addressBytes = stackalloc byte[16];
        endPoint.Address.TryWriteBytes(addressBytes, out int addressLength);

        int inputLength = addressLength + 2 + 8;
        Span<byte> input = stackalloc byte[inputLength];
        addressBytes[..addressLength].CopyTo(input);

        int offset = addressLength;
        input[offset++] = (byte)(endPoint.Port & 0xFF);
        input[offset++] = (byte)((endPoint.Port >> 8) & 0xFF);

        for (int i = 0; i < 8; i++)
            input[offset++] = (byte)((timeBucket >> (i * 8)) & 0xFF);

        Span<byte> hashBuffer = stackalloc byte[32];
        hmac.TryComputeHash(input, hashBuffer, out _);
        hashBuffer[..NatTokenSize].CopyTo(destination);
    }

    /// <summary>
    /// Returns true when <paramref name="token"/> is one this engine issued to <paramref name="endPoint"/> under
    /// <paramref name="hmac"/> in the current or previous time bucket, giving a legitimate peer roughly 30 to 60
    /// seconds to answer. Shared by the NAT challenge and the handshake return-routability challenge, which differ
    /// only in the keyed <paramref name="hmac"/>.
    /// </summary>
    /// <param name="hmac">Keyed HMAC for the token's purpose.</param>
    /// <param name="endPoint">Endpoint the token should be bound to.</param>
    /// <param name="token">The token the peer presented.</param>
    private static bool VerifyEndpointToken(HMACSHA256 hmac, IPEndPoint endPoint, ReadOnlySpan<byte> token)
    {
        long bucket = Clock.Ticks / NatTokenTimeBucketTicks;
        Span<byte> expected = stackalloc byte[NatTokenSize];

        /* Fixed-time comparison. A token is a secret the peer must reproduce, so an early-exit compare leaks how
         * many leading bytes were right. Remote timing over UDP is a stretch at 8 bytes, but the fix is free. */
        ComputeEndpointToken(hmac, endPoint, bucket, expected);
        if (CryptographicOperations.FixedTimeEquals(token, expected))
            return true;

        ComputeEndpointToken(hmac, endPoint, bucket - 1, expected);
        return CryptographicOperations.FixedTimeEquals(token, expected);
    }

    /// <summary>
    /// Answers an unknown endpoint's handshake with a return-routability challenge: its own nonce echoed back,
    /// followed by a token bound to its address and the current time bucket. Nothing is allocated for the peer:
    /// the token is stateless, so a source that cannot receive at the address it claimed simply never returns.
    /// </summary>
    /// <param name="fromEndPoint">Endpoint being challenged.</param>
    /// <param name="incomingPayload">The handshake payload received, whose leading nonce is echoed.</param>
    private void SendHandshakeChallenge(IPEndPoint fromEndPoint, ReadOnlySpan<byte> incomingPayload)
    {
        Span<byte> challenge = stackalloc byte[PacketHeader.HandshakeChallengeSize];

        incomingPayload[..PacketHeader.HandshakeNonceSize].CopyTo(challenge);
        ComputeHandshakeToken(fromEndPoint, Clock.Ticks / NatTokenTimeBucketTicks, challenge[PacketHeader.HandshakeNonceSize..]);

        _sender.SendHandshakePayload(fromEndPoint, challenge);
    }

    /// <summary>
    /// Computes the handshake return-routability token for <paramref name="endPoint"/> and <paramref name="timeBucket"/>.
    /// </summary>
    private void ComputeHandshakeToken(IPEndPoint endPoint, long timeBucket, Span<byte> destination)
        => ComputeEndpointToken(_handshakeChallengeHmac, endPoint, timeBucket, destination);

    /// <summary>
    /// Evicts stale entries from the NAT probe response-time dictionary.
    /// </summary>
    private void RemoveExpiredProbeLimitEntries(long nowTicks, long staleTicks) => RemoveExpiredEntries(_natProbeLastResponseTicks, nowTicks, staleTicks);
}
