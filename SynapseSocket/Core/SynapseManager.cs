using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CodeBoost.Performance;
using SynapseSocket.Connections;
using SynapseSocket.Diagnostics;
using SynapseSocket.Packets;
using SynapseSocket.Security;
using SynapseSocket.Transport;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Core;

/// <summary>
/// The main entry point for the SynapseSocket UDP Transport Engine.
/// This is a partial class; the core API lives here, and the background maintenance loops (keep-alive, reliable retransmission) live in <c>SynapseManager.Maintenance.cs</c>.
/// </summary>
public sealed partial class SynapseManager : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Raised when a payload is received from any connection.
    /// </summary>
    public event PacketReceivedDelegate? PacketReceived;
    /// <summary>
    /// Raised after a packet has been transmitted on the wire.
    /// </summary>
    public event PacketSentDelegate? PacketSent;
    /// <summary>
    /// Raised when a new connection is established.
    /// </summary>
    public event ConnectionEstablishedDelegate? ConnectionEstablished;
    /// <summary>
    /// Raised when a connection terminates (timeout, peer-disconnect, etc.).
    /// </summary>
    public event ConnectionClosedDelegate? ConnectionClosed;
    /// <summary>
    /// Raised on any binding, signature, or validation failure.
    /// </summary>
    public event ConnectionFailedDelegate? ConnectionFailed;
    /// <summary>
    /// Raised when the engine detects a violation (oversized packet, rate limit breach, malformed data, or rejected signature).
    /// Handlers may override <see cref="ViolationEventArgs.Action"/> to customize how the engine responds.
    /// When no handler is subscribed, the default action (<see cref="ViolationAction.KickAndBlacklist"/>) is applied.
    /// <para>
    /// <b>Warning:</b> do not unconditionally downgrade <see cref="ViolationEventArgs.Action"/> inside a handler.
    /// Setting the action to <see cref="ViolationAction.Ignore"/> suppresses every protective measure the engine would otherwise take. See <see cref="ViolationEventArgs.Action"/> for details.
    /// </para>
    /// </summary>
    public event ViolationDelegate? ViolationDetected;
    /// <summary>
    /// Raised when an unexpected exception escapes a background loop (ingress or maintenance).
    /// Subscribe to route engine errors into your logging system (e.g., Unity's Debug.LogException).
    /// The loop that raised the exception continues running after the handler returns.
    /// If no handler is subscribed the exception is silently discarded.
    /// </summary>
    public event UnhandledExceptionDelegate? UnhandledException;
    /// <summary>
    /// The configuration the engine was constructed with.
    /// </summary>
    public SynapseConfig Config { get; }
    /// <summary>
    /// Telemetry counters. Present whether telemetry is enabled or not.
    /// </summary>
    public Telemetry Telemetry { get; }
    /// <summary>
    /// Live connection manager.
    /// </summary>
    public ConnectionManager Connections { get; }
    /// <summary>
    /// Security provider used by this engine.
    /// </summary>
    public SecurityProvider Security { get; }
    /// <summary>
    /// True if <see cref="StartAsync"/> has completed successfully and the engine has not been stopped or disposed.
    /// </summary>
    public bool IsRunning => _isStarted && !_isDisposed;
    private bool _isStarted;
    #if PERFTEST
    /// <summary>
    /// Internal performance counters for hot-path timing. DEBUG builds only.
    /// </summary>
    internal PerfCounters Perf { get; } = new();
    #endif
    private bool _isDisposed;
    private readonly LatencySimulator _latencySimulator;
    private readonly bool _isSegmentingEnabled;
    private readonly int _maximumUnsegmentedPayload;
    private readonly List<Socket> _sockets = [];
    private readonly List<Task> _ingressTasks = [];
    private readonly List<IngressEngine> _ingressEngines = [];
    private TransmissionEngine? _transmissionEngine;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _maintenanceTask;

    /// <summary>
    /// Creates a new SynapseSocket engine from the supplied configuration.
    /// Call <see cref="StartAsync"/> to begin binding and receiving.
    /// </summary>
    public SynapseManager(SynapseConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));

        if (Config.BindEndPoints.Count == 0)
            throw new ArgumentException("At least one bind endpoint is required.", nameof(config));

        if (Config.SegmentAssemblyTimeoutMilliseconds > 0 && Config.SegmentAssemblyTimeoutMilliseconds > 300_000)
            throw new ArgumentOutOfRangeException(nameof(config), "SegmentAssemblyTimeoutMilliseconds must not exceed 300000 (5 minutes).");

        ISignatureProvider signatureProvider = Config.SignatureProvider ?? new DefaultSignatureProvider();
        Security = new(signatureProvider, Config.MaximumPacketsPerSecond, Config.MaximumBytesPerSecond, Config.MaximumPacketSize);
        Connections = new();
        Telemetry = new(Config.EnableTelemetry);
        _latencySimulator = new(Config.LatencySimulator);
        _isSegmentingEnabled = Config.MaximumSegments != SynapseConfig.DisabledMaximumSegments;
        /* Unreliable requires a couple bytes less for segmenting when being sent out
         * of order, which would require different maximum payload sizes between reliable
         * and unreliable segmented. Rather than add additional complexity and branching
         * the rare byte cost is consumed. */
        _maximumUnsegmentedPayload = (int)Config.MaximumTransmissionUnit - PacketHeader.TypeSize - PacketHeader.SequenceSize;
        
        /* Maintenance. */
        _connectionKeepAliveTicks = TimeSpan.FromMilliseconds(Config.Connection.KeepAliveIntervalMilliseconds).Ticks;
        _connectionTimeoutTicks = TimeSpan.FromMilliseconds(Config.Connection.TimeoutMilliseconds).Ticks;
        _reliableResendTicks = TimeSpan.FromMilliseconds(Config.Reliable.ResendMilliseconds).Ticks;
        _maximumReliableRetries = Config.Reliable.MaximumRetries;
        _isAckBatchingEnabled = Config.Reliable.AckBatchingEnabled;
        // Value is unset is ack batching is not enabled.
        _ackBatchingIntervalTicks = _isAckBatchingEnabled ? TimeSpan.FromMilliseconds(Config.Reliable.AckBatchIntervalMilliseconds).Ticks : UnsetAckBatchingIntervalTicks;
        /* Value is unset if segmenting is not enabled or if
         * a timeout is unset. */
        uint segmentAssemblyTimeoutMilliseconds = config.SegmentAssemblyTimeoutMilliseconds;
        _segmentAssemblyTimeoutTicks = _isSegmentingEnabled && segmentAssemblyTimeoutMilliseconds != SynapseConfig.UnsetSegmentAssemblyTimeout ? TimeSpan.FromMilliseconds(segmentAssemblyTimeoutMilliseconds).Ticks : UnsetSegmentAssemblyTimeoutTicks;

    }

    /// <summary>
    /// Binds all configured endpoints, starts ingress loops, and launches background maintenance (keep-alive and reliable retransmission).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isStarted)
            throw new InvalidOperationException("Engine is already running.");

        if (_isDisposed)
            throw new ObjectDisposedException(nameof(SynapseManager));

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Socket? ipv4Socket = null;
        Socket? ipv6Socket = null;

        foreach (IPEndPoint bindEndPoint in Config.BindEndPoints)
        {
            Socket socket;
            try
            {
                socket = new(bindEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

                if (bindEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);

                socket.Bind(bindEndPoint);
            }
            catch (SocketException socketException)
            {
                RaiseConnectionFailed(bindEndPoint, ConnectionRejectedReason.BindFailed, socketException.Message);
                continue;
            }

            _sockets.Add(socket);

            if (bindEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                ipv6Socket = socket;
            else
                ipv4Socket = socket;
        }

        if (_sockets.Count == 0)
            throw new InvalidOperationException("Failed to bind any configured endpoints.");

        _transmissionEngine = new(ipv4Socket ?? _sockets[0], ipv6Socket, Config, Telemetry, _latencySimulator);

        foreach (Socket socket in _sockets)
        {
            IngressEngine ingressEngine = new(socket, Config, Security, Connections, _transmissionEngine, Telemetry);

            ingressEngine.PayloadDelivered += OnPayloadDelivered;
            ingressEngine.ConnectionEstablished += OnConnectionEstablishedInternal;
            ingressEngine.ConnectionClosed += OnConnectionClosedInternal;
            ingressEngine.ConnectionFailed += RaiseConnectionFailed;
            ingressEngine.ViolationOccurred += HandleViolation;
            ingressEngine.UnhandledException += OnUnhandledException;
            ingressEngine.NatPeerReady += OnNatPeerReady;
            ingressEngine.NatSessionFull += OnNatSessionFull;
            ingressEngine.NatSessionCreated += OnNatSessionCreated;
            ingressEngine.NatSessionUnavailable += OnNatSessionUnavailable;
            
            _ingressEngines.Add(ingressEngine);
            _ingressTasks.Add(ingressEngine.StartAsync(_cancellationTokenSource.Token));
        }

        _maintenanceTask = Task.Run(() => MaintenanceLoopAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        _isStarted = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gracefully stops all ingress loops and background maintenance, then closes all sockets.
    /// The engine may be restarted by calling <see cref="StartAsync"/> again after this returns.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isStarted || _isDisposed)
            return;

        _isStarted = false;
        await ShutdownCoreAsync().ConfigureAwait(false);
        CancellationTokenSource? oldCts = _cancellationTokenSource;
        _cancellationTokenSource = null;
        oldCts?.Dispose();
    }

    /// <summary>
    /// Initiates an outgoing connection to the specified remote endpoint.
    /// Sends a handshake packet; the connection is considered established when the remote handshake response arrives.
    /// </summary>
    public async Task<SynapseConnection> ConnectAsync(IPEndPoint endPoint, CancellationToken cancellationToken = default)
    {
        if (!_isStarted || _transmissionEngine is null)
            throw new InvalidOperationException("Engine not started.");

        ulong signature = Security.ComputeSignature(endPoint, ReadOnlySpan<byte>.Empty);

        if (Security.IsBlacklisted(signature))
        {
            RaiseConnectionFailed(endPoint, ConnectionRejectedReason.Blacklisted, null);
            throw new InvalidOperationException("Remote endpoint is blacklisted.");
        }

        SynapseConnection synapseConnection = Connections.GetOrAdd(endPoint, signature, out _);

        await _transmissionEngine.SendHandshakeAsync(endPoint, cancellationToken).ConfigureAwait(false);

        if (Config.NatTraversal.Mode == NatTraversalMode.FullCone)
            _ = Task.Run(() => NatPunchAsync(synapseConnection, endPoint, cancellationToken), cancellationToken);

        return synapseConnection;
    }

    /// <summary>
    /// Sends an unreliable payload on the given connection.
    /// When the payload exceeds the MTU, behaviour is controlled by <see cref="SynapseConfig.UnreliableSegmentMode"/>:
    /// <list type="bullet">
    /// <item><see cref="UnreliableSegmentMode.Disabled"/> — throws.</item>
    /// <item><see cref="UnreliableSegmentMode.SegmentUnreliable"/> — splits into unreliable segments (default).</item>
    /// <item><see cref="UnreliableSegmentMode.SegmentReliable"/> — splits into reliable segments.</item>
    /// </list>
    /// </summary>
    public async Task SendAsync(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable, CancellationToken cancellationToken = default)
    {
        EnsureRunning();

        if (payload.Count <= _maximumUnsegmentedPayload)
        {
            if (isReliable)
                await _transmissionEngine!.SendReliableUnsegmentedAsync(synapseConnection, payload, cancellationToken).ConfigureAwait(false);
            else
                await _transmissionEngine!.SendUnreliableUnsegmentedAsync(synapseConnection, payload, cancellationToken).ConfigureAwait(false);

            RaisePacketSent(synapseConnection.RemoteEndPoint, payload, isReliable);

            return;
        }

        /* If here, the packet must be segmented. */

        // Segmenting is disabled entirely.
        if (!_isSegmentingEnabled)
            throw new InvalidOperationException($"Payload ({payload.Count} bytes) exceeds the MTU-based limit ({_maximumUnsegmentedPayload} bytes). Set MaximumSegments to enable segmentation.");

        // An additional check on segmentation is required for unreliable sending.
        if (!isReliable)
        {
            UnreliableSegmentMode unreliableSegmentMode = Config.UnreliableSegmentMode;

            if (unreliableSegmentMode is UnreliableSegmentMode.Disabled)
                throw new InvalidOperationException($"Unreliable payload ({payload.Count} bytes) exceeds the MTU-based limit ({_maximumUnsegmentedPayload} bytes). Set UnreliableSegmentMode or reduce payload size.");

            // Make reliable if the unreliableSegmentMode permits.
            isReliable = unreliableSegmentMode is UnreliableSegmentMode.SegmentReliable;
        }

        await _transmissionEngine!.SendSegmentedAsync(synapseConnection, payload, isReliable, GetOrRentSplitter(synapseConnection), cancellationToken).ConfigureAwait(false);
        RaisePacketSent(synapseConnection.RemoteEndPoint, payload, isReliable);
    }

    /// <summary>
    /// Gracefully disconnects a connection, notifying the peer.
    /// </summary>
    public async Task DisconnectAsync(SynapseConnection synapseConnection, CancellationToken cancellationToken = default)
    {
        if (_transmissionEngine is not null)
            await _transmissionEngine.SendDisconnectAsync(synapseConnection, cancellationToken).ConfigureAwait(false);

        ReturnConnectionSegmenters(synapseConnection);
        ReturnReorderBufferToPool(synapseConnection);
        SynapseConnection.DrainPendingReliableQueue(synapseConnection);
        synapseConnection.State = ConnectionState.Disconnected;
        Connections.Remove(synapseConnection.RemoteEndPoint, out _);

        RaiseConnectionClosed(synapseConnection);
    }

    /// <summary>
    /// Central violation handler.
    /// Builds a <see cref="ViolationEventArgs"/> via the pool, applies the caller-supplied <paramref name="initialAction"/>
    /// as the default response, invokes any user listener (which may mutate <see cref="ViolationEventArgs.Action"/>),
    /// and then applies the final action.
    /// </summary>
    internal void HandleViolation(IPEndPoint endPoint, ulong signature, ViolationReason violationReason, int packetSize, string? details, ViolationAction initialAction = ViolationAction.KickAndBlacklist)
    {
        Connections.ConnectionsByEndPoint.TryGetValue(endPoint, out SynapseConnection? synapseConnection);

        ViolationEventArgs violationEventArgs = ResettableObjectPool<ViolationEventArgs>.Rent();
        violationEventArgs.Initialize(endPoint, signature, violationReason, synapseConnection, packetSize, details, initialAction);

        try
        {
            try
            {
                ViolationDetected?.Invoke(violationEventArgs);
            }
            catch
            {
                /* never let a listener crash the ingress path */
            }

            switch (violationEventArgs.Action)
            {
                case ViolationAction.Ignore:
                    return;

                case ViolationAction.Drop:
                    return;

                case ViolationAction.Kick:
                    DisconnectAndBlacklist(endPoint, canBlacklist: false);
                    return;

                case ViolationAction.KickAndBlacklist:
                default: // ViolationAction.KickAndBlacklist
                    if (signature != SecurityProvider.UnsetSignature)
                        Security.AddToBlacklist(signature);

                    DisconnectAndBlacklist(endPoint, canBlacklist: false);
                    return;
            }
        }
        finally
        {
            ResettableObjectPool<ViolationEventArgs>.Return(violationEventArgs);
        }
    }

    /// <summary>
    /// Stops the engine and releases all resources. Prefer <see cref="DisposeAsync"/> in async contexts.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _isStarted = false;

        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch { }

        CloseSockets();

        List<Task> pendingTasks = [.. _ingressTasks];

        if (_maintenanceTask is not null)
            pendingTasks.Add(_maintenanceTask);

        _ingressTasks.Clear();
        _ingressEngines.Clear();
        _maintenanceTask = null;

        if (pendingTasks.Count > 0)
        {
            try
            {
                Task.WhenAll(pendingTasks).Wait(5000);
            }
            catch { }
        }

        _cancellationTokenSource?.Dispose();
    }

    /// <summary>
    /// Stops the engine and releases all resources asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _isStarted = false;

        await ShutdownCoreAsync().ConfigureAwait(false);

        _cancellationTokenSource?.Dispose();
    }

    /// <summary>
    /// Cancels all loops, closes sockets, and waits up to 5 seconds for all background tasks to exit.
    /// Shared by <see cref="StopAsync"/> and <see cref="DisposeAsync"/>.
    /// </summary>
    private async Task ShutdownCoreAsync()
    {
        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch { }

        CloseSockets();

        List<Task> pendingTasks = [.. _ingressTasks];

        if (_maintenanceTask is not null)
            pendingTasks.Add(_maintenanceTask);

        _ingressTasks.Clear();
        _ingressEngines.Clear();
        _maintenanceTask = null;

        if (pendingTasks.Count > 0)
            await Task.WhenAny(Task.WhenAll(pendingTasks), Task.Delay(5000)).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes and disposes all bound sockets, swallowing any exceptions.
    /// </summary>
    private void CloseSockets()
    {
        foreach (Socket socket in _sockets)
        {
            try
            {
                socket.Close();
            }
            catch { }

            try
            {
                socket.Dispose();
            }
            catch { }
        }

        _sockets.Clear();
    }

    /// <summary>
    /// Forwards a background-loop exception to the <see cref="UnhandledException"/> event.
    /// </summary>
    private void OnUnhandledException(Exception exception) => UnhandledException?.Invoke(exception);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if the engine is not currently running.
    /// </summary>
    private void EnsureRunning()
    {
        if (!_isStarted || _transmissionEngine is null || _isDisposed)
            throw new InvalidOperationException("Engine is not running.");
    }

    /// <summary>
    /// Returns the existing splitter for <paramref name="synapseConnection"/>, or rents a fresh one from the pool and atomically assigns it.
    /// If two threads race, the loser's instance is returned to the pool immediately and the winner's instance is used.
    /// </summary>
    private PacketSplitter GetOrRentSplitter(SynapseConnection synapseConnection)
    {
        if (synapseConnection.Splitter is not null)
            return synapseConnection.Splitter;

        PacketSplitter rented = ResettableObjectPool<PacketSplitter>.Rent();
        rented.Initialize(Config.MaximumTransmissionUnit, Config.MaximumSegments);

        PacketSplitter? existing = Interlocked.CompareExchange(ref synapseConnection.Splitter, rented, null);

        if (existing is not null)
        {
            ResettableObjectPool<PacketSplitter>.Return(rented);
            return existing;
        }

        return rented;
    }

    /// <summary>
    /// Atomically clears and returns both the splitter and reassembler on <paramref name="synapseConnection"/>
    /// to their respective pools. Safe to call even when neither was ever rented.
    /// </summary>
    private static void ReturnReorderBufferToPool(SynapseConnection synapseConnection)
    {
        lock (synapseConnection.ReliableLock)
        {
            foreach (ArraySegment<byte> segment in synapseConnection.ReorderBuffer.Values)
            {
                if (segment.Array is not null)
                    ArrayPool<byte>.Shared.Return(segment.Array);
            }

            synapseConnection.ReorderBuffer.Clear();
        }
    }

    /// <summary>
    /// Atomically detaches and returns the splitter and reassembler on <paramref name="synapseConnection"/>
    /// to their respective pools. Safe to call even when neither was ever rented.
    /// </summary>
    private static void ReturnConnectionSegmenters(SynapseConnection synapseConnection)
    {
        PacketSplitter? splitter = Interlocked.Exchange(ref synapseConnection.Splitter, null);
        if (splitter is not null)
            ResettableObjectPool<PacketSplitter>.Return(splitter);

        PacketReassembler? reassembler = Interlocked.Exchange(ref synapseConnection.Reassembler, null);
        if (reassembler is not null)
            ResettableObjectPool<PacketReassembler>.Return(reassembler);
    }

    /// <summary>
    /// Ingress callback: wraps the delivered payload in a pooled <see cref="PacketReceivedEventArgs"/>
    /// and raises <see cref="PacketReceived"/>. Returns the payload buffer to the pool in the finally block.
    /// </summary>
    private void OnPayloadDelivered(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable)
    {
        PacketReceivedEventArgs packetReceivedEventArgs = new(synapseConnection, payload, isReliable);

        try
        {
            PacketReceived?.Invoke(packetReceivedEventArgs);
        }
        finally
        {
            if (payload.Array is not null)
                ArrayPool<byte>.Shared.Return(payload.Array);
        }
    }

    /// <summary>
    /// Ingress callback: raises <see cref="ConnectionEstablished"/> via a pooled <see cref="ConnectionEventArgs"/>.
    /// </summary>
    private void OnConnectionEstablishedInternal(SynapseConnection synapseConnection)
    {
        ConnectionEventArgs connectionEventArgs = ResettableObjectPool<ConnectionEventArgs>.Rent();
        connectionEventArgs.Initialize(synapseConnection);

        try
        {
            ConnectionEstablished?.Invoke(connectionEventArgs);
        }
        finally
        {
            ResettableObjectPool<ConnectionEventArgs>.Return(connectionEventArgs);
        }
    }

    /// <summary>
    /// Ingress callback: raises <see cref="ConnectionClosed"/> via a pooled <see cref="ConnectionEventArgs"/>.
    /// </summary>
    private void OnConnectionClosedInternal(SynapseConnection synapseConnection)
    {
        ConnectionEventArgs connectionEventArgs = ResettableObjectPool<ConnectionEventArgs>.Rent();
        connectionEventArgs.Initialize(synapseConnection);

        try
        {
            ConnectionClosed?.Invoke(connectionEventArgs);
        }
        finally
        {
            ResettableObjectPool<ConnectionEventArgs>.Return(connectionEventArgs);
        }
    }

    /// <summary>
    /// Raises <see cref="PacketSent"/> via a pooled <see cref="PacketSentEventArgs"/>.
    /// </summary>
    private void RaisePacketSent(IPEndPoint endPoint, ArraySegment<byte> payload, bool isReliable)
    {
        PacketSentEventArgs packetSentEventArgs = ResettableObjectPool<PacketSentEventArgs>.Rent();
        packetSentEventArgs.Initialize(endPoint, payload, isReliable);

        try
        {
            PacketSent?.Invoke(packetSentEventArgs);
        }
        finally
        {
            ResettableObjectPool<PacketSentEventArgs>.Return(packetSentEventArgs);
        }
    }

    /// <summary>
    /// Raises <see cref="ConnectionClosed"/> via a pooled <see cref="ConnectionEventArgs"/>.
    /// </summary>
    private void RaiseConnectionClosed(SynapseConnection synapseConnection)
    {
        ConnectionEventArgs connectionEventArgs = ResettableObjectPool<ConnectionEventArgs>.Rent();
        connectionEventArgs.Initialize(synapseConnection);

        try
        {
            ConnectionClosed?.Invoke(connectionEventArgs);
        }
        finally
        {
            ResettableObjectPool<ConnectionEventArgs>.Return(connectionEventArgs);
        }
    }

    /// <summary>
    /// Raises <see cref="ConnectionFailed"/> via a pooled <see cref="ConnectionFailedEventArgs"/>.
    /// </summary>
    private void RaiseConnectionFailed(IPEndPoint? endPoint, ConnectionRejectedReason connectionRejectedReason, string? message)
    {
        ConnectionFailedEventArgs connectionFailedEventArgs = ResettableObjectPool<ConnectionFailedEventArgs>.Rent();
        connectionFailedEventArgs.Initialize(endPoint, connectionRejectedReason, message);

        try
        {
            ConnectionFailed?.Invoke(connectionFailedEventArgs);
        }
        finally
        {
            ResettableObjectPool<ConnectionFailedEventArgs>.Return(connectionFailedEventArgs);
        }
    }

    /// <summary>
    /// Removes the connection for <paramref name="endPoint"/>, returns pooled resources, and optionally blacklists the computed signature.
    /// </summary>
    private void DisconnectAndBlacklist(IPEndPoint endPoint, bool canBlacklist)
    {
        if (Connections.Remove(endPoint, out SynapseConnection? synapseConnection) && synapseConnection is not null)
        {
            ReturnConnectionSegmenters(synapseConnection);
            ReturnReorderBufferToPool(synapseConnection);
            SynapseConnection.DrainPendingReliableQueue(synapseConnection);
            synapseConnection.State = ConnectionState.Disconnected;
            RaiseConnectionClosed(synapseConnection);
        }

        if (canBlacklist)
        {
            ulong signature = Security.ComputeSignature(endPoint, ReadOnlySpan<byte>.Empty);
            Security.AddToBlacklist(signature);
        }
    }
}