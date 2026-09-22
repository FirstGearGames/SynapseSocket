using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CodeBoost.Performance;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Diagnostics;
using SynapseSocket.Packets;
using SynapseSocket.Security;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Transport;

/// <summary>
/// Ingress Engine (Receiver).
/// Manages incoming data and initial filtering.
/// Applies lowest-level mitigations BEFORE any payload copy.
/// </summary>
internal sealed partial class IngressEngine
{
    /// <summary>
    /// Raised when a complete payload (unsegmented or fully reassembled) is ready for the application layer.
    /// </summary>
    internal event PayloadDeliveredHandler? PayloadDelivered;
    /// <summary>
    /// Raised when a new connection is established via a successful handshake.
    /// </summary>
    internal event ConnectionHandler? ConnectionEstablished;
    /// <summary>
    /// Raised when a remote peer sends a disconnect packet.
    /// </summary>
    internal event ConnectionHandler? ConnectionClosed;
    /// <summary>
    /// Raised when the ingress path has determined a connection is finished and the manager should run its single
    /// teardown. The manager owns teardown because only it can retire NAT punches and schedule the pool return;
    /// the ingress engine must not touch the connection after raising this.
    /// </summary>
    internal event ConnectionHandler? TeardownRequested;
    /// <summary>
    /// Raised when a connection attempt is rejected before it can be established.
    /// </summary>
    internal event ConnectionFailedCallbackHandler? ConnectionFailed;
    /// <summary>
    /// Raised when a protocol violation is detected on the ingress path.
    /// </summary>
    internal event ViolationCallbackHandler? ViolationOccurred;
    /// <summary>
    /// Raised when an unexpected exception escapes the receive loop.
    /// </summary>
    internal event UnhandledExceptionHandler? UnhandledException;
    /// <summary>
    /// Raised when the ingress path receives a datagram whose leading type byte is not a recognised
    /// Synapse <see cref="PacketType"/>. Allows external protocols to piggyback on the UDP socket.
    /// </summary>
    internal event UnknownPacketReceivedHandler? UnknownPacketReceived;
    /// <summary>
    /// True when the ingress receive loop is running.
    /// </summary>
    internal bool IsRunning { get; private set; }
    /// <summary>
    /// The UDP socket this engine receives from.
    /// </summary>
    private readonly Socket _socket;
    /// <summary>
    /// Engine configuration snapshot.
    /// </summary>
    private readonly SynapseConfig _config;
    /// <summary>
    /// Security provider for signature computation, blacklist, and rate limiting.
    /// </summary>
    private readonly SecurityProvider _security;
    /// <summary>
    /// Active connection table.
    /// </summary>
    private readonly ConnectionManager _connections;
    /// <summary>
    /// Transmission engine used to send acknowledgements and handshake responses.
    /// </summary>
    private readonly TransmissionEngine _sender;
    /// <summary>
    /// Telemetry counters for this engine.
    /// </summary>
    private readonly Telemetry _telemetry;
    /// <summary>
    /// Tracks the last probe-response tick per source IP to enforce the per-address rate limit.
    /// </summary>
    private readonly ConcurrentDictionary<IpKey, long> _natProbeLastResponseTicks = [];
    /// <summary>
    /// Monotonic <see cref="Clock.Ticks"/> of the last stale-entry eviction pass for <see cref="_natProbeLastResponseTicks"/>.
    /// </summary>
    private long _lastProbeEvictionTicks;
    /// <summary>
    /// Replay cache mapping handshake signature to the first-seen <see cref="Clock.Ticks"/> value.
    /// Prevents replayed handshakes from re-establishing connections after the original session ends.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, long> _seenHandshakes = [];
    /// <summary>
    /// Monotonic <see cref="Clock.Ticks"/> of the last stale-entry eviction pass for <see cref="_seenHandshakes"/>.
    /// </summary>
    private long _lastHandshakeEvictionTicks;
    /// <summary>
    /// Live entry count of the handshake replay cache. Diagnostic surface for verifying the cache stays bounded.
    /// </summary>
    internal int ReplayCacheCount => _seenHandshakes.Count;
    /// <summary>
    /// How long a replay-cache entry stays meaningful. Sized off the connection timeout so a handshake cannot be
    /// replayed within any window where the original session could still be alive.
    /// </summary>
    private long ReplayCacheEntryLifetimeTicks => _config.Connection.TimeoutMilliseconds * TimeSpan.TicksPerMillisecond * 2;

    /// <summary>
    /// True when received payloads are copied before being dispatched to event handlers.
    /// </summary>
    private readonly bool _copyReceivedPayloads;
    /// <summary>
    /// Keyed HMAC signing NAT challenge tokens. The key is generated once at construction and never transmitted.
    /// </summary>
    private readonly System.Security.Cryptography.HMACSHA256 _natChallengeHmac;
    /// <summary>
    /// Keyed HMAC signing handshake return-routability tokens. Keyed separately from
    /// <see cref="_natChallengeHmac"/> so a token minted for one exchange cannot be replayed into the other.
    /// </summary>
    private readonly System.Security.Cryptography.HMACSHA256 _handshakeChallengeHmac;
    /// <summary>
    /// Live connection count at or above which unknown endpoints must prove return-routability before any state is
    /// allocated for them, or <see cref="SynapseConfig.EffectiveUnlimitedValueUInt32"/> when the gate is disabled.
    /// </summary>
    private readonly uint _handshakeChallengeThreshold;
    /// <summary>
    /// True when Ack batching is enabled and the interval is not unset.
    /// </summary>
    private readonly bool _isAckBatchingEnabled;
    /// <summary>
    /// True if SecurityConfig is enabled.
    /// </summary>
    private readonly bool _isSecurityEnabled;
    /// <summary>
    /// Effective reorder buffer cap: <see cref="SecurityConfig.MaximumOutOfOrderReliablePackets"/> converted from
    /// 0 (disabled) to <see cref="SynapseConfig.EffectiveUnlimitedValueUInt32"/> so the hot-path check is a single
    /// comparison with no zero guard.
    /// </summary>
    private readonly uint _effectiveMaximumOutOfOrderReliablePackets;
    /// <summary>
    /// Effective MTU: <see cref="SynapseConfig.MaximumTransmissionUnit"/> less the
    /// <see cref="IPacketTransform.ReservedBytes"/> of a configured <see cref="SynapseConfig.PacketTransform"/>, cached
    /// to avoid repeated config dereferences on the receive path. Payloads are reversed through the transform before
    /// any size accounting runs, so this is the ceiling an arriving segment payload is measured against.
    /// </summary>
    private readonly uint _effectiveMaximumTransmissionUnit;
    /// <summary>
    /// Optional layer that reverses each inbound payload before the packet is parsed, or null when
    /// <see cref="SynapseConfig.PacketTransform"/> was left unset.
    /// </summary>
    private readonly IPacketTransform? _packetTransform;
    /// <summary>
    /// Effective reassembled packet size cap: <see cref="SecurityConfig.MaximumReassembledPacketSize"/> converted from
    /// 0 (disabled) to <see cref="SynapseConfig.EffectiveUnlimitedValueUInt32"/> so the hot-path check is a single
    /// comparison with no zero guard.
    /// </summary>
    private readonly uint _effectiveMaximumReassembledPacketSize;
    /// <summary>
    /// Effective connection cap: <see cref="SynapseConfig.MaximumConcurrentConnections"/> converted from
    /// 0 (disabled) to <see cref="SynapseConfig.EffectiveUnlimitedValueUInt32"/> so the handshake check is a single
    /// comparison with no zero guard.
    /// </summary>
    private readonly uint _effectiveMaximumConcurrentConnections;
    /// <summary>
    /// Effective per-poll receive budget: <see cref="SynapseConfig.MaximumReceivesPerPoll"/> converted from
    /// 0 (disabled) to <see cref="SynapseConfig.EffectiveUnlimitedValueUInt32"/>.
    /// </summary>
    private readonly uint _effectiveMaximumReceivesPerPoll;

    /// <summary>
    /// Window within which an inbound handshake is read as the peer's answer to one we just sent, rather than as a
    /// fresh request that would reset the session. Must comfortably exceed a real round trip.
    /// </summary>
    private const long HandshakeAnswerWindowTicks = TimeSpan.TicksPerSecond;

    /// <summary>
    /// Violation detail reported when a peer declares a reassembled size above the configured ceiling.
    /// </summary>
    private const string ViolationSegmentAssemblyOversized = "Declared segment assembly size exceeds MaximumReassembledPacketSize.";
    /// <summary>
    /// Violation detail reported when a segment reappears with a different segment count or reliability flag than
    /// the assembly it belongs to was opened with.
    /// </summary>
    private const string ViolationSegmentMismatch = "Segment resent with mismatched segment count or reliability flag.";
    /// <summary>
    /// Violation detail reported when a peer holds more out-of-order reliable packets than the reorder buffer allows.
    /// </summary>
    private const string ViolationReorderBufferExceeded = "Reorder buffer capacity exceeded.";

    /// <summary>
    /// Creates a new ingress engine bound to the provided socket.
    /// </summary>
    /// <param name="socket">The bound UDP socket to receive from.</param>
    /// <param name="config">Engine configuration snapshot.</param>
    /// <param name="security">Security provider for signature, blacklist, and rate-limit checks.</param>
    /// <param name="connections">Active connection table shared with the rest of the engine.</param>
    /// <param name="sender">Transmission engine used to emit acknowledgements and handshake responses.</param>
    /// <param name="telemetry">Telemetry counters for this engine instance.</param>
    internal IngressEngine(Socket socket, SynapseConfig config, SecurityProvider security, ConnectionManager connections, TransmissionEngine sender, Telemetry telemetry)
    {
        _socket = socket;
        _config = config;
        _security = security;
        _connections = connections;
        _sender = sender;
        _telemetry = telemetry;

        _copyReceivedPayloads = _config.CopyReceivedPayloads;
        _isNatEnabled = _config.NatTraversal.Mode != NatTraversalMode.Disabled;
        _isAckBatchingEnabled = _config.Reliable.AckBatchingEnabled;
        _isSecurityEnabled = config.Security.Enabled;
        _packetTransform = config.PacketTransform;
        _effectiveMaximumTransmissionUnit = config.MaximumTransmissionUnit - (_packetTransform?.ReservedBytes ?? 0);
        _effectiveMaximumOutOfOrderReliablePackets = SynapseConfig.ToEffectiveLimit(config.Security.MaximumOutOfOrderReliablePackets);
        _effectiveMaximumReassembledPacketSize = SynapseConfig.ToEffectiveLimit(config.Security.MaximumReassembledPacketSize);
        _effectiveMaximumConcurrentConnections = SynapseConfig.ToEffectiveLimit(config.MaximumConcurrentConnections);
        _effectiveMaximumReceivesPerPoll = SynapseConfig.ToEffectiveLimit(config.MaximumReceivesPerPoll);
        _handshakeChallengeThreshold = SynapseConfig.ToEffectiveLimit(config.Security.HandshakeChallengeThreshold);

        Span<byte> secret = stackalloc byte[32];

        System.Security.Cryptography.RandomNumberGenerator.Fill(secret);
        _natChallengeHmac = new(secret.ToArray());

        System.Security.Cryptography.RandomNumberGenerator.Fill(secret);
        _handshakeChallengeHmac = new(secret.ToArray());
    }

    /// <summary>
    /// Maximum UDP datagram size. The engine always receives into a buffer this large so that oversized datagrams
    /// are not silently truncated by the kernel, the security layer must see them to raise an Oversized violation.
    /// </summary>
    private const int MaximumUdpDatagramSize = 65535;
    /// <summary>
    /// Receive buffer rented for this engine's lifetime and reused across every <see cref="Drain"/>.
    /// </summary>
    private byte[]? _receiveBuffer;
    /// <summary>
    /// Buffer the reversed packet is assembled into, rented alongside <see cref="_receiveBuffer"/> and reused for every
    /// datagram. Null when no <see cref="SynapseConfig.PacketTransform"/> is configured, so an engine without one
    /// rents nothing extra.
    /// </summary>
    /// <remarks>
    /// A reversed packet is handed to the packet handlers straight out of this buffer, which makes it exactly as stable
    /// across a <see cref="SynapseManager.PacketReceived"/> handler as <see cref="_receiveBuffer"/> is under
    /// <see cref="SynapseConfig.CopyReceivedPayloads"/> being false.
    /// </remarks>
    private byte[]? _transformBuffer;
    /// <summary>
    /// Caller-owned sockaddr the native receive fills per datagram, reused for the engine's lifetime. Replaces the
    /// SocketAddress, IPEndPoint and IPAddress that the managed any-sender receive creates every time.
    /// </summary>
    private byte[]? _receiveSockAddr;
#if !NET8_0_OR_GREATER
    /// <summary>
    /// True when this engine takes the native receive path. Engaged where the managed API cannot receive from an
    /// unspecified sender without allocating, which is every runtime lacking the SocketAddress overloads.
    /// Absent on net8.0, whose SocketAddress overloads already receive without allocating.
    /// </summary>
    private bool _isNativeReceiveEnabled;
#endif
    /// <summary>
    /// Wildcard source endpoint handed (by ref) to each blocking receive; the kernel overwrites it with the sender.
    /// </summary>
    private EndPoint? _anyEndPoint;
    /// <summary>
    /// The single remote the socket is OS-connected to when <see cref="SynapseConfig.ConnectedSocketEnabled"/> engaged, or null
    /// for the ordinary any-sender mode. A connected socket receives through the endpoint-free Receive call and every datagram
    /// is attributed to this stable instance, no per-datagram endpoint serialization or materialization on any runtime.
    /// </summary>
    private IPEndPoint? _connectedRemoteEndPoint;
#if NET8_0_OR_GREATER
    /// <summary>
    /// Reusable sender address the allocation-free receive overload fills per datagram, resolved against the connection
    /// table without materializing an <see cref="IPEndPoint"/>. The classic ref-EndPoint receive allocates a SocketAddress,
    /// an IPEndPoint, and an IPAddress per datagram; this instance replaces all three for every known sender.
    /// </summary>
    private SocketAddress? _receivedSocketAddress;
    /// <summary>
    /// Template endpoint <see cref="_receivedSocketAddress"/> materializes unknown senders through, handshake and
    /// violation paths still need a real <see cref="IPEndPoint"/>, and creating one is the exception, not the per-datagram rule.
    /// </summary>
    private IPEndPoint? _endPointTemplate;
#endif

    /// <summary>
    /// Allocates the receive buffer and marks the engine running. Called once by the manager before the first poll.
    /// </summary>
    public void Start()
    {
        _anyEndPoint = _socket.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
#if NET8_0_OR_GREATER
        _receivedSocketAddress = new(_socket.AddressFamily);
        _endPointTemplate = (IPEndPoint)_anyEndPoint;
#endif
        _receiveBuffer = ArrayPool<byte>.Shared.Rent(MaximumUdpDatagramSize);

        if (_packetTransform is not null)
            _transformBuffer = ArrayPool<byte>.Shared.Rent(MaximumUdpDatagramSize);

        _receiveSockAddr = new byte[NativeSocket.SockAddrSize];

        /* Only worth engaging where the managed receive allocates. On NET8 the SocketAddress overload already
         * costs nothing, so the syscall binding buys nothing and is left alone. */
#if !NET8_0_OR_GREATER
        _isNativeReceiveEnabled = _config.NativeReceiveEnabled && NativeSocket.IsSupported;
#endif
        IsRunning = true;
    }

    /// <summary>
    /// Attributes every future receive to the single remote the socket has been OS-connected to, switching the drain onto the
    /// endpoint-free Receive path.
    /// </summary>
    /// <param name="remoteEndPoint">The remote the socket is connected to.</param>
    public void SetConnectedRemote(IPEndPoint remoteEndPoint) => _connectedRemoteEndPoint = remoteEndPoint;

    /// <summary>
    /// Marks the engine stopped and returns the receive buffer to the pool.
    /// </summary>
    public void Stop()
    {
        IsRunning = false;

        if (_receiveBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_receiveBuffer, clearArray: false);
            _receiveBuffer = null;
        }

        if (_transformBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_transformBuffer, clearArray: false);
            _transformBuffer = null;
        }

        // The manager builds fresh ingress engines on every Start, so a stopped engine is never reused.
        _natChallengeHmac.Dispose();
        _handshakeChallengeHmac.Dispose();
    }

    /// <summary>
    /// Drains every datagram currently buffered on the socket, running lowest-level filters and dispatching each
    /// to the packet handlers inline. Called once per engine poll on the host's thread; returns when the socket
    /// has nothing more to read. The kernel receive buffer (SO_RCVBUF) bounds how much can accumulate between polls.
    /// </summary>
    /// <param name="nowTicks">
    /// The tick this poll started at, reused for every datagram in the batch rather than re-read per datagram.
    /// <see cref="Clock.Ticks"/> is a QPC/vDSO call at roughly 20-30ns, an order of magnitude above anything else
    /// on this path, and a drain completes in microseconds, so per-datagram precision buys nothing against a 15s
    /// timeout or a 250ms resend interval.
    /// </param>
    public void Drain(long nowTicks)
    {
        if (_receiveBuffer is null)
            return;

        uint receivesThisPoll = 0;
        while (true)
        {
            /* Bounded work per poll. Without this the loop runs until the socket is empty, so traffic arriving
             * faster than the engine processes it keeps the loop fed and Poll never returns. The host frame loop
             * stalls for the duration of the flood. Counting every iteration rather than every successful receive
             * also guarantees forward progress: an error path that continues without consuming its datagram can no
             * longer spin here indefinitely. Whatever is left stays queued for the next poll.
             */
            if (receivesThisPoll >= _effectiveMaximumReceivesPerPoll)
                break;

            receivesThisPoll++;
            int receivedLength;
#if !NET8_0_OR_GREATER
            int nativeSockAddrLength = 0;
#endif
            EndPoint remoteEndPoint = _anyEndPoint!;

            try
            {
                /* Kept deliberately, despite costing a syscall per datagram on top of the receive. The alternative
                 * (non-blocking mode with WouldBlock as the exit condition) would halve the syscalls, but
                 * Blocking is a socket-wide property: every send would then have to handle WouldBlock too, turning
                 * a guaranteed send into a partial one under buffer pressure. The per-poll receive budget already
                 * bounds this loop, which was the actual hazard. */
                // Available (FIONREAD) reports the pending datagram bytes and is reliable across runtimes, whereas
                // Socket.Poll(0, SelectRead) under Unity's Mono can report no data on a UDP socket that has some, the
                // single-process loopback handshake stalls there while it connects under .NET. The socket stays in blocking
                // mode, so sends are unaffected, and because the engine is single-threaded a positive Available guarantees
                // ReceiveFrom will not block.
                if (_socket.Available == 0)
                    break;

                /* A connected socket receives through the endpoint-free Receive call, no endpoint is serialized or
                 * materialized on any runtime, which is the only zero-allocation receive Unity's Mono has at all. The kernel
                 * already filtered the datagram to the connected remote, so attribution is the stable stored instance. */
                if (_connectedRemoteEndPoint is not null)
                {
                    receivedLength = _socket.Receive(_receiveBuffer, 0, MaximumUdpDatagramSize, SocketFlags.None);
                }
#if NET8_0_OR_GREATER
                /* The SocketAddress overload fills the reusable instance in place. The classic ref-EndPoint overload below
                 * allocates a SocketAddress, an IPEndPoint, and an IPAddress for every datagram; netstandard2.1 has no
                 * allocation-free any-sender alternative, so only the modern build takes this path. */
                else
                {
                    receivedLength = _socket.ReceiveFrom(_receiveBuffer.AsSpan(0, MaximumUdpDatagramSize), SocketFlags.None, _receivedSocketAddress!);
                }
#else
                else if (_isNativeReceiveEnabled)
                {
                    /* recvfrom writes the sender into our own buffer, so nothing is created per datagram. The
                     * managed overload below allocates a SocketAddress, an IPEndPoint and an IPAddress every time. */
                    receivedLength = NativeSocket.ReceiveFrom(_socket, _receiveBuffer, MaximumUdpDatagramSize, _receiveSockAddr!, out nativeSockAddrLength);

                    if (receivedLength < 0)
                        break;
                }
                else
                {
                    receivedLength = _socket.ReceiveFrom(_receiveBuffer, 0, MaximumUdpDatagramSize, SocketFlags.None, ref remoteEndPoint);
                }
#endif
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException socketException) when (socketException.SocketErrorCode is SocketError.Interrupted or SocketError.OperationAborted or SocketError.NotSocket)
            {
                // Socket closed during shutdown.
                break;
            }
            catch (SocketException socketException) when (socketException.SocketErrorCode == SocketError.MessageSize)
            {
                // Datagram larger than our buffer (should not happen with 64K, but be defensive).
                ViolationOccurred?.Invoke(new(IPAddress.Any, 0), 0, ViolationReason.Oversized, 0, "MessageSize", ViolationAction.KickAndBlacklist);
                continue;
            }
            catch (SocketException)
            {
                // Datagram-specific error (e.g. a prior ICMP port-unreachable surfacing as ConnectionReset). Skip it.
                continue;
            }
            catch (Exception unexpectedException)
            {
                UnhandledException?.Invoke(unexpectedException);
                continue;
            }

            try
            {
                if (_connectedRemoteEndPoint is not null)
                {
                    HandleDatagram(_receiveBuffer, receivedLength, _connectedRemoteEndPoint, nowTicks);
                }
#if NET8_0_OR_GREATER
                /* A known sender resolves to its connection's stable endpoint without materializing anything; only an unknown
                 * sender (a handshake, probe, or violation, never the steady state) pays for a fresh IPEndPoint. */
                else
                {
                    IPEndPoint fromEndPoint = _connections.TryGetBySocketAddress(_receivedSocketAddress!, out SynapseConnection? resolvedConnection)
                        ? resolvedConnection!.RemoteEndPoint
                        : (IPEndPoint)_endPointTemplate!.Create(_receivedSocketAddress!);

                    HandleDatagram(_receiveBuffer, receivedLength, fromEndPoint, nowTicks, resolvedConnection);
                }
#else
                else if (_isNativeReceiveEnabled)
                {
                    /* An established peer resolves straight from the raw address the kernel filled. Only an unknown
                     * sender (a handshake, probe or violation, never the steady state) pays to materialise one. */
                    ulong addressKey = NativeSocket.ComputeAddressKey(_receiveSockAddr!, nativeSockAddrLength);

                    IPEndPoint? nativeEndPoint = _connections.TryGetByAddressKey(addressKey, out SynapseConnection? nativeConnection)
                        ? nativeConnection!.RemoteEndPoint
                        : NativeSocket.ToEndPoint(_receiveSockAddr!);

                    if (nativeEndPoint is not null)
                        HandleDatagram(_receiveBuffer, receivedLength, nativeEndPoint, nowTicks, nativeConnection);
                }
                else
                {
                    HandleDatagram(_receiveBuffer, receivedLength, (IPEndPoint)remoteEndPoint, nowTicks);
                }
#endif
            }
            catch (Exception unexpectedException)
            {
                // A single bad packet must not stop the drain.
                UnhandledException?.Invoke(unexpectedException);
            }
        }
    }

    /// <summary>
    /// Periodic upkeep for this engine, driven from <see cref="SynapseManager.Poll"/>.
    /// Runs the replay-cache and NAT probe-table sweeps here rather than from the receive path: both are O(n) scans
    /// over tables an attacker sizes, and running them inline meant the pause landed on a timer the attacker chose.
    /// </summary>
    /// <param name="nowTicks">Current time in <see cref="DateTime.Ticks"/>.</param>
    internal void RunMaintenance(long nowTicks)
    {
        if (nowTicks - _lastHandshakeEvictionTicks > TimeSpan.TicksPerSecond)
        {
            _lastHandshakeEvictionTicks = nowTicks;
            RemoveExpiredHandshakeEntries(nowTicks, ReplayCacheEntryLifetimeTicks);
        }

        if (_isNatEnabled && nowTicks - _lastProbeEvictionTicks > TimeSpan.TicksPerSecond)
        {
            _lastProbeEvictionTicks = nowTicks;
            RemoveExpiredProbeLimitEntries(nowTicks, _config.NatTraversal.IntervalMilliseconds * TimeSpan.TicksPerMillisecond * 10);
        }
    }

    /// <summary>
    /// Runs the lowest-level mitigations on one received datagram and dispatches it to the packet handlers.
    /// Established connections skip signature recomputation and blacklist lookup, those only apply at handshake
    /// time. Size and rate-limit checks still run for all senders.
    /// </summary>
    /// <param name="buffer">The raw receive buffer containing the datagram.</param>
    /// <param name="receivedLength">Number of valid bytes in <paramref name="buffer"/>.</param>
    /// <param name="fromEndPoint">The source endpoint of the datagram.</param>
    /// <param name="nowTicks">Monotonic timestamp for the current poll, resolved once by the caller.</param>
    /// <param name="resolvedConnection">Connection the caller already resolved for <paramref name="fromEndPoint"/>, or null to resolve it here.</param>
    private void HandleDatagram(byte[] buffer, int receivedLength, IPEndPoint fromEndPoint, long nowTicks, SynapseConnection? resolvedConnection = null)
    {
        FilterResult filterResult;
        ulong signature;

        /* The drain may already have resolved the sender while avoiding an endpoint materialisation; reuse that
         * rather than hashing the endpoint a second time for the same datagram. */
        SynapseConnection? synapseConnection = resolvedConnection;
        bool isEstablished = synapseConnection is not null || _connections.ConnectionsByEndPoint.TryGetValue(fromEndPoint, out synapseConnection);

        if (isEstablished)
        {
            signature = synapseConnection!.Signature;
            filterResult = _security.InspectEstablished(synapseConnection, receivedLength);
        }
        else
            filterResult = _security.InspectNew(fromEndPoint, receivedLength, out signature);

        if (filterResult is FilterResult.Allowed)
        {
            // Counted before the transform runs so telemetry, the rate limiter, and the oversize check all measure the
            // datagram as it actually crossed the wire.
            _telemetry.OnReceived(receivedLength);

            if (_packetTransform is not null && !TryTransformInbound(fromEndPoint, ref buffer, ref receivedLength))
            {
                _telemetry.OnSecurityDroppedReceived();
                ViolationOccurred?.Invoke(fromEndPoint, signature, ViolationReason.TransformRejected, receivedLength, null, ViolationAction.Drop);

                return;
            }

            ProcessPacket(buffer, receivedLength, fromEndPoint, synapseConnection, nowTicks);

            return;
        }

        _telemetry.OnSecurityDroppedReceived();

        if (filterResult is FilterResult.Blacklisted)
        {
            // Blacklisted = REJECTION, not a per-packet violation. The peer is already known-bad.
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.Blacklisted, filterResult.ToString());
            return;
        }

        ViolationReason violationReason = filterResult switch
        {
            FilterResult.Oversized => ViolationReason.Oversized,
            FilterResult.RateLimited => ViolationReason.RateLimitExceeded,
            _ => ViolationReason.Malformed
        };

        /* Rate limiting sheds load; it does not ban. Banning on a single threshold crossing turns a legitimate
         * burst into a lockout, and because the source address of an unauthenticated datagram is attacker-chosen it
         * also hands over a way to evict any endpoint that can be forged. Excess is dropped and nothing more. */
        ViolationAction violationAction = violationReason == ViolationReason.RateLimitExceeded
            ? ViolationAction.Drop
            : ViolationAction.KickAndBlacklist;

        ViolationOccurred?.Invoke(fromEndPoint, signature, violationReason, receivedLength, filterResult.ToString(), violationAction);
    }

    /// <summary>
    /// Reverses the payload of an arriving packet through <see cref="_packetTransform"/> and repoints
    /// <paramref name="buffer"/> and <paramref name="receivedLength"/> at the rebuilt packet, header first.
    /// Datagrams whose leading byte is above <see cref="PacketType.NatChallenge"/> belong to an external protocol
    /// piggybacking on the socket and pass through untouched, as do datagrams too short for the header they declare,
    /// which <see cref="ProcessPacket"/> already reports as malformed.
    /// </summary>
    /// <param name="fromEndPoint">The source endpoint of the datagram.</param>
    /// <param name="buffer">The receive buffer, replaced by the transform buffer when a payload was reversed.</param>
    /// <param name="receivedLength">Number of valid bytes, updated to the reversed packet's length.</param>
    /// <returns>True to keep processing the datagram, or false when the transform rejected it.</returns>
    private bool TryTransformInbound(IPEndPoint fromEndPoint, ref byte[] buffer, ref int receivedLength)
    {
        if (receivedLength <= 0)
            return true;

        byte typeByte = buffer[0];

        if (typeByte > (byte)PacketType.NatChallenge)
            return true;

        PacketType packetType = (PacketType)typeByte;
        int headerSize = PacketHeader.ComputeHeaderSize(packetType);

        if (receivedLength < headerSize)
            return true;

        byte[] transformBuffer = _transformBuffer!;
        Buffer.BlockCopy(buffer, 0, transformBuffer, 0, headerSize);

        bool isTransformed = _packetTransform!.TryTransform(PacketTransformDirection.Inbound, packetType, fromEndPoint, buffer.AsSpan(headerSize, receivedLength - headerSize), transformBuffer.AsSpan(headerSize), out int writtenLength);

        // The unsigned compare also catches a negative length, so a transform that misreports what it wrote cannot
        // hand the packet handlers a length that runs off the end of the buffer.
        if (!isTransformed || (uint)writtenLength > (uint)(transformBuffer.Length - headerSize))
            return false;

        buffer = transformBuffer;
        receivedLength = headerSize + writtenLength;

        return true;
    }

    /// <summary>
    /// Parses a single received datagram and routes it to the appropriate handler (handshake, data, ack, disconnect, keep-alive, or NAT probe/challenge).
    /// </summary>
    /// <param name="buffer">The raw receive buffer containing the datagram.</param>
    /// <param name="length">Number of valid bytes in <paramref name="buffer"/>.</param>
    /// <param name="fromEndPoint">The source endpoint of the datagram.</param>
    /// <param name="synapseConnection">The connection this datagram was resolved to, or null when the sender is not an established peer.</param>
    /// <param name="nowTicks">Monotonic timestamp for the current poll, resolved once by the caller.</param>
    private void ProcessPacket(byte[] buffer, int length, IPEndPoint fromEndPoint, SynapseConnection? synapseConnection, long nowTicks)
    {
        // Fast path: unreliable unsegmented payload, the dominant case.
        // PacketType.None = 0, header is exactly one byte. Filter guarantees length > 0.
        // Skip PacketHeader.Read entirely; no other fields are needed.
        if (buffer[0] == (byte)PacketType.None)
        {
            if (synapseConnection is null)
            {
                _telemetry.OnSecurityDroppedReceived();
                return;
            }

            /* Stamped whatever the state, so this stays a truthful record of the last packet seen. A pending
             * session cannot be held open by that: its timeout is measured from
             * <see cref="SynapseConnection.HandshakeStartedTicks"/>, which inbound traffic never refreshes. */
            synapseConnection.LastReceivedTicks = nowTicks;

            int fastPayloadLength = length - PacketHeader.TypeSize;

            if (!_copyReceivedPayloads)
            {
                /* Zero-copy: the segment points straight into this engine's receive buffer, which is rented once in
                 * Start and reused for every datagram. Ownership stays here, so the delivery is flagged unrented and
                 * the subscriber must not return the array. */
                PayloadDelivered?.Invoke(synapseConnection, new(buffer, PacketHeader.TypeSize, fastPayloadLength), isReliable: false, isPayloadRented: false);
            }
            else
            {
                byte[] payloadCopyBuffer = ArrayPool<byte>.Shared.Rent(fastPayloadLength);
                Buffer.BlockCopy(buffer, PacketHeader.TypeSize, payloadCopyBuffer, 0, fastPayloadLength);
                PayloadDelivered?.Invoke(synapseConnection, new(payloadCopyBuffer, 0, fastPayloadLength), isReliable: false, isPayloadRented: true);
            }

            return;
        }

        // Unknown packet type, byte outside the Synapse PacketType range.
        // External protocols (e.g. beacon/rendezvous clients) piggyback here intentionally.
        byte typeByte = buffer[0];

        if (typeByte > (byte)PacketType.SegmentAck)
        {
            /* AllowUnknownPackets value is not cached
             * because this condition is rare. */
            if (!_config.Security.AllowUnknownPackets)
            {
                _telemetry.OnSecurityDroppedReceived();
                ulong unknownSignature = synapseConnection?.Signature ?? _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);
                ViolationOccurred?.Invoke(fromEndPoint, unknownSignature, ViolationReason.UnknownPacket, length, null, ViolationAction.KickAndBlacklist);
                return;
            }

            FilterResult unknownFilterResult = UnknownPacketReceived?.Invoke(fromEndPoint, new(buffer, 0, length)) ?? FilterResult.Allowed;

            if (unknownFilterResult != FilterResult.Allowed)
            {
                _telemetry.OnSecurityDroppedReceived();
                ulong unknownSignature = synapseConnection?.Signature ?? _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);
                ViolationOccurred?.Invoke(fromEndPoint, unknownSignature, ViolationReason.UnknownPacket, length, unknownFilterResult.ToString(), ViolationAction.KickAndBlacklist);
            }

            return;
        }

        PacketType type;
        ushort sequence;
        ushort segmentId;
        byte segmentIndex;
        byte segmentCount;
        int headerSize;

        if (!PacketHeader.TryRead(buffer.AsSpan(0, length), out headerSize, out type, out sequence, out segmentId, out segmentIndex, out segmentCount))
        {
            _telemetry.OnSecurityDroppedReceived();
            ulong signature = _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);
            ViolationOccurred?.Invoke(fromEndPoint, signature, ViolationReason.Malformed, length, "Header parse failure", ViolationAction.KickAndBlacklist);
            return;
        }

        // Connection-less packet types, handled before any connection lookup.
        switch (type)
        {
            case PacketType.Handshake:
                ProcessHandshake(fromEndPoint, buffer, headerSize, length);
                return;

            case PacketType.NatProbe:
                ProcessNatProbe(fromEndPoint);
                return;

            case PacketType.NatChallenge:
                ProcessNatChallengeExchange(fromEndPoint, buffer.AsSpan(headerSize, length - headerSize));
                return;
        }

        if (synapseConnection is null)
        {
            _telemetry.OnSecurityDroppedReceived();
            return;
        }

        /* See the note on the fast path for why this is stamped in every state.
         *
         * A forged packet from a spoofed source does refresh this, holding a session alive after the real peer is
         * gone. That is not specific to KeepAlive. Every accepted type reaches this line, so authenticating one
         * type would move the vector rather than close it. Closing it means authenticating every packet, which is
         * accepted risk by design: this transport does not spend bytes per datagram. Damage is bounded to a held
         * connection slot, and anyone able to forge here can already inject payloads as that peer. */
        synapseConnection.LastReceivedTicks = nowTicks;

        switch (type)
        {
            case PacketType.Disconnect:
            {
                // Capture identity before teardown: TeardownRequested queues the instance for the pool, and OnReturn
                // clears Signature and RemoteEndPoint.
                ulong disconnectSignature = synapseConnection.Signature;

                TeardownRequested?.Invoke(synapseConnection);
                ViolationOccurred?.Invoke(fromEndPoint, disconnectSignature, ViolationReason.PeerDisconnect, packetSize: 0, details: null, ViolationAction.Ignore);

                return;
            }

            case PacketType.KeepAlive:
                return;

            case PacketType.SegmentAck:
            {
                /* Selective acknowledgement: the peer is telling us which segments of this message it already
                 * holds, so the retransmit sweep can skip them instead of resending the whole thing. */
                if (synapseConnection.PendingReliableQueue.TryGetValue(sequence, out SynapseConnection.PendingReliable? partiallyAcked))
                {
                    int bitmapLength = length - headerSize;

                    if (bitmapLength > 0 && partiallyAcked.ApplyAckedBitmap(buffer.AsSpan(headerSize, bitmapLength)))
                    {
                        // Every segment confirmed: the message is delivered, so drop it like a full ack would.
                        synapseConnection.PendingReliableQueue.Remove(sequence);
                        SynapseConnection.ReleasePendingReliable(partiallyAcked);
                    }
                }

                return;
            }

            case PacketType.Ack:
            {
                /* An ack datagram carries one or more sequences packed back to back after the type byte. Remove
                 * each immediately (stopping retransmission and freeing backpressure) and return its buffer now:
                 * the engine is single-threaded, so no retransmit can be reading it concurrently. */
                for (int offset = PacketHeader.TypeSize; offset + PacketHeader.SequenceSize <= length; offset += PacketHeader.SequenceSize)
                {
                    ushort ackedSequence = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

                    if (synapseConnection.PendingReliableQueue.Remove(ackedSequence, out SynapseConnection.PendingReliable? acked))
                        SynapseConnection.ReleasePendingReliable(acked);
                }

                return;
            }
        }

        int payloadLength = length - headerSize;

        if (payloadLength < 0)
        {
            _telemetry.OnSecurityDroppedReceived();
            ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.Malformed, length, "Negative payload length", ViolationAction.KickAndBlacklist);
            return;
        }

        if (type is PacketType.Segmented or PacketType.ReliableSegmented)
        {
            /* Ungated: a bound on memory a remote peer controls is not a switchable feature. The product is a
             * sound upper bound only because TryReassemble now refuses any segment larger than the MTU; widened
             * to ulong so a large configured MTU cannot wrap the multiplication. */
            if ((ulong)segmentCount * _effectiveMaximumTransmissionUnit > _effectiveMaximumReassembledPacketSize)
            {
                _telemetry.OnSecurityDroppedReceived();
                ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.Oversized, length, ViolationSegmentAssemblyOversized, ViolationAction.KickAndBlacklist);
                return;
            }
        }

        switch (type)
        {
            case PacketType.Reliable:
            {
                byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                Buffer.BlockCopy(buffer, headerSize, payloadBuffer, 0, payloadLength);

                EnqueueOrSendAck(synapseConnection, sequence);

                ArraySegment<byte> payload = new(payloadBuffer, 0, payloadLength);
                DeliverOrdered(synapseConnection, sequence, payload, isReliable: true);

                return;
            }

            case PacketType.ReliableSegmented:
            {
                if (_config.Segment.ReliableEnabled)
                {
                    /* Read straight out of the receive buffer. TryReassemble copies each segment into the
                     * assembly's own storage anyway, so renting an intermediate buffer just to copy into it and
                     * immediately return it was a second copy and a pool round-trip per segment. */
                    PacketReassembler reassembler = GetOrRentReassembler(synapseConnection);

                    if (reassembler.TryReassemble(segmentId, segmentIndex, segmentCount, buffer.AsSpan(headerSize, payloadLength), isReliable: true, out ArraySegment<byte> assembledPayload, out bool isProtocolViolation, out bool isDuplicateSegment))
                    {
                        EnqueueOrSendAck(synapseConnection, sequence);
                        DeliverOrdered(synapseConnection, sequence, assembledPayload, isReliable: true);
                    }
                    else if (isDuplicateSegment || segmentIndex == segmentCount - 1)
                    {
                        /* Report what we hold, so the sender repairs the gap instead of resending the message.
                         *
                         * Two triggers. The last segment arriving while the assembly is still incomplete means the
                         * burst finished and something was lost. Reporting immediately avoids the sender's first
                         * blind retransmit of everything. A duplicate segment covers the case where the last one
                         * was itself lost, so the only signal is the sender retransmitting. */
                        Span<byte> receivedBitmap = stackalloc byte[PacketHeader.SegmentAckBitmapSize];

                        if (reassembler.TryWriteReceivedBitmap(segmentId, receivedBitmap, out _))
                            _sender.SendSegmentAck(synapseConnection, sequence, receivedBitmap);
                    }
                    else if (isProtocolViolation)
                    {
                        _telemetry.OnSecurityDroppedReceived();
                        ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.Malformed, length, ViolationSegmentMismatch, ViolationAction.KickAndBlacklist);
                    }
                }

                return;
            }

            case PacketType.Segmented:
            {
                if (_config.Segment.UnreliableMode != UnreliableSegmentMode.Disabled)
                {
                    // See the reliable case: the assembly copies internally, so the intermediate rental was dead weight.
                    PacketReassembler reassembler = GetOrRentReassembler(synapseConnection);

                    if (reassembler.TryReassemble(segmentId, segmentIndex, segmentCount, buffer.AsSpan(headerSize, payloadLength), isReliable: false, out ArraySegment<byte> assembledPayload, out bool isProtocolViolation, out _))
                    {
                        PayloadDelivered?.Invoke(synapseConnection, assembledPayload, isReliable: false, isPayloadRented: true);
                    }
                    else if (isProtocolViolation)
                    {
                        _telemetry.OnSecurityDroppedReceived();
                        ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.Malformed, length, ViolationSegmentMismatch, ViolationAction.KickAndBlacklist);
                    }
                }

                return;
            }
        }
    }

    /// <summary>
    /// Returns the existing reassembler for <paramref name="synapseConnection"/>, or rents a fresh one from the pool and atomically assigns it.
    /// If two threads race, the loser's instance is returned to the pool and the winner's instance is used.
    /// </summary>
    /// <param name="synapseConnection">The connection whose reassembler is needed.</param>
    /// <returns>The initialised <see cref="PacketReassembler"/> assigned to the connection.</returns>
    private PacketReassembler GetOrRentReassembler(SynapseConnection synapseConnection)
    {
        if (synapseConnection.Reassembler is not null)
            return synapseConnection.Reassembler;

        PacketReassembler rented = ResettableObjectPool<PacketReassembler>.Rent();
        uint effectiveMax = _config.Segment.MaximumSegments == 0 ? 255u : _config.Segment.MaximumSegments;
        rented.Initialize(_effectiveMaximumTransmissionUnit, effectiveMax, _config.Segment.MaximumConcurrentAssembliesPerConnection);

        PacketReassembler? existing = Interlocked.CompareExchange(ref synapseConnection.Reassembler, rented, null);

        if (existing is not null)
        {
            ResettableObjectPool<PacketReassembler>.Return(rented);
            return existing;
        }

        return rented;
    }

    /// <summary>
    /// Delivers in-order isReliable payloads and drains any consecutive buffered packets from the reorder buffer.
    /// </summary>
    /// <param name="synapseConnection">The connection the payload belongs to.</param>
    /// <param name="sequence">The sequence number of the arriving packet.</param>
    /// <param name="payload">The payload bytes to deliver.</param>
    /// <param name="isReliable">True if the payload was sent reliably; forwarded to <see cref="PayloadDelivered"/>.</param>
    /// <remarks>
    /// Every payload reaching this method is backed by a buffer rented from <see cref="ArrayPool{T}.Shared"/>, whether it
    /// arrived unsegmented, was reassembled, or was held in the reorder buffer. Ownership therefore always transfers to
    /// the delivery, and the buffer is returned here on each path that drops a payload instead of delivering it.
    /// </remarks>
    private void DeliverOrdered(SynapseConnection synapseConnection, ushort sequence, ArraySegment<byte> payload, bool isReliable)
    {
        if (sequence != synapseConnection.NextExpectedSequence)
        {
            // Half-space comparison: if (sequence - NextExpectedSequence) wraps past the midpoint,
            // the sequence is "behind", a retransmit of an already-delivered packet. Discard it.
            if (unchecked((ushort)(sequence - synapseConnection.NextExpectedSequence)) >= 32768)
            {
                if (payload.Array is not null)
                    ArrayPool<byte>.Shared.Return(payload.Array);
                return;
            }

            // Out of order - buffer (only if not already received).
            /* Ungated for the same reason as the assembly bound above: with Security.Enabled false the half-space
             * check still admits sequences up to 32,767 ahead, so one peer could pin that many pooled payload
             * buffers by never sending the gap. */
            if (synapseConnection.ReorderBuffer.Count >= _effectiveMaximumOutOfOrderReliablePackets)
            {
                if (payload.Array is not null)
                    ArrayPool<byte>.Shared.Return(payload.Array);

                ViolationOccurred?.Invoke(synapseConnection.RemoteEndPoint, synapseConnection.Signature, ViolationReason.Oversized, 0, ViolationReorderBufferExceeded, ViolationAction.KickAndBlacklist);
                return;
            }

            if (!synapseConnection.ReorderBuffer.TryAdd(sequence, payload) && payload.Array is not null)
                ArrayPool<byte>.Shared.Return(payload.Array);

            return;
        }

        synapseConnection.NextExpectedSequence++;

        /* Loss-free case, which is the overwhelming majority: nothing was buffered behind this packet, so there is
         * no batch to assemble. Renting a pooled list to carry exactly one element and returning it immediately is
         * pure overhead on the dominant path. The sequence bookkeeping is already complete here, so a handler that
         * re-enters the engine still sees consistent state. */
        if (!synapseConnection.ReorderBuffer.TryGetValue(synapseConnection.NextExpectedSequence, out ArraySegment<byte> nextPayload))
        {
            PayloadDelivered?.Invoke(synapseConnection, payload, isReliable, isPayloadRented: true);
            return;
        }

        /* This packet closed a gap, so drain everything now contiguous. The batch is assembled before any handler
         * runs, so the reorder buffer and sequence are fully settled if a callback re-enters the engine. */
        List<ArraySegment<byte>> toDeliver = ListPool<ArraySegment<byte>>.Rent();
        toDeliver.Add(payload);

        do
        {
            synapseConnection.ReorderBuffer.Remove(synapseConnection.NextExpectedSequence);
            synapseConnection.NextExpectedSequence++;
            toDeliver.Add(nextPayload);
        }
        while (synapseConnection.ReorderBuffer.TryGetValue(synapseConnection.NextExpectedSequence, out nextPayload));

        // Deliver after the sequence/reorder bookkeeping is done so user handlers may safely re-enter the engine
        // (e.g. SendReliable) from within the callback. No lock is needed. The engine is single-threaded.
        try
        {
            foreach (ArraySegment<byte> deliverPayload in toDeliver)
                PayloadDelivered?.Invoke(synapseConnection, deliverPayload, isReliable, isPayloadRented: true);
        }
        finally
        {
            ListPool<ArraySegment<byte>>.Return(toDeliver);
        }
    }

    /// <summary>
    /// Processes an inbound handshake: checks blacklist, connection cap, replay cache, and signature validation.
    /// Registers the connection on success and sends a handshake-ack.
    /// </summary>
    /// <param name="fromEndPoint">The source endpoint of the handshake datagram.</param>
    /// <param name="buffer">The raw receive buffer.</param>
    /// <param name="headerSize">Byte offset where the payload begins in <paramref name="buffer"/>.</param>
    /// <param name="length">Total number of valid bytes in <paramref name="buffer"/>.</param>
    private void ProcessHandshake(IPEndPoint fromEndPoint, byte[] buffer, int headerSize, int length)
    {
        long entryTicks = Clock.Ticks;

        ReadOnlySpan<byte> handshakePayload = buffer.AsSpan(headerSize, length - headerSize);
        ulong signature = _security.ComputeSignature(fromEndPoint, handshakePayload);

        if (_security.IsBlacklisted(signature))
        {
            // Blacklisted handshake = REJECTION, not violation.
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.Blacklisted, null);
            return;
        }

        /* A handshake shorter than a nonce is malformed. Rejecting it here also denies the cheapest reflection
         * shape: a 1-byte datagram that would otherwise draw a full challenge in reply. */
        if (handshakePayload.Length < PacketHeader.HandshakeNonceSize)
            return;

        bool hasConnection = _connections.ConnectionsByEndPoint.TryGetValue(fromEndPoint, out SynapseConnection? existingConnection);

        /* We initiated to this peer and it is asking us to prove we can receive at the address we claimed. Echo the
         * challenge back verbatim and stay Pending; the peer allocates nothing until this proof lands. */
        if (hasConnection && existingConnection!.State == ConnectionState.Pending && handshakePayload.Length == PacketHeader.HandshakeChallengeSize)
        {
            _sender.SendHandshakePayload(fromEndPoint, handshakePayload);
            return;
        }

        /* Return-routability gate. Above the occupancy threshold an unknown endpoint gets a stateless token instead
         * of a connection: no table entry, no replay-cache entry, no keep-alive stream aimed at an address that may
         * not be theirs. A spoofed source never receives the challenge and so can never consume a slot. Endpoints we
         * already hold a connection for skip this. We chose to talk to them. */
        if (!hasConnection && _connections.Count >= _handshakeChallengeThreshold)
        {
            bool isProven = handshakePayload.Length == PacketHeader.HandshakeChallengeSize
                && VerifyEndpointToken(_handshakeChallengeHmac, fromEndPoint, handshakePayload[PacketHeader.HandshakeNonceSize..]);

            if (!isProven)
            {
                SendHandshakeChallenge(fromEndPoint, handshakePayload);
                return;
            }
        }

        // Connection cap: reject new peers when the engine is full.
        if (!hasConnection && _connections.Count >= _effectiveMaximumConcurrentConnections)
        {
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.ServerFull, "Connection limit reached");
            return;
        }

        // Replay check: mix the handshake nonce into the cache key independently of the connection signature.
        // The connection signature is IP-based (stable across reconnects) so blacklisting survives reconnects.
        // The replay key adds the nonce so each handshake is unique, reconnections from the same IP are
        // not incorrectly rejected, and the nonce is meaningfully consumed.
        long nowTicks = Clock.Ticks;
        ulong replayKey = MixHandshakeNonce(signature, handshakePayload);

        /* Bounded. The key folds in the peer-supplied nonce, so one peer mints a fresh entry per handshake it
         * sends; without a ceiling the table grows for as long as the flood lasts. Expired entries are swept
         * first, and only if that recovers nothing is the table cleared. Briefly re-admitting replays under
         * active flood is a far smaller problem than unbounded growth. */
        if (_seenHandshakes.Count >= SynapseManager.MaximumReplayCacheEntries)
        {
            RemoveExpiredHandshakeEntries(nowTicks, ReplayCacheEntryLifetimeTicks);

            if (_seenHandshakes.Count >= SynapseManager.MaximumReplayCacheEntries)
                _seenHandshakes.Clear();
        }

        if (!_seenHandshakes.TryAdd(replayKey, nowTicks))
        {
            // Exact same bytes received again - replay.
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.SignatureRejected, "Handshake replay detected");
            return;
        }

        if (_config.Security.SignatureValidator is not null && !_config.Security.SignatureValidator.Validate(fromEndPoint, signature, handshakePayload))
        {
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.SignatureRejected, "Validator returned false");
            return;
        }

        SynapseConnection synapseConnection = _connections.GetOrAdd(fromEndPoint, signature, out bool isExistingConnection);

        /* A handshake arriving hard on the heels of one we just sent to this peer is that peer's answer, not a fresh
         * request. Treating it as a request would reset the live session and emit yet another handshake, which the
         * peer (running this same code) would answer in kind. That is a self-sustaining reset loop: one injected
         * handshake against an established pair leaves both sides wiping their sequence spaces forever. Recognising
         * our own answer terminates the exchange after a single round. */
        bool isAnswerToOurHandshake = isExistingConnection
            && synapseConnection.LastHandshakeSentTicks != 0
            && entryTicks - synapseConnection.LastHandshakeSentTicks < HandshakeAnswerWindowTicks;

        // True when the peer reconnected without a clean disconnect. We need to respond with a
        // handshake-ack in this case just as we would for a brand-new connection.
        bool isReconnecting = isExistingConnection && synapseConnection.State == ConnectionState.Connected && !isAnswerToOurHandshake;

        if (isReconnecting)
        {
            // Peer reconnected without a clean disconnect (e.g. dropped disconnect packet).
            // Reset per-session state so the new session starts with fresh sequence numbers
            // and a clean reorder buffer, then fall through to normal connection initialisation.
            synapseConnection.ResetForReconnect();
            ConnectionClosed?.Invoke(synapseConnection);
        }

        if (!isExistingConnection || synapseConnection.State != ConnectionState.Connected)
        {
            long establishedTicks = Clock.Ticks;

            synapseConnection.State = ConnectionState.Connected;
            synapseConnection.LastReceivedTicks = establishedTicks;
            // The handshake exchange that got us here is fresh traffic in both directions.
            // Stamping last-sent too holds the keep-alive sweep a full interval out instead of firing on the first pass.
            synapseConnection.LastSentTicks = establishedTicks;
            synapseConnection.TransmissionEngine = _sender;

            // Send a handshake-ack only when this side did not initiate the connection.
            // If the connection was already in our table as Pending, we are the client waiting
            // for the server's reply, echoing back would create an infinite ping-pong.
            // We do respond for brand-new connections (!isExistingConnection) and for forced
            // reconnects where the peer was previously fully Connected (isReconnecting).
            if (!isExistingConnection || isReconnecting)
            {
                _sender.SendHandshake(fromEndPoint);
                synapseConnection.LastHandshakeSentTicks = Clock.Ticks;
            }

            ConnectionEstablished?.Invoke(synapseConnection);
        }
    }

    /// <summary>
    /// Evicts stale entries from the handshake replay cache.
    /// </summary>
    /// <param name="nowTicks">Current timestamp in <see cref="System.DateTime.Ticks"/>.</param>
    /// <param name="staleTicks">Age in ticks beyond which a cached handshake signature is considered expired.</param>
    private void RemoveExpiredHandshakeEntries(long nowTicks, long staleTicks) => RemoveExpiredEntries(_seenHandshakes, nowTicks, staleTicks);

    /// <summary>
    /// Mixes <paramref name="payload"/> into <paramref name="signature"/> using FNV-1a to produce a per-handshake replay cache key.
    /// The connection signature remains IP-based (stable across reconnects) for blacklisting; this key adds the nonce
    /// so that each handshake produces a distinct cache entry and the nonce is meaningfully consumed.
    /// Returns <paramref name="signature"/> unchanged when <paramref name="payload"/> is empty.
    /// </summary>
    private static ulong MixHandshakeNonce(ulong signature, ReadOnlySpan<byte> payload)
    {
        const ulong FnvPrime = 1099511628211UL;
        ulong key = signature;

        /* Only the nonce the protocol defines is folded in. Hashing the whole payload let a peer decide how much
         * work each of its handshakes cost, on a path that runs before any rate limit applies. */
        int mixLength = Math.Min(payload.Length, PacketHeader.HandshakeNonceSize);

        for (int i = 0; i < mixLength; i++)
        {
            key ^= payload[i];
            key *= FnvPrime;
        }

        return key;
    }

    /// <summary>
    /// Queues an ACK for batch delivery when batching is enabled, or sends it immediately when disabled.
    /// </summary>
    /// <param name="synapseConnection">The connection whose ACK queue receives the sequence, or that receives the immediate send when batching is disabled.</param>
    /// <param name="sequence">The reliable sequence number being acknowledged.</param>
    private void EnqueueOrSendAck(SynapseConnection synapseConnection, ushort sequence)
    {
        if (_isAckBatchingEnabled)
            synapseConnection.PendingAcks.Enqueue(sequence);
        else
            _sender.SendAck(synapseConnection, sequence);
    }

    /// <summary>
    /// Removes entries from <paramref name="dictionary"/> whose tick-stamped values are older than <paramref name="staleTicks"/> relative to <paramref name="nowTicks"/>.
    /// </summary>
    /// <param name="dictionary">The dictionary to prune.</param>
    /// <param name="nowTicks">Current timestamp in <see cref="DateTime.Ticks"/>.</param>
    /// <param name="staleTicks">Age threshold in ticks; entries older than this are removed.</param>
    private static void RemoveExpiredEntries<TKey>(ConcurrentDictionary<TKey, long> dictionary, long nowTicks, long staleTicks) where TKey : notnull
    {
        foreach (KeyValuePair<TKey, long> entry in dictionary)
        {
            if (nowTicks - entry.Value > staleTicks)
                dictionary.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>
    /// Zero-allocation dictionary key covering both IPv4 (4 bytes packed into <c>_upper64</c>)
    /// and IPv6 (16 bytes split across <c>_upper64</c> and <c>_lower64</c> via stackalloc + MemoryMarshal).
    /// </summary>
    private readonly struct IpKey : IEquatable<IpKey>
    {
        /// <summary>
        /// High 64 bits of the address (holds the full IPv4 address or the first 8 bytes of an IPv6 address).
        /// </summary>
        private readonly ulong _upper64;
        /// <summary>
        /// Low 64 bits of the address (zero for IPv4; bytes 8-15 of an IPv6 address).
        /// </summary>
        private readonly ulong _lower64;

        /// <summary>
        /// Initialises an <see cref="IpKey"/> from its raw 128-bit representation.
        /// </summary>
        /// <param name="upper64">High 64 bits of the address.</param>
        /// <param name="lower64">Low 64 bits of the address.</param>
        private IpKey(ulong upper64, ulong lower64)
        {
            _upper64 = upper64;
            _lower64 = lower64;
        }

        /// <summary>
        /// Creates an <see cref="IpKey"/> from the raw bytes of the given <paramref name="address"/>.
        /// </summary>
        public static IpKey From(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                Span<byte> b = stackalloc byte[4];
                address.TryWriteBytes(b, out _);
                return new(MemoryMarshal.Read<uint>(b), 0UL);
            }
            else
            {
                Span<byte> b = stackalloc byte[16];
                address.TryWriteBytes(b, out _);
                return new(MemoryMarshal.Read<ulong>(b), MemoryMarshal.Read<ulong>(b.Slice(8)));
            }
        }

        /// <inheritdoc/>
        public bool Equals(IpKey other) => _upper64 == other._upper64 && _lower64 == other._lower64;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is IpKey other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(_upper64, _lower64);
    }
}
