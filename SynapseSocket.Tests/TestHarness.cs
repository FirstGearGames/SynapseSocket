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
    /// Captures background-loop exceptions (via <c>SynapseManager.UnhandledException</c>),
    /// connection-rejection failures (via <c>ConnectionFailed</c>), and any faulted
    /// <c>SendAsync</c> Tasks handed to <see cref="ObserveSend"/>. Exists specifically because
    /// tests commonly use <c>_ = SendAsync(...)</c> fire-and-forget, which otherwise swallows
    /// both synchronous and asynchronous failures and hides real bugs behind the xUnit timeout.
    /// </summary>
    public sealed class FailureObserver : IDisposable
    {
        private readonly ConcurrentBag<string> _failures = [];
        private readonly ConcurrentBag<(SynapseManager Manager, UnhandledExceptionDelegate Handler)> _unhandledSubscriptions = [];
        private readonly ConcurrentBag<(SynapseManager Manager, ConnectionFailedDelegate Handler)> _failedSubscriptions = [];

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
            UnhandledExceptionDelegate unhandled = ex => _failures.Add($"UnhandledException: {ex}");
            ConnectionFailedDelegate failed = args => _failures.Add($"ConnectionFailed: {args.Reason} (endpoint={args.EndPoint}) {args.Message}");
            manager.UnhandledException += unhandled;
            manager.ConnectionFailed += failed;
            _unhandledSubscriptions.Add((manager, unhandled));
            _failedSubscriptions.Add((manager, failed));
        }

        /// <summary>
        /// Attach a fault-only continuation to <paramref name="sendTask"/> so that a faulted
        /// <c>SendAsync</c> (synchronous or asynchronous) is recorded instead of vanishing.
        /// Use in place of <c>_ = clients[i].SendAsync(...)</c>:
        /// <c>observer.ObserveSend(clients[i].SendAsync(...));</c>
        /// </summary>
        public void ObserveSend(Task sendTask)
        {
            _ = sendTask.ContinueWith(static (completed, state) =>
            {
                FailureObserver self = (FailureObserver)state!;

                if (completed.Exception is AggregateException aggregateException)
                {
                    foreach (Exception inner in aggregateException.Flatten().InnerExceptions)
                        self._failures.Add($"SendAsync faulted: {inner}");
                }
            }, this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
            foreach ((SynapseManager manager, UnhandledExceptionDelegate handler) in _unhandledSubscriptions)
                manager.UnhandledException -= handler;

            foreach ((SynapseManager manager, ConnectionFailedDelegate handler) in _failedSubscriptions)
                manager.ConnectionFailed -= handler;
        }
    }
}
