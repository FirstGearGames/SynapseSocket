using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests;

/// <summary>
/// Shared helpers for spinning up real loopback Synapse engines quickly
/// inside tests. Everything is loopback-only and uses OS-assigned free
/// ports so parallel test runs don't collide.
/// </summary>
public static class TestHarness
{
    /// <summary>
    /// Grabs a free UDP port by binding to port 0 and releasing it.
    /// </summary>
    public static int GetFreePort()
    {
        using Socket probeSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probeSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probeSocket.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Makes a loopback-only server config on a free port.
    /// </summary>
    public static SynapseConfig ServerConfig(int port, Action<SynapseConfig>? tweak = null)
    {
        SynapseConfig synapseConfig = new()
        {
            BindEndPoints = { new(IPAddress.Loopback, port) },
            IsTelemetryEnabled = true
        };
        tweak?.Invoke(synapseConfig);
        return synapseConfig;
    }

    /// <summary>
    /// Makes a loopback-only client config on an ephemeral port.
    /// </summary>
    public static SynapseConfig ClientConfig(Action<SynapseConfig>? tweak = null)
    {
        SynapseConfig synapseConfig = new()
        {
            BindEndPoints = { new(IPAddress.Loopback, 0) },
            IsTelemetryEnabled = true
        };
        tweak?.Invoke(synapseConfig);
        return synapseConfig;
    }

    /// <summary>
    /// Polls <paramref name="condition"/> every 20 ms up to <paramref name="timeoutMs"/>.
    /// Returns true if the condition ever became true; false on timeout.
    /// </summary>
    public static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return condition();
    }

    /// <summary>
    /// Thread-safe event recorder for tests that want to count callbacks.
    /// </summary>
    public sealed class EventRecorder
    {
        private int _packetsReceived;
        private int _packetsSent;
        private int _connectionsEstablished;
        private int _connectionsClosed;
        private int _failures;
        private int _violations;

        public int PacketsReceived => Volatile.Read(ref _packetsReceived);
        public int PacketsSent => Volatile.Read(ref _packetsSent);
        public int ConnectionsEstablished => Volatile.Read(ref _connectionsEstablished);
        public int ConnectionsClosed => Volatile.Read(ref _connectionsClosed);
        public int Failures => Volatile.Read(ref _failures);
        public int Violations => Volatile.Read(ref _violations);

        public ConcurrentBag<byte[]> Payloads { get; } = [];
        public ConcurrentBag<ConnectionRejectedReason> FailureReasons { get; } = [];
        public ConcurrentBag<ViolationReason> ViolationReasons { get; } = [];

        public void Attach(SynapseManager engine)
        {
            engine.PacketReceived += (packetReceivedEventArgs) =>
            {
                Interlocked.Increment(ref _packetsReceived);
                Payloads.Add(packetReceivedEventArgs.Payload.ToArray());
            };
            engine.PacketSent += (_) => Interlocked.Increment(ref _packetsSent);
            engine.ConnectionEstablished += (_) => Interlocked.Increment(ref _connectionsEstablished);
            engine.ConnectionClosed += (_) => Interlocked.Increment(ref _connectionsClosed);
            engine.ConnectionFailed += (connectionFailedEventArgs) =>
            {
                Interlocked.Increment(ref _failures);
                FailureReasons.Add(connectionFailedEventArgs.Reason);
            };
            engine.ViolationDetected += (violationEventArgs) =>
            {
                Interlocked.Increment(ref _violations);
                ViolationReasons.Add(violationEventArgs.Reason);
            };
        }
    }

    /// <summary>
    /// Creates a raw UDP socket bound to loopback.
    /// Useful for exploit tests that need to send arbitrary, hand-crafted bytes
    /// without going through Synapse at all.
    /// </summary>
    public static Socket CreateRawSocket()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }
}
