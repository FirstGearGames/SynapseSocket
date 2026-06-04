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
public sealed partial class SynapseManager : IDisposable
{
    /// <summary>
    /// Raised when a payload is received from any connection.
    /// </summary>
    public event PacketReceivedHandler? PacketReceived;
    /// <summary>
    /// Raised after a packet has been transmitted on the wire.
    /// </summary>
    public event PacketSentHandler? PacketSent;
    /// <summary>
    /// Raised when a new connection is established.
    /// </summary>
    public event ConnectionEstablishedHandler? ConnectionEstablished;
    /// <summary>
    /// Raised when a connection terminates (timeout, peer-disconnect, etc.).
    /// </summary>
    public event ConnectionClosedHandler? ConnectionClosed;
    /// <summary>
    /// Raised on any binding, signature, or validation failure.
    /// </summary>
    public event ConnectionFailedHandler? ConnectionFailed;
    /// <summary>
    /// Raised when the engine detects a violation (oversized packet, rate limit breach, malformed data, or rejected signature).
    /// Handlers may override <see cref="ViolationEventArgs.Action"/> to customize how the engine responds.
    /// When no handler is subscribed, the default action (<see cref="ViolationAction.KickAndBlacklist"/>) is applied.
    /// <para>
    /// <b>Warning:</b> do not unconditionally downgrade <see cref="ViolationEventArgs.Action"/> inside a handler.
    /// Setting the action to <see cref="ViolationAction.Ignore"/> suppresses every protective measure the engine would otherwise take. See <see cref="ViolationEventArgs.Action"/> for details.
    /// </para>
    /// </summary>
    public event ViolationHandler? ViolationDetected;
    /// <summary>
    /// Raised when an unexpected exception escapes a background loop (ingress or maintenance).
    /// Subscribe to route engine errors into your logging system (e.g., Unity's Debug.LogException).
    /// The loop that raised the exception continues running after the handler returns.
    /// If no handler is subscribed the exception is silently discarded.
    /// </summary>
    public event UnhandledExceptionHandler? UnhandledException;
    /// <summary>
    /// Raised when the ingress path receives a datagram whose leading type byte is not a recognised
    /// Synapse <see cref="SynapseSocket.Packets.PacketType"/> and
    /// <see cref="SynapseSocket.Core.Configuration.SecurityConfig.AllowUnknownPackets"/> is true.
    /// Enables external protocols (e.g. a rendezvous/beacon client) to piggyback on the UDP socket
    /// so the NAT mapping opened by talking to the external service is the same mapping used for P2P traffic.
    /// <para>
    /// The handler must return <see cref="SynapseSocket.Security.FilterResult.Allowed"/> to accept the
    /// packet. Any other value raises a <see cref="SynapseSocket.Core.Events.ViolationReason.UnknownPacket"/>
    /// violation and takes the default action (<see cref="SynapseSocket.Core.Events.ViolationAction.KickAndBlacklist"/>),
    /// which subscribers may override via <see cref="ViolationDetected"/>.
    /// </para>
    /// <para>
    /// The packet bytes reference the internal receive buffer and are only valid for the duration
    /// of the callback. Copy anything the handler needs to retain.
    /// </para>
    /// </summary>
    public event UnknownPacketReceivedHandler? UnknownPacketReceived;
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
    /// <summary>
    /// True after <see cref="StartAsync"/> completes; false after <see cref="StopAsync"/> or disposal.
    /// </summary>
    private bool _isStarted;
    /// <summary>
    /// True after <see cref="Dispose"/> or <see cref="DisposeAsync"/> is called. Guards against double-dispose.
    /// </summary>
    private bool _isDisposed;
    /// <summary>
    /// Optional latency simulator applied to all outbound packets. Configured from <see cref="SynapseConfig.LatencySimulator"/>.
    /// </summary>
    private readonly LatencySimulator _latencySimulator;
    /// <summary>
    /// True when reliable or unreliable segmentation is enabled; controls the segmented send path.
    /// </summary>
    private readonly bool _isSegmentingEnabled;
    /// <summary>
    /// Maximum payload bytes that fit in a single unsegmented packet, derived from MTU minus header overhead.
    /// </summary>
    private readonly int _maximumUnsegmentedPayload;
    /// <summary>
    /// Bound UDP sockets, one per configured endpoint. Shared with the ingress engines.
    /// </summary>
    private readonly List<Socket> _sockets = [];
    /// <summary>
    /// Ingress engines, one per socket. Each is drained on every <see cref="Poll"/>.
    /// </summary>
    private readonly List<IngressEngine> _ingressEngines = [];
    /// <summary>
    /// Shared outbound engine used by all send paths and maintenance.
    /// Null until <see cref="Start"/> binds sockets.
    /// </summary>
    private TransmissionEngine? _transmissionEngine;

    /// <summary>
    /// Creates a new SynapseSocket engine from the supplied configuration.
    /// Call <see cref="StartAsync"/> to begin binding and receiving.
    /// </summary>
    public SynapseManager(SynapseConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));

        if (Config.BindEndPoints.Count == 0)
            throw new ArgumentException("At least one bind endpoint is required.", nameof(config));

        if (Config.Segment.AssemblyTimeoutMilliseconds is > 0 and > 300_000)
            throw new ArgumentOutOfRangeException(nameof(config), "Segment.AssemblyTimeoutMilliseconds must not exceed 300000 (5 minutes).");

        ISignatureProvider signatureProvider = Config.Security.SignatureProvider ?? new DefaultSignatureProvider();
        Security = new(signatureProvider, Config.Security.MaximumPacketsPerSecond, Config.Security.MaximumBytesPerSecond, Config.MaximumPacketSize, Config.Security.Enabled);
        Connections = new();
        Telemetry = new(Config.EnableTelemetry);
        _latencySimulator = new(Config.LatencySimulator);
        _isSegmentingEnabled = Config.Segment.ReliableEnabled || Config.Segment.UnreliableMode != UnreliableSegmentMode.Disabled;
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
        /* Value is unset if segmenting is not enabled or if
         * a timeout is unset. */
        uint segmentAssemblyTimeoutMilliseconds = config.Segment.AssemblyTimeoutMilliseconds;
        _segmentAssemblyTimeoutTicks = _isSegmentingEnabled && segmentAssemblyTimeoutMilliseconds != SegmentConfig.DisabledAssemblyTimeout ? TimeSpan.FromMilliseconds(segmentAssemblyTimeoutMilliseconds).Ticks : UnsetSegmentAssemblyTimeoutTicks;
        _maximumPacketsPerSecond = Config.Security.MaximumPacketsPerSecond;
        _maximumBytesPerSecond = Config.Security.MaximumBytesPerSecond;
    }

    /// <summary>
    /// Binds all configured endpoints and prepares the ingress engines. After this returns the host must call
    /// <see cref="Poll"/> regularly (e.g. once per frame) to receive datagrams and run maintenance — the engine
    /// spawns no background threads.
    /// </summary>
    public void Start()
    {
        if (_isStarted)
            throw new InvalidOperationException("Engine is already running.");

        if (_isDisposed)
            throw new ObjectDisposedException(nameof(SynapseManager));

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

                // Raise kernel UDP buffers before bind. The OS default (8–64 KiB on Windows) is too small
                // for bursty loopback traffic with many concurrent peers and causes silent datagram drops.
                if (Config.SocketReceiveBufferBytes != SynapseConfig.DisabledSocketBufferOverride)
                    socket.ReceiveBufferSize = Config.SocketReceiveBufferBytes;
                if (Config.SocketSendBufferBytes != SynapseConfig.DisabledSocketBufferOverride)
                    socket.SendBufferSize = Config.SocketSendBufferBytes;

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
            ingressEngine.UnknownPacketReceived += OnUnknownPacketReceivedInternal;

            ingressEngine.Start();
            _ingressEngines.Add(ingressEngine);
        }

        _isStarted = true;
    }

    /// <summary>
    /// Pumps the engine: receives and dispatches all buffered datagrams, advances NAT hole-punching, runs
    /// maintenance (keep-alive, timeout, reliable retransmit, segment timeout), and flushes batched ACKs.
    /// The host must call this regularly (e.g. once per frame) on the same thread it uses for sends. Received
    /// payloads are delivered via the <see cref="PacketReceived"/> event synchronously during this call.
    /// </summary>
    public void Poll()
    {
        if (!_isStarted || _isDisposed || _transmissionEngine is null)
            return;

        long nowTicks = DateTime.UtcNow.Ticks;

        // 1. Receive: drain each socket, processing and delivering inline on this thread.
        for (int i = 0; i < _ingressEngines.Count; i++)
            _ingressEngines[i].Drain();

        // 2. Advance NAT hole-punch state machines for any pending FullCone connects.
        AdvanceNatPunches(nowTicks);

        // 3. Maintenance: keep-alive, timeout, reliable retransmit, segment-assembly timeout, rate-counter reset.
        RunMaintenance(nowTicks);

        // 4. Flush batched outbound ACKs.
        if (_isAckBatchingEnabled)
            FlushPendingAcks();

        // 5. Release any latency-simulator-delayed packets whose due time has elapsed.
        _transmissionEngine.FlushDeferredSends(nowTicks);
    }

    /// <summary>
    /// Gracefully stops the engine: tears down connections and closes all sockets.
    /// The engine may be restarted by calling <see cref="Start"/> again after this returns.
    /// </summary>
    public void Stop()
    {
        if (!_isStarted || _isDisposed)
            return;

        _isStarted = false;
        ShutdownCore();
    }

    /// <summary>
    /// Initiates an outgoing connection to the specified remote endpoint.
    /// Sends a handshake packet; the connection is considered established when the remote handshake response arrives
    /// (observed on a subsequent <see cref="Poll"/>).
    /// </summary>
    public SynapseConnection Connect(IPEndPoint endPoint)
    {
        EnsureRunning();

        ulong signature = Security.ComputeSignature(endPoint, ReadOnlySpan<byte>.Empty);

        if (Security.IsBlacklisted(signature))
        {
            RaiseConnectionFailed(endPoint, ConnectionRejectedReason.Blacklisted, null);
            throw new InvalidOperationException("Remote endpoint is blacklisted.");
        }

        SynapseConnection synapseConnection = Connections.CreateNew(endPoint, signature);

        _transmissionEngine!.SendHandshake(endPoint);

        if (Config.NatTraversal.Mode == NatTraversalMode.FullCone)
            RegisterNatPunch(synapseConnection, endPoint);

        return synapseConnection;
    }

    /// <summary>
    /// Sends an unreliable payload on the given connection.
    /// When the payload exceeds the MTU, behaviour is controlled by <see cref="SegmentConfig.UnreliableMode"/>:
    /// <list type="bullet">
    /// <item><see cref="UnreliableSegmentMode.Disabled"/> — throws.</item>
    /// <item><see cref="UnreliableSegmentMode.SegmentUnreliable"/> — splits into unreliable segments (default).</item>
    /// <item><see cref="UnreliableSegmentMode.SegmentReliable"/> — splits into reliable segments.</item>
    /// </list>
    /// </summary>
    public void Send(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable)
    {
        EnsureRunning();

        if (payload.Count <= _maximumUnsegmentedPayload)
        {
            if (isReliable)
                _transmissionEngine!.SendReliableUnsegmented(synapseConnection, payload);
            else
                _transmissionEngine!.SendUnreliableUnsegmented(synapseConnection, payload);

            RaisePacketSent(synapseConnection.RemoteEndPoint, payload, isReliable);

            return;
        }

        /* If here, the packet must be segmented. */

        // Segmenting is disabled entirely.
        if (!_isSegmentingEnabled)
            throw new InvalidOperationException($"Payload ({payload.Count} bytes) exceeds the MTU-based limit ({_maximumUnsegmentedPayload} bytes). Enable segmentation via Segment.ReliableEnabled or Segment.UnreliableMode.");

        // An additional check on segmentation is required for unreliable sending.
        if (!isReliable)
        {
            UnreliableSegmentMode unreliableSegmentMode = Config.Segment.UnreliableMode;

            if (unreliableSegmentMode is UnreliableSegmentMode.Disabled)
                throw new InvalidOperationException($"Unreliable payload ({payload.Count} bytes) exceeds the MTU-based limit ({_maximumUnsegmentedPayload} bytes). Set Segment.UnreliableMode or reduce payload size.");

            // Make reliable if the unreliableSegmentMode permits.
            isReliable = unreliableSegmentMode is UnreliableSegmentMode.SegmentReliable;
        }

        _transmissionEngine!.SendSegmented(synapseConnection, payload, isReliable, GetOrRentSplitter(synapseConnection));
        RaisePacketSent(synapseConnection.RemoteEndPoint, payload, isReliable);
    }

    /// <summary>
    /// Sends arbitrary bytes directly to the given endpoint over the engine's UDP socket,
    /// bypassing Synapse's connection, handshake, and packet framing. Intended for external
    /// protocols (e.g. a rendezvous/beacon client) that piggyback on the socket so their traffic
    /// shares the same NAT mapping as Synapse's peer-to-peer traffic.
    /// <para>
    /// External protocols must use a leading byte strictly greater than
    /// <see cref="SynapseSocket.Packets.PacketType.NatChallenge"/> so the ingress path can
    /// distinguish their packets from Synapse packets and route them through
    /// <see cref="UnknownPacketReceived"/>.
    /// </para>
    /// </summary>
    /// <param name="target">The remote endpoint to send to.</param>
    /// <param name="data">The wire-ready bytes to send.</param>
    public void SendRaw(IPEndPoint target, ArraySegment<byte> data)
    {
        EnsureRunning();
        _transmissionEngine!.SendRaw(data, target);
    }

    /// <summary>
    /// Gracefully disconnects a connection, notifying the peer.
    /// </summary>
    public void Disconnect(SynapseConnection synapseConnection)
    {
        if (_transmissionEngine is not null)
            _transmissionEngine.SendDisconnect(synapseConnection);

        ReturnConnectionSegmenters(synapseConnection);
        ReturnReorderBufferToPool(synapseConnection);
        SynapseConnection.DrainPendingReliableQueue(synapseConnection);
        synapseConnection.State = ConnectionState.Disconnected;
        Connections.Remove(synapseConnection.RemoteEndPoint, out _);

        RaiseConnectionClosed(synapseConnection);
    }

    /// <summary>
    /// Central violation handler.
    /// Constructs a <see cref="ViolationEventArgs"/> from the supplied parameters, invokes
    /// <see cref="ViolationDetected"/> (if subscribed) to obtain the desired <see cref="ViolationAction"/>,
    /// and applies that action. Falls back to <paramref name="initialAction"/> when no subscriber is attached.
    /// </summary>
    internal void HandleViolation(IPEndPoint endPoint, ulong signature, ViolationReason violationReason, int packetSize, string? details, ViolationAction initialAction = ViolationAction.KickAndBlacklist)
    {
        Connections.ConnectionsByEndPoint.TryGetValue(endPoint, out SynapseConnection? synapseConnection);

        ViolationEventArgs violationEventArgs = new(endPoint, signature, violationReason, synapseConnection, packetSize, details, initialAction);
        ViolationAction returnedViolationAction = initialAction;

        try
        {
            try
            {
                returnedViolationAction = ViolationDetected?.Invoke(violationEventArgs) ?? initialAction;
            }
            catch
            {
                /* never let a listener crash the ingress path */
            }

            switch (returnedViolationAction)
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
        catch { }
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

        ShutdownCore();
    }

    /// <summary>
    /// Closes sockets, stops the ingress engines, tears down all connections, and returns pooled resources.
    /// Shared by <see cref="Stop"/> and <see cref="Dispose"/>.
    /// </summary>
    private void ShutdownCore()
    {
        CloseSockets();

        for (int i = 0; i < _ingressEngines.Count; i++)
            _ingressEngines[i].Stop();
        _ingressEngines.Clear();

        _transmissionEngine?.ClearDeferredSends();

        TeardownAllConnections();
        _natPunches.Clear();
    }

    /// <summary>
    /// Frees every live connection's pooled buffers (reliable queue, reorder buffer, segmenters) and clears the
    /// connection tables. Safe because the engine is single-threaded and stopped.
    /// </summary>
    private void TeardownAllConnections()
    {
        IReadOnlyList<SynapseConnection> connections = Connections.Connections;

        for (int i = connections.Count - 1; i >= 0; i--)
        {
            SynapseConnection connection = connections[i];
            ReturnConnectionSegmenters(connection);
            ReturnReorderBufferToPool(connection);
            SynapseConnection.DrainPendingReliableQueue(connection);
            connection.State = ConnectionState.Disconnected;
        }

        Connections.Clear();
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
    /// Forwards an unknown packet received on the ingress path to the <see cref="UnknownPacketReceived"/> event
    /// and returns the delegate's <see cref="SynapseSocket.Security.FilterResult"/> to the ingress path.
    /// Returns <see cref="SynapseSocket.Security.FilterResult.Allowed"/> when no subscribers are attached or
    /// when a subscriber throws, so listener exceptions cannot crash the ingress loop.
    /// </summary>
    private FilterResult OnUnknownPacketReceivedInternal(IPEndPoint fromEndPoint, ArraySegment<byte> packet)
    {
        try
        {
            return UnknownPacketReceived?.Invoke(fromEndPoint, packet) ?? FilterResult.Allowed;
        }
        catch (Exception listenerException)
        {
            UnhandledException?.Invoke(listenerException);
            return FilterResult.Allowed;
        }
    }

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
        uint effectiveMax = Config.Segment.MaximumSegments == 0 ? 255u : Config.Segment.MaximumSegments;
        rented.Initialize(Config.MaximumTransmissionUnit, effectiveMax);

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
        foreach (ArraySegment<byte> segment in synapseConnection.ReorderBuffer.Values)
        {
            if (segment.Array is not null)
                ArrayPool<byte>.Shared.Return(segment.Array);
        }

        synapseConnection.ReorderBuffer.Clear();
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
        ConnectionEventArgs connectionEventArgs = new(synapseConnection);

        try
        {
            ConnectionEstablished?.Invoke(connectionEventArgs);
        }
        catch { }
    }

    /// <summary>
    /// Ingress callback: raises <see cref="ConnectionClosed"/> via a pooled <see cref="ConnectionEventArgs"/>.
    /// </summary>
    private void OnConnectionClosedInternal(SynapseConnection synapseConnection)
    {
        ConnectionEventArgs connectionEventArgs = new(synapseConnection);

        try
        {
            ConnectionClosed?.Invoke(connectionEventArgs);
        }
        catch { }
    }

    /// <summary>
    /// Raises <see cref="PacketSent"/> via a pooled <see cref="PacketSentEventArgs"/>.
    /// </summary>
    private void RaisePacketSent(IPEndPoint endPoint, ArraySegment<byte> payload, bool isReliable)
    {
        PacketSentEventArgs packetSentEventArgs = new(endPoint, payload, isReliable);

        try
        {
            PacketSent?.Invoke(packetSentEventArgs);
        }
        catch { }
    }

    /// <summary>
    /// Raises <see cref="ConnectionClosed"/> via a pooled <see cref="ConnectionEventArgs"/>.
    /// </summary>
    private void RaiseConnectionClosed(SynapseConnection synapseConnection)
    {
        ConnectionEventArgs connectionEventArgs = new(synapseConnection);

        try
        {
            ConnectionClosed?.Invoke(connectionEventArgs);
        }
        catch { }
    }

    /// <summary>
    /// Raises <see cref="ConnectionFailed"/> via a pooled <see cref="ConnectionFailedEventArgs"/>.
    /// </summary>
    private void RaiseConnectionFailed(IPEndPoint? endPoint, ConnectionRejectedReason connectionRejectedReason, string? message)
    {
        ConnectionFailedEventArgs connectionFailedEventArgs = new(endPoint, connectionRejectedReason, message);

        try
        {
            ConnectionFailed?.Invoke(connectionFailedEventArgs);
        }
        catch { }
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