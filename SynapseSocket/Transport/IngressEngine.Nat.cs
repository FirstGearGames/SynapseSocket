using System;
using System.Net;
using System.Threading;
using SynapseSocket.Connections;
using SynapseSocket.Packets;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Transport;

/// <summary>
/// Ingress Engine (Receiver).
/// Manages incoming data and initial filtering.
/// Applies lowest-level mitigations BEFORE any payload copy.
/// </summary>
public sealed partial class IngressEngine
{
    /// <summary>
    /// Raised when a NAT rendezvous server reports the peer's external endpoint.
    /// </summary>
    public event NatPeerReadyDelegate? NatPeerReady;
    /// <summary>
    /// Raised when a NAT rendezvous server rejects the session because it is already full.
    /// </summary>
    public event NatSessionFullDelegate? NatSessionFull;

    /// <summary>
    /// Handles an inbound NAT probe from an unrecognised endpoint.
    /// Responds with a probe + handshake, subject to per-IP rate limiting.
    /// </summary>
    /// <param name="fromEndPoint">The source endpoint that sent the NAT probe.</param>
    /// <param name="cancellationToken">Token forwarded to outbound send helpers.</param>
    private void ProcessNatProbe(IPEndPoint fromEndPoint, CancellationToken cancellationToken)
    {
        if (_config.NatTraversal.Mode == NatTraversalMode.Disabled)
            return;

        // Never respond to blacklisted addresses.
        ulong signature = _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);
        if (_security.IsBlacklisted(signature))
            return;

        // Only respond to unrecognised endpoints; established peers do not need probes.
        if (_connections.TryGet(fromEndPoint, out SynapseConnection? _))
            return;

        // Rate-limit outbound probe responses per source IP to mitigate amplification abuse.
        // A spoofed-source probe would cause us to send one probe + one handshake to the spoofed
        // address, but no more than once per IntervalMilliseconds — bounding the amplification
        // factor to 2 packets at the configured interval regardless of inbound flood rate.
        long nowTicks = DateTime.UtcNow.Ticks;
        long minIntervalTicks = _config.NatTraversal.IntervalMilliseconds * TimeSpan.TicksPerMillisecond;
        IpKey addressKey = IpKey.From(fromEndPoint.Address);

        // Periodic eviction: bound dictionary growth without relying on traffic volume.
        long lastProbeEvict = Volatile.Read(ref _lastProbeEvictionTicks);
        if (nowTicks - lastProbeEvict > TimeSpan.TicksPerMinute)
            if (Interlocked.CompareExchange(ref _lastProbeEvictionTicks, nowTicks, lastProbeEvict) == lastProbeEvict)
                RemoveExpiredProbeLimitEntries(nowTicks, staleTicks: minIntervalTicks * 10);

        long lastTicks = _natProbeLastResponseTicks.GetOrAdd(addressKey, 0L);
        if (nowTicks - lastTicks < minIntervalTicks)
            return;
        _natProbeLastResponseTicks[addressKey] = nowTicks;

        _ = _sender.SendNatProbeAsync(fromEndPoint, cancellationToken);
        _ = _sender.SendHandshakeAsync(fromEndPoint, cancellationToken);
    }
    
    /// <summary>
    /// Routes an inbound packet from the configured NAT rendezvous server
    /// to the appropriate handler (PeerReady, SessionFull, HeartbeatAck).
    /// </summary>
    /// <param name="fromEndPoint">The source endpoint the packet arrived from.</param>
    /// <param name="payload">The packet bytes following the wire header.</param>
    private void ProcessNatServerPacket(IPEndPoint fromEndPoint, ReadOnlySpan<byte> payload)
    {
        if (_config.NatTraversal.Mode != NatTraversalMode.Server)
            return;
        if (_config.NatTraversal.Server.ServerEndPoint is null)
            return;
        if (!fromEndPoint.Equals(_config.NatTraversal.Server.ServerEndPoint))
            return;
        if (payload.Length < 1)
            return;

        NatPacketType packetType = (NatPacketType)payload[0];
        ReadOnlySpan<byte> body = payload[1..];

        switch (packetType)
        {
            case NatPacketType.PeerReady:
                IPEndPoint? peerEndPoint = TryParsePeerEndPoint(body);
                if (peerEndPoint is not null)
                    NatPeerReady?.Invoke(peerEndPoint);
                break;

            case NatPacketType.SessionFull:
                NatSessionFull?.Invoke();
                break;

            case NatPacketType.HeartbeatAck:
                break;
        }
    }

}