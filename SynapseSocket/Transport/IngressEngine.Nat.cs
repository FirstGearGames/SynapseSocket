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

    private void HandleNatProbe(IPEndPoint fromEndPoint, CancellationToken cancellationToken)
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
        long nowTicks = DateTime.UtcNow.Ticks;
        long minIntervalTicks = _config.NatTraversal.IntervalMilliseconds * TimeSpan.TicksPerMillisecond;
        IpKey addressKey = IpKey.From(fromEndPoint.Address);

        // Lightweight periodic eviction: run every 100 probes to bound dictionary growth.
        if (Interlocked.Increment(ref _natProbeCounter) % 100 == 0)
            RemoveStaleProbeLimitEntries(nowTicks, staleTicks: minIntervalTicks * 10);

        long lastTicks = _natProbeRateLimiter.GetOrAdd(addressKey, 0L);
        if (nowTicks - lastTicks < minIntervalTicks)
            return;
        _natProbeRateLimiter[addressKey] = nowTicks;

        _ = _sender.SendNatProbeAsync(fromEndPoint, cancellationToken);
        _ = _sender.SendHandshakeAsync(fromEndPoint, cancellationToken);
    }
    
    private void HandleNatServerPacket(IPEndPoint fromEndPoint, ReadOnlySpan<byte> payload)
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