using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests;

/// <summary>
/// Shared helpers for spinning up real loopback Synapse engines quickly
/// inside tests. Everything is loopback-only and uses OS-assigned free
/// ports so parallel test runs don't collide.
/// <para>
/// The engine is poll-driven: nothing is received and no maintenance runs unless the engine is polled. Tests
/// therefore drive their engines with <see cref="PumpUntil"/> / <see cref="PumpFor"/> from the test thread,
/// which keeps each engine single-threaded (the one valid usage model).
/// </para>
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
            EnableTelemetry = true
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
            EnableTelemetry = true
        };
        tweak?.Invoke(synapseConfig);
        return synapseConfig;
    }

    /// <summary>
    /// Polls the given engines repeatedly until <paramref name="condition"/> is true or <paramref name="timeoutMs"/>
    /// elapses. Returns true if the condition became true. This is how tests advance a poll-driven engine while
    /// waiting for an asynchronous outcome (handshake, delivery, ACK, timeout, …).
    /// </summary>
    public static bool PumpUntil(Func<bool> condition, int timeoutMs, params SynapseManager[] engines)
    {
        long deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            for (int i = 0; i < engines.Length; i++)
                engines[i].Poll();

            if (condition())
                return true;

            Thread.Sleep(1);
        }

        for (int i = 0; i < engines.Length; i++)
            engines[i].Poll();

        return condition();
    }

    /// <summary>
    /// Polls the given engines for a fixed duration. Use where a test previously slept to let background work run.
    /// </summary>
    public static void PumpFor(int durationMs, params SynapseManager[] engines)
    {
        long deadline = Environment.TickCount64 + durationMs;

        while (Environment.TickCount64 < deadline)
        {
            for (int i = 0; i < engines.Length; i++)
                engines[i].Poll();

            Thread.Sleep(1);
        }
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
                return violationEventArgs.Action;
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

    /// <summary>
    /// Attaches a <see cref="FailureObserver"/> to the supplied engines and returns it.
    /// Call <see cref="FailureObserver.AssertNoFailures"/> before your <c>finally</c> disposal
    /// to make the test fail with a concrete error list instead of silently timing out.
    /// </summary>
    public static FailureObserver ObserveFailures(params SynapseManager[] managers)
    {
        FailureObserver observer = new();

        foreach (SynapseManager manager in managers)
            observer.Attach(manager);

        return observer;
    }

    /// <summary>
    /// Captures background-loop exceptions (via <c>SynapseManager.UnhandledException</c>) and
    /// connection-rejection failures (via <c>ConnectionFailed</c>), plus any exception thrown by a send routed
    /// through <see cref="RunSend"/>. Sends are synchronous now, so a failed send throws at the call site;
    /// <see cref="RunSend"/> records it instead of aborting the test mid-loop.
    /// </summary>
    public sealed class FailureObserver : IDisposable
    {
        private readonly ConcurrentBag<string> _failures = [];
        private readonly ConcurrentBag<(SynapseManager Manager, UnhandledExceptionHandler Handler)> _unhandledSubscriptions = [];
        private readonly ConcurrentBag<(SynapseManager Manager, ConnectionFailedHandler Handler)> _failedSubscriptions = [];

        /// <summary>True if any failure has been recorded.</summary>
        public bool HasFailures => !_failures.IsEmpty;

        /// <summary>Snapshot of the recorded failure descriptions.</summary>
        public string[] Failures => [.. _failures];

        /// <summary>
        /// Attach to one more <see cref="SynapseManager"/>.
        /// Normally you'd pass all managers to <see cref="TestHarness.ObserveFailures"/> up-front;
        /// this is only needed when managers are constructed later in the test body.
        /// </summary>
        public void Attach(SynapseManager manager)
        {
            UnhandledExceptionHandler unhandled = ex => _failures.Add($"UnhandledException: {ex}");
            ConnectionFailedHandler failed = args => _failures.Add($"ConnectionFailed: {args.Reason} (endpoint={args.EndPoint}) {args.Message}");
            manager.UnhandledException += unhandled;
            manager.ConnectionFailed += failed;
            _unhandledSubscriptions.Add((manager, unhandled));
            _failedSubscriptions.Add((manager, failed));
        }

        /// <summary>
        /// Runs a synchronous send, recording any exception it throws instead of letting it abort the caller.
        /// Use in place of <c>client.Send(...)</c> when a transient send failure should be recorded, not thrown:
        /// <c>observer.RunSend(() =&gt; client.Send(...));</c>
        /// </summary>
        public void RunSend(Action send)
        {
            try
            {
                send();
            }
            catch (Exception sendException)
            {
                _failures.Add($"Send faulted: {sendException}");
            }
        }

        /// <summary>
        /// Throws an <see cref="Xunit.Sdk.XunitException"/> listing every recorded failure.
        /// Does nothing when no failures are observed. Call this at the end of your test's
        /// <c>try</c> block (before the <c>finally</c> that disposes the engines).
        /// </summary>
        public void AssertNoFailures()
        {
            if (_failures.IsEmpty)
                return;

            throw new Xunit.Sdk.XunitException(
                "SynapseManager reported " + _failures.Count + " failure(s):" + Environment.NewLine +
                string.Join(Environment.NewLine, _failures));
        }

        /// <summary>
        /// Detaches every handler this observer attached. Does not assert on failures — call
        /// <see cref="AssertNoFailures"/> explicitly first if you want the test to fail.
        /// </summary>
        public void Dispose()
        {
            foreach ((SynapseManager manager, UnhandledExceptionHandler handler) in _unhandledSubscriptions)
                manager.UnhandledException -= handler;

            foreach ((SynapseManager manager, ConnectionFailedHandler handler) in _failedSubscriptions)
                manager.ConnectionFailed -= handler;
        }
    }
}
