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
/// This is a partial class; the core API lives here, and the maintenance work (keep-alive, reliable retransmission) driven from <see cref="Poll"/> lives in <c>SynapseManager.Maintenance.cs</c>.
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
    /// Raised when an unexpected exception escapes the engine's own work during <see cref="Poll"/>, or escapes one of
    /// the application's event handlers that the engine invokes.
    /// Subscribe to route engine errors into your logging system (e.g., Unity's Debug.LogException).
    /// The poll continues after the handler returns; a single failure does not abort the rest of the frame.
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
    /// The effective maximum transmission unit the engine packs wire packets against, which is
    /// <see cref="SynapseConfig.MaximumTransmissionUnit"/> less the
    /// <see cref="IPacketTransform.ReservedBytes"/> of a configured <see cref="SynapseConfig.PacketTransform"/>.
    /// Reserving that headroom up front is what keeps a transformed datagram inside the configured MTU on the wire.
    /// </summary>
    public uint MaximumTransmissionUnit { get; }
    /// <summary>
    /// Maximum payload bytes that fit in a single unsegmented packet, derived from
    /// <see cref="MaximumTransmissionUnit"/> minus header overhead.
    /// Larger payloads are segmented, or rejected when segmentation is disabled.
    /// </summary>
    public int MaximumPayloadSize { get; }
    /// <summary>
    /// The endpoints the engine actually bound, in the order the sockets were bound. The list is empty until
    /// <see cref="Start"/> has bound at least one endpoint, and is emptied again when the engine stops or is disposed.
    /// </summary>
    /// <remarks>
    /// These are the endpoints read back from the bound sockets, not the ones <see cref="SynapseConfig.BindEndPoints"/>
    /// asked for. A configuration that requests port zero leaves the operating system to choose an ephemeral port, and
    /// this property is how a caller learns which port that turned out to be.
    /// </remarks>
    public IReadOnlyList<IPEndPoint> BoundEndPoints => _boundEndPoints;
    /// <summary>
    /// True if <see cref="Start"/> has completed successfully and the engine has not been stopped or disposed.
    /// </summary>
    public bool IsRunning => _isStarted && !_isDisposed;
    /// <summary>
    /// True after <see cref="Start"/> completes; false after <see cref="Stop"/> or disposal.
    /// </summary>
    private bool _isStarted;
    /// <summary>
    /// True after <see cref="Dispose"/> is called. Guards against double-dispose.
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
    /// Bound UDP sockets, one per configured endpoint. Shared with the ingress engines.
    /// </summary>
    private readonly List<Socket> _sockets = [];
    /// <summary>
    /// The endpoint the socket at the same index within <see cref="_sockets"/> actually bound.
    /// </summary>
    /// <remarks>
    /// Each entry is read back from the socket after the bind succeeds rather than copied from
    /// <see cref="SynapseConfig.BindEndPoints"/>, because a configuration commonly asks for port zero and the
    /// port the operating system chose is only knowable once the socket is bound.
    /// </remarks>
    private readonly List<IPEndPoint> _boundEndPoints = [];
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
    /// Connections torn down since the last release, awaiting the release of their pooled buffers once no engine frame
    /// can still be holding them. Drained at the end of every poll.
    /// </summary>
    private readonly List<SynapseConnection> _pendingReleases = [];
    /// <summary>
    /// Raw sends queued from other threads by <see cref="EnqueueRaw"/>, drained on the engine thread during
    /// <see cref="Poll"/>.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<QueuedRawSend> _queuedRawSends = new();

    /// <summary>
    /// Ceiling on handshake replay-cache entries per ingress engine. The key mixes the peer-supplied nonce, so
    /// without a cap a single peer mints one entry per handshake it sends.
    /// </summary>
    internal const int MaximumReplayCacheEntries = 8192;

    /// <summary>
    /// Total handshake replay-cache entries across every ingress engine. Diagnostic surface.
    /// </summary>
    internal int ReplayCacheCount
    {
        get
        {
            int total = 0;

            for (int i = 0; i < _ingressEngines.Count; i++)
                total += _ingressEngines[i].ReplayCacheCount;

            return total;
        }
    }

    /// <summary>
    /// Creates a new SynapseSocket engine from the supplied configuration.
    /// Call <see cref="Start"/> to begin binding and receiving.
    /// </summary>
    public SynapseManager(SynapseConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));

        if (Config.BindEndPoints.Count == 0)
            throw new ArgumentException("At least one bind endpoint is required.", nameof(config));

        if (Config.Segment.AssemblyTimeoutMilliseconds > 300_000)
            throw new ArgumentOutOfRangeException(nameof(config), "Segment.AssemblyTimeoutMilliseconds must not exceed 300000 (5 minutes).");

        uint handshakeTimeoutMilliseconds = Config.Connection.HandshakeTimeoutMilliseconds;

        if (handshakeTimeoutMilliseconds is not ConnectionConfig.UnsetHandshakeTimeoutMilliseconds && Config.NatTraversal.Mode is NatTraversalMode.FullCone)
        {
            NatTraversalConfig natTraversalConfig = Config.NatTraversal;
            /* A punch declares NatTraversalFailed one interval after its last attempt. A handshake timeout short of that
             * tears the pending connection down mid-punch, so the later attempts never run and the failure is never raised. */
            ulong punchScheduleMilliseconds = natTraversalConfig.FullCone.DirectAttemptMilliseconds + (ulong)natTraversalConfig.MaximumAttempts * natTraversalConfig.IntervalMilliseconds;

            if (handshakeTimeoutMilliseconds < punchScheduleMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(config), $"Connection.HandshakeTimeoutMilliseconds [{handshakeTimeoutMilliseconds}] ends before the full-cone hole-punch schedule of [{punchScheduleMilliseconds}] milliseconds can finish. It must be at least NatTraversal.FullCone.DirectAttemptMilliseconds plus NatTraversal.MaximumAttempts times NatTraversal.IntervalMilliseconds.");
        }

        uint reservedBytes = Config.PacketTransform?.ReservedBytes ?? 0;

        if (reservedBytes + PacketHeader.MaxHeaderSize >= Config.MaximumTransmissionUnit)
            throw new ArgumentOutOfRangeException(nameof(config), $"PacketTransform.ReservedBytes ({reservedBytes}) leaves no room under MaximumTransmissionUnit ({Config.MaximumTransmissionUnit}) for a packet header ({PacketHeader.MaxHeaderSize} bytes).");

        MaximumTransmissionUnit = Config.MaximumTransmissionUnit - reservedBytes;

        ISignatureProvider signatureProvider = Config.Security.SignatureProvider ?? new DefaultSignatureProvider();
        Security = new(signatureProvider, Config.Security.MaximumPacketsPerSecond, Config.Security.MaximumBytesPerSecond, Config.MaximumPacketSize, Config.Security.Enabled,
            Config.Security.ViolationsBeforeBlacklist, Config.Security.ViolationWindowMilliseconds, Config.Security.BlacklistDurationMilliseconds);
        Connections = new();
        Telemetry = new(Config.EnableTelemetry);
        _latencySimulator = new(Config.LatencySimulator);
        _isSegmentingEnabled = Config.Segment.ReliableEnabled || Config.Segment.UnreliableMode != UnreliableSegmentMode.Disabled;
        /* Unreliable requires a couple bytes less for segmenting when being sent out
         * of order, which would require different maximum payload sizes between reliable
         * and unreliable segmented. Rather than add additional complexity and branching
         * the rare byte cost is consumed. */
        MaximumPayloadSize = (int)MaximumTransmissionUnit - PacketHeader.TypeSize - PacketHeader.SequenceSize;

        /* Maintenance. */
        _connectionKeepAliveTicks = TimeSpan.FromMilliseconds(Config.Connection.KeepAliveIntervalMilliseconds).Ticks;
        _connectionTimeoutTicks = TimeSpan.FromMilliseconds(Config.Connection.TimeoutMilliseconds).Ticks;
        _handshakeTimeoutTicks = handshakeTimeoutMilliseconds is ConnectionConfig.UnsetHandshakeTimeoutMilliseconds ? _connectionTimeoutTicks : TimeSpan.FromMilliseconds(handshakeTimeoutMilliseconds).Ticks;
        _handshakeRetryIntervalTicks = TimeSpan.FromMilliseconds(Config.Connection.HandshakeRetryIntervalMilliseconds).Ticks;
        _handshakeMaximumAttempts = Config.Connection.HandshakeMaximumAttempts;
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
    /// <see cref="Poll"/> regularly (e.g. once per frame) to receive datagrams and run maintenance, the engine
    /// spawns no background threads.
    /// </summary>
    public void Start()
    {
        if (_isStarted)
            throw new InvalidOperationException("Engine is already running.");

        if (_isDisposed)
            throw new ObjectDisposedException(nameof(SynapseManager));

        /* One TransmissionEngine serves every bound socket and selects its outbound socket purely by address
         * family, so two endpoints of the same family leave it pointing at whichever bound last. Every reply
         * (handshake acks, ACKs, keep-alives, payloads) would then leave through the wrong socket, and peers that
         * connected via the other endpoint would see replies from an unexpected source address. Fail loudly
         * rather than misroute silently. */
        HashSet<AddressFamily> boundFamilies = [];

        foreach (IPEndPoint configuredEndPoint in Config.BindEndPoints)
            if (!boundFamilies.Add(configuredEndPoint.AddressFamily))
                throw new InvalidOperationException($"Multiple bind endpoints share the address family {configuredEndPoint.AddressFamily}. Bind at most one endpoint per address family, or run a separate SynapseManager per endpoint.");

        Socket? ipv4Socket = null;
        Socket? ipv6Socket = null;

        foreach (IPEndPoint bindEndPoint in Config.BindEndPoints)
        {
            Socket? socket = null;
            try
            {
                socket = new(bindEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

                if (bindEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);

                // Raise kernel UDP buffers before bind. The OS default (8 to 64 KiB on Windows) is too small
                // for bursty loopback traffic with many concurrent peers and causes silent datagram drops.
                if (Config.SocketReceiveBufferBytes != SynapseConfig.DisabledSocketBufferOverride)
                    socket.ReceiveBufferSize = Config.SocketReceiveBufferBytes;
                if (Config.SocketSendBufferBytes != SynapseConfig.DisabledSocketBufferOverride)
                    socket.SendBufferSize = Config.SocketSendBufferBytes;

                socket.Bind(bindEndPoint);
            }
            catch (SocketException socketException)
            {
                // The socket was constructed before the failure, so its OS handle is live until disposed.
                socket?.Dispose();
                RaiseConnectionFailed(bindEndPoint, ConnectionRejectedReason.BindFailed, socketException.Message);

                continue;
            }

            _sockets.Add(socket);
            // Read the endpoint back from the socket; a bind endpoint of port zero does not name the bound port.
            _boundEndPoints.Add((IPEndPoint)socket.LocalEndPoint!);

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
            ingressEngine.TeardownRequested += TeardownConnection;
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

        long nowTicks = Clock.Ticks;

        // 1. Receive: drain each socket, processing and delivering inline on this thread.
        for (int i = 0; i < _ingressEngines.Count; i++)
            _ingressEngines[i].Drain(nowTicks);

        // 1b. Per-engine upkeep: replay-cache and NAT probe-table sweeps, moved off the receive path.
        for (int i = 0; i < _ingressEngines.Count; i++)
            _ingressEngines[i].RunMaintenance(nowTicks);

        // 2. Advance NAT hole-punch state machines for any pending FullCone connects.
        AdvanceNatPunches(nowTicks);

        // 3. Maintenance: keep-alive, timeout, reliable retransmit, segment-assembly timeout, rate-counter reset.
        RunMaintenance(nowTicks);

        // 4. Flush batched outbound ACKs.
        if (_isAckBatchingEnabled)
            FlushPendingAcks();

        // 4b. Send anything handed over from other threads, on this thread.
        FlushQueuedRawSends();

        // 5. Release any latency-simulator-delayed packets whose due time has elapsed.
        _transmissionEngine.FlushDeferredSends(nowTicks);

        // 6. Release the buffers of connections torn down since the last poll. Deferred to here so that no engine frame,
        //    including a user handler that disconnected re-entrantly from inside PacketReceived, is still using them.
        ReleaseTornDownConnections();
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

        if (Config.ConnectedSocketEnabled)
            ConnectSocketToRemote(endPoint);

        // Tear the previous session down through the one teardown path rather than letting CreateNew drop it.
        if (Connections.ConnectionsByEndPoint.TryGetValue(endPoint, out SynapseConnection? previousConnection))
            TeardownConnection(previousConnection);

        SynapseConnection synapseConnection = Connections.CreateNew(endPoint, signature);

        _transmissionEngine!.SendHandshake(endPoint);
        // Stamped so the peer's answering handshake is recognised as an answer rather than a session-resetting request.
        synapseConnection.LastHandshakeSentTicks = Clock.Ticks;

        if (Config.NatTraversal.Mode == NatTraversalMode.FullCone)
            RegisterNatPunch(synapseConnection, endPoint);

        return synapseConnection;
    }

    /// <summary>
    /// Sends an unreliable payload on the given connection.
    /// When the payload exceeds the MTU, behaviour is controlled by <see cref="SegmentConfig.UnreliableMode"/>:
    /// <list type="bullet">
    /// <item><see cref="UnreliableSegmentMode.Disabled"/>: throws.</item>
    /// <item><see cref="UnreliableSegmentMode.SegmentUnreliable"/>: splits into unreliable segments (default).</item>
    /// <item><see cref="UnreliableSegmentMode.SegmentReliable"/>: splits into reliable segments.</item>
    /// </list>
    /// Throws <see cref="InvalidOperationException"/> for a connection that has been closed.
    /// </summary>
    public void Send(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable)
    {
        EnsureRunning();

        /* A closed connection is never reused, so a reference to one can only be stale. Sending on it would park
         * reliable buffers on a dead object and put datagrams on the wire for a session that no longer exists. */
        if (synapseConnection.IsTornDown)
            throw new InvalidOperationException("Cannot send on a connection that has been closed.");

        if (payload.Count <= MaximumPayloadSize)
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
            throw new InvalidOperationException($"Payload ({payload.Count} bytes) exceeds the MTU-based limit ({MaximumPayloadSize} bytes). Enable segmentation via Segment.ReliableEnabled or Segment.UnreliableMode.");

        // An additional check on segmentation is required for unreliable sending.
        if (!isReliable)
        {
            UnreliableSegmentMode unreliableSegmentMode = Config.Segment.UnreliableMode;

            if (unreliableSegmentMode is UnreliableSegmentMode.Disabled)
                throw new InvalidOperationException($"Unreliable payload ({payload.Count} bytes) exceeds the MTU-based limit ({MaximumPayloadSize} bytes). Set Segment.UnreliableMode or reduce payload size.");

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
    /// Queues raw bytes to be sent from the engine thread on the next <see cref="Poll"/>.
    /// </summary>
    /// <param name="target">The remote endpoint to send to.</param>
    /// <param name="data">The wire-ready bytes; copied, so the caller may reuse its buffer immediately.</param>
    /// <remarks>
    /// Use this instead of <see cref="SendRaw"/> from any thread that is not the one calling <see cref="Poll"/>.
    /// The send path keeps unsynchronised per-engine state. The serialized-target cache and the latency
    /// simulator queue, on the assumption that only the poll thread touches it. A background timer or heartbeat
    /// calling SendRaw directly races that state, and a Dictionary resize interleaved with an insert can leave a
    /// cyclic bucket chain that spins the next lookup forever.
    /// </remarks>
    public void EnqueueRaw(IPEndPoint target, ArraySegment<byte> data)
    {
        if (data.Array is null || data.Count == 0)
            return;

        byte[] copy = ArrayPool<byte>.Shared.Rent(data.Count);
        Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);

        _queuedRawSends.Enqueue(new(copy, data.Count, target));
    }

    /// <summary>
    /// Gracefully disconnects a connection, notifying the peer. Does nothing for a connection that is already closed.
    /// </summary>
    public void Disconnect(SynapseConnection synapseConnection)
    {
        /* Already closed: nothing to do. Sending the disconnect packet anyway would be worse than redundant, because the
         * peer may have a new session with this endpoint by now, and the packet would end that one instead. */
        if (synapseConnection.IsTornDown)
            return;

        if (_transmissionEngine is not null)
            _transmissionEngine.SendDisconnect(synapseConnection);

        TeardownConnection(synapseConnection);
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
                    /* The kick is immediate; the ban is not. A single datagram carries an attacker-chosen source
                     * address, so banning on one violation lets a forged packet lock out an arbitrary endpoint.
                     * RegisterViolation blacklists only once the signature crosses SecurityConfig's threshold,
                     * and the resulting entry expires. */
                    if (signature != SecurityProvider.UnsetSignature)
                        Security.RegisterViolation(signature);

                    DisconnectAndBlacklist(endPoint, canBlacklist: false);
                    return;
            }
        }
        catch { }
    }

    /// <summary>
    /// Stops the engine and releases all resources.
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

            connection.State = ConnectionState.Disconnected;

            if (connection.IsTornDown)
                continue;

            connection.IsTornDown = true;
            _pendingReleases.Add(connection);
        }

        Connections.Clear();
        ReleaseTornDownConnections();
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
        _boundEndPoints.Clear();
    }

    /// <summary>
    /// Forwards an exception raised by an ingress engine to the <see cref="UnhandledException"/> event.
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
    /// OS-connects the bound socket whose address family matches the remote, switching its ingress drain and every send
    /// addressed to the remote onto the endpoint-free connected socket calls.
    /// </summary>
    /// <param name="endPoint">The single remote this engine will talk to.</param>
    /// <remarks>With no family-matched bound socket the engine simply stays in any-sender mode, as
    /// <see cref="SynapseConfig.ConnectedSocketEnabled"/> documents.</remarks>
    private void ConnectSocketToRemote(IPEndPoint endPoint)
    {
        /* The OS filters a connected socket's inbound datagrams to the connected remote, and full-cone traversal depends on
         * receiving probes from third parties, the combination can never work, so it fails loudly instead of silently. */
        if (Config.NatTraversal.Mode == NatTraversalMode.FullCone)
            throw new InvalidOperationException($"{nameof(SynapseConfig.ConnectedSocketEnabled)} cannot combine with full-cone NAT traversal: a connected socket cannot receive third-party probe datagrams.");

        for (int index = 0; index < _sockets.Count; index++)
        {
            Socket socket = _sockets[index];

            if (socket.AddressFamily != endPoint.AddressFamily)
                continue;

            socket.Connect(endPoint);

            // Ingress engines are created in socket order, so the index pairs the drain with the socket it owns.
            _ingressEngines[index].SetConnectedRemote(endPoint);
            _transmissionEngine!.SetConnectedRemote(socket, endPoint);

            return;
        }
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
        rented.Initialize(MaximumTransmissionUnit, effectiveMax);

        PacketSplitter? existing = Interlocked.CompareExchange(ref synapseConnection.Splitter, rented, null);

        if (existing is not null)
        {
            ResettableObjectPool<PacketSplitter>.Return(rented);
            return existing;
        }

        return rented;
    }

    /// <summary>
    /// Ingress callback: wraps the delivered payload in a <see cref="PacketReceivedEventArgs"/> and raises
    /// <see cref="PacketReceived"/>. Returns the payload buffer to the pool in the finally block, but only when the
    /// ingress path rented that buffer for this delivery and handed ownership over with it.
    /// </summary>
    /// <param name="synapseConnection">The connection the payload arrived on.</param>
    /// <param name="payload">The complete payload to dispatch.</param>
    /// <param name="isReliable">True when the payload was sent reliably.</param>
    /// <param name="isPayloadRented">
    /// True when <paramref name="payload"/> is a per-delivery rental this method owns and must return. False when the
    /// buffer is the ingress engine's own receive buffer, delivered zero-copy under
    /// <see cref="Configuration.SynapseConfig.CopyReceivedPayloads"/>. Returning that one would put the engine's live
    /// buffer back in the pool for a second owner to rent, and the engine would return it once more at shutdown.
    /// </param>
    private void OnPayloadDelivered(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable, bool isPayloadRented)
    {
        PacketReceivedEventArgs packetReceivedEventArgs = new(synapseConnection, payload, isReliable);

        try
        {
            PacketReceived?.Invoke(packetReceivedEventArgs);
        }
        catch (Exception listenerException)
        {
            /* One bad subscriber must not abort the drain. DeliverOrdered can be mid-way through releasing a
             * batch out of the reorder buffer; letting the exception escape drops every remaining payload and
             * strands its pooled buffer. Every other event raise on this class already swallows this way. */
            UnhandledException?.Invoke(listenerException);
        }
        finally
        {
            if (isPayloadRented && payload.Array is not null)
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
    /// Removes the connection for <paramref name="endPoint"/>, tears it down, and optionally blacklists the computed signature.
    /// </summary>
    private void DisconnectAndBlacklist(IPEndPoint endPoint, bool canBlacklist)
    {
        if (Connections.ConnectionsByEndPoint.TryGetValue(endPoint, out SynapseConnection? synapseConnection))
            TeardownConnection(synapseConnection);

        if (canBlacklist)
        {
            ulong signature = Security.ComputeSignature(endPoint, ReadOnlySpan<byte>.Empty);
            Security.AddToBlacklist(signature);
        }
    }

    /// <summary>
    /// The single teardown path for a connection. Every route by which a session ends, local disconnect, violation
    /// kick, timeout, a peer's disconnect packet, being replaced by a fresh connect, or engine shutdown, goes
    /// through here, so no two paths can disagree about what gets released.
    /// </summary>
    /// <param name="synapseConnection">The connection to terminate.</param>
    /// <remarks>
    /// Order matters. The lookup tables are cleared first so nothing can resolve the connection again, then every
    /// engine-internal reference to it is dropped, then it is marked torn down, then the close notification is raised
    /// while the instance is still intact, and only then is its release queued. Marking it before the notification is
    /// what stops a handler that disconnects the same connection re-entrantly from raising <c>ConnectionClosed</c> a
    /// second time and queueing a second release. The release runs at the end of <see cref="Poll"/> rather than here,
    /// because a user handler can call this re-entrantly from inside <c>PacketReceived</c> while the ingress loop still
    /// holds the same connection in a local, and its buffers are still in use.
    /// </remarks>
    private void TeardownConnection(SynapseConnection synapseConnection)
    {
        if (synapseConnection.IsTornDown)
            return;

        Connections.Remove(synapseConnection.RemoteEndPoint, out _);
        RemoveNatPunchesFor(synapseConnection);

        synapseConnection.State = ConnectionState.Disconnected;
        synapseConnection.IsTornDown = true;
        RaiseConnectionClosed(synapseConnection);

        _pendingReleases.Add(synapseConnection);
    }

    /// <summary>
    /// Sends everything queued by <see cref="EnqueueRaw"/>. Runs on the engine thread as part of <see cref="Poll"/>.
    /// </summary>
    private void FlushQueuedRawSends()
    {
        while (_queuedRawSends.TryDequeue(out QueuedRawSend queued))
        {
            try
            {
                _transmissionEngine?.SendRaw(new(queued.Buffer, 0, queued.Length), queued.Target);
            }
            catch (Exception unexpectedException)
            {
                UnhandledException?.Invoke(unexpectedException);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(queued.Buffer);
            }
        }
    }

    /// <summary>
    /// Releases the pooled buffers of every connection queued by <see cref="TeardownConnection"/>. Called at the end of
    /// <see cref="Poll"/>, once no engine frame can still be holding one, and again on shutdown.
    /// </summary>
    /// <remarks>
    /// The connection object itself is deliberately <b>not</b> returned to its pool; it is left torn down, with its
    /// identity intact, for the garbage collector. <c>Connect</c> and every connection event hand the raw object to the
    /// application, and the pool is a static thread-local stack shared by every <see cref="SynapseManager"/> in the
    /// process, so the very next connection made on this thread would be given the same object. An application still
    /// holding the old reference, as Nucleus's relay link did, would then send to or disconnect an unrelated peer.
    /// Recycling saved one allocation per connection, not per packet, which is not worth that. See F16 in
    /// <c>docs/ROBUSTNESS_SWEEP.md</c>.
    /// </remarks>
    private void ReleaseTornDownConnections()
    {
        if (_pendingReleases.Count == 0)
            return;

        for (int i = 0; i < _pendingReleases.Count; i++)
            _pendingReleases[i].ReleasePooledResources();

        _pendingReleases.Clear();
    }


    /// <summary>
    /// A raw send handed over from another thread, holding a private copy of the payload.
    /// </summary>
    private readonly struct QueuedRawSend
    {
        /// <summary>
        /// Pooled buffer holding the payload; returned once sent.
        /// </summary>
        public readonly byte[] Buffer;
        /// <summary>
        /// Valid byte count within <see cref="Buffer"/>.
        /// </summary>
        public readonly int Length;
        /// <summary>
        /// Destination endpoint.
        /// </summary>
        public readonly IPEndPoint Target;

        /// <summary>
        /// Creates a queued send over <paramref name="buffer"/>, carrying <paramref name="length"/>
        /// valid bytes and addressed to <paramref name="target"/>.
        /// </summary>
        /// <param name="buffer">Pooled buffer holding the payload copy.</param>
        /// <param name="length">Number of valid bytes within <paramref name="buffer"/>.</param>
        /// <param name="target">Endpoint the payload is addressed to.</param>
        public QueuedRawSend(byte[] buffer, int length, IPEndPoint target)
        {
            Buffer = buffer;
            Length = length;
            Target = target;
        }
    }
}
