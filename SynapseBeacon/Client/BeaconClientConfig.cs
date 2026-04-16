using System;
using System.Net;

namespace SynapseBeacon.Client;

/// <summary>
/// Configuration for a <see cref="BeaconClient"/>.
/// </summary>
public sealed class BeaconClientConfig
{
    /// <summary>
    /// Rendezvous (beacon) server endpoint. Required.
    /// </summary>
    public IPEndPoint ServerEndPoint { get; }

    /// <summary>
    /// Maximum time to wait for a <see cref="Wire.BeaconPacketType.SessionCreated"/> or
    /// <see cref="Wire.BeaconPacketType.PeerReady"/> response before the operation fails.
    /// Default: 10 seconds.
    /// </summary>
    public int ResponseTimeoutMilliseconds = 10_000;

    /// <summary>
    /// Interval between heartbeat packets sent by a host to keep its session alive.
    /// Default: 30 seconds.
    /// </summary>
    public int HeartbeatIntervalMilliseconds = 30_000;

    /// <summary>
    /// Initialises a new config instance targeting <paramref name="serverEndPoint"/>.
    /// </summary>
    public BeaconClientConfig(IPEndPoint serverEndPoint)
    {
        ServerEndPoint = serverEndPoint ?? throw new ArgumentNullException(nameof(serverEndPoint));
    }
}
