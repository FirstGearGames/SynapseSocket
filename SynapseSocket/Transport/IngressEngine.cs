using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    /// True when the ingress receive loop is running.
    /// </summary>
    public bool IsRunning { get; private set; }
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
    /// UTC ticks of the last stale-entry eviction pass for <see cref="_natProbeLastResponseTicks"/>.
    /// </summary>
    private long _lastProbeEvictionTicks;
    /// <summary>
    /// Replay cache mapping handshake signature to first-seen UTC ticks.
    /// Prevents replayed handshakes from re-establishing connections after the original session ends.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, long> _seenHandshakes = [];
    /// <summary>
    /// UTC ticks of the last stale-entry eviction pass for <see cref="_seenHandshakes"/>.
    /// </summary>
    private long _lastHandshakeEvictionTicks;
    /// <summary>
    /// Server secret used to sign NAT challenge tokens. Generated once at construction and never transmitted.
    /// </summary>
    private readonly byte[] _natChallengeSecret = new byte[32];
    /// <summary>
    /// Pending ACKs queued for batch delivery when <see cref="SynapseSocket.Core.Configuration.ReliableConfig.AckBatchingEnabled"/> is true.
    /// Drained by <see cref="FlushPendingAcks"/>.
    /// </summary>
    private readonly ConcurrentQueue<(SynapseConnection Connection, ushort Sequence)> _pendingAcks = new();
    /// <summary>
    /// Raised when a complete payload (unsegmented or fully reassembled) is ready for the application layer.
    /// </summary>
    public event PayloadDeliveredDelegate? PayloadDelivered;
    /// <summary>
    /// Raised when a new connection is established via a successful handshake.
    /// </summary>
    public event ConnectionDelegate? ConnectionEstablished;
    /// <summary>
    /// Raised when a remote peer sends a disconnect packet.
    /// </summary>
    public event ConnectionDelegate? ConnectionClosed;
    /// <summary>
    /// Raised when a connection attempt is rejected before it can be established.
    /// </summary>
    public event ConnectionFailedCallbackDelegate? ConnectionFailed;
    /// <summary>
    /// Raised when a protocol violation is detected on the ingress path.
    /// </summary>
    public event ViolationCallbackDelegate? ViolationOccurred;
    /// <summary>
    /// Raised when an unexpected exception escapes the receive loop.
    /// </summary>
    public event UnhandledExceptionDelegate? UnhandledException;

    private const string ViolationSegmentAssemblyOversized = "Declared segment assembly size exceeds MaximumReassembledPacketSize.";
    private const string ViolationSegmentMismatch = "Segment resent with mismatched segment count or reliability flag.";
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
    public IngressEngine(Socket socket, SynapseConfig config, SecurityProvider security, ConnectionManager connections, TransmissionEngine sender, Telemetry telemetry)
    {
        _socket = socket;
        _config = config;
        _security = security;
        _connections = connections;
        _sender = sender;
        _telemetry = telemetry;
        System.Security.Cryptography.RandomNumberGenerator.Fill(_natChallengeSecret);
    }

    /// <summary>
    /// Starts the async receive loop on the thread pool.
    /// </summary>
    /// <param name="cancellationToken">Token that signals the receive loop to stop.</param>
    /// <returns>A task that completes when the receive loop exits.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        return Task.Run(() => ReceiveLoopAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Core receive loop: awaits datagrams, runs lowest-level filters, and dispatches to packet handlers.
    /// Runs until <paramref name="cancellationToken"/> is cancelled or the socket is disposed.
    /// </summary>
    /// <param name="cancellationToken">Token that signals the loop to exit cleanly.</param>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        EndPoint anyEndPoint = _socket.AddressFamily == AddressFamily.InterNetworkV6 ? new(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);

        // Always receive into a max-UDP-sized buffer so that oversized datagrams are not silently truncated by the kernel — we want to see them so the security layer can raise an Oversized violation.
        const int MaximumUdpDatagramSize = 65535;

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(MaximumUdpDatagramSize);
            bool isPayloadCopied = true;

            try
            {
                SocketReceiveFromResult socketReceiveResult;
                try
                {
                    socketReceiveResult = await _socket.ReceiveFromAsync(new(rentedBuffer, 0, MaximumUdpDatagramSize), SocketFlags.None, anyEndPoint).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException socketException) when (socketException.SocketErrorCode == SocketError.MessageSize)
                {
                    // Datagram larger than our buffer (should not happen with 64K, but be defensive).
                    // Report as oversized from unknown endpoint.
                    ViolationOccurred?.Invoke(new(IPAddress.Any, 0), 0, ViolationReason.Oversized, 0, "MessageSize", ViolationAction.KickAndBlacklist);
                    continue;
                }
                catch (SocketException)
                {
                    continue;
                }

                IPEndPoint fromEndPoint = (IPEndPoint)socketReceiveResult.RemoteEndPoint;
                int receivedLength = socketReceiveResult.ReceivedBytes;

                // Lowest-level mitigation first, before any copy.
                // Established connections skip signature recomputation and blacklist lookup — those only apply at handshake time. Size and rate-limit checks still run for all senders.
                FilterResult filterResult;
                ulong signature;

                if (_connections.TryGet(fromEndPoint, out SynapseConnection? synapseConnection) && synapseConnection is not null)
                {
                    signature = synapseConnection.Signature;
                    filterResult = _security.InspectEstablished(receivedLength, signature);
                }
                else
                    filterResult = _security.InspectNew(fromEndPoint, receivedLength, out signature);

                if (filterResult != FilterResult.Allowed)
                {
                    _telemetry.OnDroppedIn();

                    if (filterResult == FilterResult.Blacklisted)
                    {
                        // Blacklisted = REJECTION, not a per-packet violation.
                        // The peer is already known-bad; surface a connection-rejected event.
                        ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.Blacklisted, filterResult.ToString());
                        continue;
                    }

                    ViolationReason violationReason = filterResult switch
                    {
                        FilterResult.Oversized => ViolationReason.Oversized,
                        FilterResult.RateLimited => ViolationReason.RateLimitExceeded,
                        _ => ViolationReason.Malformed
                    };
                    ViolationOccurred?.Invoke(fromEndPoint, signature, violationReason, receivedLength, filterResult.ToString(), ViolationAction.KickAndBlacklist);
                    continue;
                }

                _telemetry.OnReceived(receivedLength);

                ProcessPacket(rentedBuffer, receivedLength, fromEndPoint, cancellationToken, ref isPayloadCopied);
            }
            catch (OperationCanceledException)
            {
                // Shutdown in progress - exit cleanly.
                break;
            }
            catch (Exception unexpectedException)
            {
                // Unexpected bug in the processing path. Surface it and keep the loop alive so a single bad packet cannot silently kill the receive loop.
                UnhandledException?.Invoke(unexpectedException);
            }
            finally
            {
                // When CopyReceivedPayloads is false, ownership of rentedBuffer is transferred to the PayloadDelivered subscriber (via OnPayloadDelivered), which returns it to the pool.
                if (isPayloadCopied)
                    ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: false);
            }
        }
        IsRunning = false;
    }

    /// <summary>
    /// Parses a single received datagram and routes it to the appropriate handler (handshake, data, ack, disconnect, keep-alive, or NAT probe/server).
    /// </summary>
    /// <param name="buffer">The raw receive buffer containing the datagram.</param>
    /// <param name="length">Number of valid bytes in <paramref name="buffer"/>.</param>
    /// <param name="fromEndPoint">The source endpoint of the datagram.</param>
    /// <param name="cancellationToken">Token forwarded to async send helpers.</param>
    /// <param name="isPayloadCopied">
    /// Set to false when ownership of <paramref name="buffer"/> is transferred to a <see cref="PayloadDelivered"/> subscriber so the receive loop does not return it to the pool.
    /// </param>
    private void ProcessPacket(byte[] buffer, int length, IPEndPoint fromEndPoint, CancellationToken cancellationToken, ref bool isPayloadCopied)
    {
        PacketType type;
        ushort sequence;
        ushort segmentId;
        byte segmentIndex;
        byte segmentCount;
        int headerSize;

        try
        {
            headerSize = PacketHeader.Read(buffer.AsSpan(0, length), out type, out sequence, out segmentId, out segmentIndex, out segmentCount);
        }
        catch
        {
            _telemetry.OnDroppedIn();
            ulong signature = _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);
            ViolationOccurred?.Invoke(fromEndPoint, signature, ViolationReason.Malformed, length, "Header parse failure", ViolationAction.KickAndBlacklist);
            return;
        }

        // Connection-less packet types — handled before any connection lookup.
        switch (type)
        {
            case PacketType.Handshake:
                ProcessHandshake(fromEndPoint, buffer, headerSize, length, cancellationToken);
                return;

            case PacketType.NatProbe:
                ProcessNatProbe(fromEndPoint, cancellationToken);
                return;

            case PacketType.NatChallenge:
                ProcessNatChallengeExchange(fromEndPoint, buffer.AsSpan(headerSize, length - headerSize), cancellationToken);
                return;

            case PacketType.NatRegister:
            case PacketType.NatHeartbeat:
            case PacketType.NatHeartbeatAck:
            case PacketType.NatPeerReady:
            case PacketType.NatSessionFull:
                ProcessNatServerPacket(fromEndPoint, type, buffer.AsSpan(headerSize, length - headerSize));
                return;
        }

        if (!_connections.TryGet(fromEndPoint, out SynapseConnection? synapseConnection) || synapseConnection is null)
        {
            _telemetry.OnDroppedIn();
            return;
        }

        synapseConnection.LastReceivedTicks = DateTime.UtcNow.Ticks;

        switch (type)
        {
            case PacketType.Disconnect:
                synapseConnection.State = ConnectionState.Disconnected;
                _connections.Remove(fromEndPoint, out _);
                ConnectionClosed?.Invoke(synapseConnection);
                ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.PeerDisconnect, 0, null, ViolationAction.Ignore);
                return;

            case PacketType.KeepAlive:
                return;

            case PacketType.Ack:
                if (synapseConnection.PendingReliableQueue.TryRemove(sequence, out SynapseConnection.PendingReliable? acked))
                    SynapseConnection.ReleasePendingReliable(acked);
                return;
        }

        int payloadLength = length - headerSize;

        if (payloadLength < 0)
        {
            _telemetry.OnDroppedIn();
            ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.Malformed, length, "Negative payload length", ViolationAction.KickAndBlacklist);
            return;
        }

        if ((type == PacketType.Segmented || type == PacketType.ReliableSegmented) && _config.MaximumReassembledPacketSize > 0)
        {
            if (segmentCount * _config.MaximumTransmissionUnit > _config.MaximumReassembledPacketSize)
            {
                _telemetry.OnDroppedIn();
                ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature,
                    ViolationReason.Oversized, length,
                    ViolationSegmentAssemblyOversized,
                    ViolationAction.KickAndBlacklist);
                return;
            }
        }

        switch (type)
        {
            case PacketType.Reliable:
            {
                byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                Buffer.BlockCopy(buffer, headerSize, payloadBuffer, 0, payloadLength);
                ArraySegment<byte> payload = new(payloadBuffer, 0, payloadLength);
                EnqueueOrSendAck(synapseConnection, sequence, cancellationToken);
                DeliverOrdered(synapseConnection, sequence, payload, isReliable: true);
                return;
            }

            case PacketType.ReliableSegmented:
            {
                if (_config.MaximumSegments is not SynapseConfig.DisabledMaximumSegments)
                {
                    byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                    Buffer.BlockCopy(buffer, headerSize, payloadBuffer, 0, payloadLength);
                    ArraySegment<byte> payload = new(payloadBuffer, 0, payloadLength);
                    PacketReassembler reassembler = GetOrRentReassembler(synapseConnection);

                    if (reassembler.TryReassemble(segmentId, segmentIndex, segmentCount, payload, isReliable: true, out ArraySegment<byte> assembledPayload, out bool isProtocolViolation))
                    {
                        EnqueueOrSendAck(synapseConnection, sequence, cancellationToken);
                        DeliverOrdered(synapseConnection, sequence, assembledPayload, isReliable: true);
                    }
                    else if (isProtocolViolation)
                    {
                        _telemetry.OnDroppedIn();
                        ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature,
                            ViolationReason.Malformed, length,
                            ViolationSegmentMismatch,
                            ViolationAction.KickAndBlacklist);
                        ArrayPool<byte>.Shared.Return(payloadBuffer);
                        return;
                    }

                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                }

                return;
            }

            case PacketType.Segmented:
            {
                if (_config.MaximumSegments != SynapseConfig.DisabledMaximumSegments)
                {
                    byte[] segmentPayloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                    Buffer.BlockCopy(buffer, headerSize, segmentPayloadBuffer, 0, payloadLength);
                    PacketReassembler reassembler = GetOrRentReassembler(synapseConnection);

                    if (reassembler.TryReassemble(segmentId, segmentIndex, segmentCount, new(segmentPayloadBuffer, 0, payloadLength), isReliable: false, out ArraySegment<byte> assembledPayload, out bool isProtocolViolation))
                    {
                        PayloadDelivered?.Invoke(synapseConnection, assembledPayload, false);
                    }
                    else if (isProtocolViolation)
                    {
                        _telemetry.OnDroppedIn();
                        ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature,
                            ViolationReason.Malformed, length,
                            ViolationSegmentMismatch,
                            ViolationAction.KickAndBlacklist);
                        ArrayPool<byte>.Shared.Return(segmentPayloadBuffer);
                        return;
                    }

                    ArrayPool<byte>.Shared.Return(segmentPayloadBuffer);
                }

                return;
            }

            case PacketType.None:
            {
                if (!_config.CopyReceivedPayloads)
                {
                    isPayloadCopied = false;
                    PayloadDelivered?.Invoke(synapseConnection, new(buffer, headerSize, payloadLength), false);
                }
                else
                {
                    byte[] payloadCopyBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                    Buffer.BlockCopy(buffer, headerSize, payloadCopyBuffer, 0, payloadLength);
                    PayloadDelivered?.Invoke(synapseConnection, new(payloadCopyBuffer, 0, payloadLength), false);
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
        rented.Initialize(_config.MaximumTransmissionUnit, _config.MaximumSegments);

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
    private void DeliverOrdered(SynapseConnection synapseConnection, ushort sequence, ArraySegment<byte> payload, bool isReliable)
    {
        List<ArraySegment<byte>>? toDeliver = null;

        lock (synapseConnection.ReliableLock)
        {
            if (sequence == synapseConnection.NextExpectedSequence)
            {
                synapseConnection.NextExpectedSequence++;
                toDeliver = [payload];

                while (synapseConnection.ReorderBuffer.TryGetValue(synapseConnection.NextExpectedSequence, out ArraySegment<byte> nextPayload))
                {
                    synapseConnection.ReorderBuffer.Remove(synapseConnection.NextExpectedSequence);
                    synapseConnection.NextExpectedSequence++;
                    toDeliver.Add(nextPayload);
                }
            }
            else
            {
                // Out of order - buffer (only if not already received).
                if (_config.MaximumOutOfOrderReliablePackets > 0
                    && synapseConnection.ReorderBuffer.Count >= _config.MaximumOutOfOrderReliablePackets)
                {
                    if (payload.Array is not null)
                        ArrayPool<byte>.Shared.Return(payload.Array);

                    ViolationOccurred?.Invoke(
                        synapseConnection.RemoteEndPoint,
                        synapseConnection.Signature,
                        ViolationReason.Oversized,
                        0,
                        ViolationReorderBufferExceeded,
                        ViolationAction.KickAndBlacklist);
                    return;
                }

                synapseConnection.ReorderBuffer.TryAdd(sequence, payload);
            }
        }

        // Callbacks happen OUTSIDE the lock so user handlers are free to call back into the engine (e.g., SendReliableAsync) safely.
        if (toDeliver is not null)
        {
            foreach (ArraySegment<byte> deliverPayload in toDeliver)
                PayloadDelivered?.Invoke(synapseConnection, deliverPayload, isReliable);
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
    /// <param name="cancellationToken">Token forwarded to the handshake-ack send.</param>
    private void ProcessHandshake(IPEndPoint fromEndPoint, byte[] buffer, int headerSize, int length, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> handshakePayload = buffer.AsSpan(headerSize, length - headerSize);
        ulong signature = _security.ComputeSignature(fromEndPoint, handshakePayload);

        if (_security.IsBlacklisted(signature))
        {
            // Blacklisted handshake = REJECTION, not violation.
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.Blacklisted, null);
            return;
        }

        // Connection cap: reject new peers when the engine is full.
        if (_config.MaximumConcurrentConnections > 0
            && _connections.Count >= _config.MaximumConcurrentConnections
            && !_connections.TryGet(fromEndPoint, out _))
        {
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.ServerFull, "Connection limit reached");
            return;
        }

        // Replay check: mix the handshake nonce into the cache key independently of the connection signature.
        // The connection signature is IP-based (stable across reconnects) so blacklisting survives reconnects.
        // The replay key adds the nonce so each handshake is unique — reconnections from the same IP are
        // not incorrectly rejected, and the nonce is meaningfully consumed.
        long nowTicks = DateTime.UtcNow.Ticks;
        ulong replayKey = MixHandshakeNonce(signature, handshakePayload);

        if (!_seenHandshakes.TryAdd(replayKey, nowTicks))
        {
            // Exact same bytes received again - replay.
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.SignatureRejected, "Handshake replay detected");
            return;
        }

        // Periodic eviction: keep the replay cache from growing without bound.
        long lastHandshakeEvict = Volatile.Read(ref _lastHandshakeEvictionTicks);

        if (nowTicks - lastHandshakeEvict > TimeSpan.TicksPerMinute)
        {
            if (Interlocked.CompareExchange(ref _lastHandshakeEvictionTicks, nowTicks, lastHandshakeEvict) == lastHandshakeEvict)
                RemoveExpiredHandshakeEntries(nowTicks, _config.Connection.TimeoutMilliseconds * TimeSpan.TicksPerMillisecond * 2);
        }

        if (_config.SignatureValidator is not null && !_config.SignatureValidator.Validate(fromEndPoint, signature, handshakePayload))
        {
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.SignatureRejected, "Validator returned false");
            return;
        }

        bool isNewConnection = !_connections.TryGet(fromEndPoint, out SynapseConnection? _);

        SynapseConnection synapseConnection = _connections.GetOrAdd(fromEndPoint, signature, (remoteEndPoint, remoteSignature) => new(remoteEndPoint, remoteSignature));

        if (isNewConnection || synapseConnection.State != ConnectionState.Connected)
        {
            synapseConnection.State = ConnectionState.Connected;
            synapseConnection.LastReceivedTicks = DateTime.UtcNow.Ticks;
            _ = _sender.SendHandshakeAsync(fromEndPoint, cancellationToken); // handshake-ack = another handshake packet
            ConnectionEstablished?.Invoke(synapseConnection);
        }
    }

    /// <summary>
    /// Evicts stale entries from the handshake replay cache.
    /// </summary>
    /// <param name="nowTicks">The current UTC tick count.</param>
    /// <param name="staleTicks">Age in ticks beyond which a cached handshake signature is considered expired.</param>
    private void RemoveExpiredHandshakeEntries(long nowTicks, long staleTicks) =>
        RemoveExpiredEntries(_seenHandshakes, nowTicks, staleTicks);

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

        foreach (byte b in payload)
        {
            key ^= b;
            key *= FnvPrime;
        }

        return key;
    }

    /// <summary>
    /// Generic helper that removes entries from a <see cref="ConcurrentDictionary{TKey, Long}"/>
    /// whose tick-valued values are older than <paramref name="staleTicks"/> relative to <paramref name="nowTicks"/>.
    /// </summary>
    /// <param name="dictionary">The dictionary to prune.</param>
    /// <param name="nowTicks">The current UTC tick count.</param>
    /// <param name="staleTicks">Age in ticks beyond which an entry is removed.</param>
    /// <typeparam name="TKey">The dictionary key type.</typeparam>
    /// <summary>
    /// Queues an ACK for batch delivery when batching is enabled, or sends it immediately when disabled.
    /// </summary>
    private void EnqueueOrSendAck(SynapseConnection synapseConnection, ushort sequence, CancellationToken cancellationToken)
    {
        if (_config.Reliable.AckBatchingEnabled)
            _pendingAcks.Enqueue((synapseConnection, sequence));
        else
            _ = _sender.SendAckAsync(synapseConnection, sequence, cancellationToken);
    }

    /// <summary>
    /// Drains the pending ACK queue and sends all queued acknowledgements.
    /// Called by the maintenance loop at the configured <see cref="SynapseSocket.Core.Configuration.ReliableConfig.AckBatchIntervalMilliseconds"/> interval.
    /// </summary>
    public void FlushPendingAcks(CancellationToken cancellationToken)
    {
        while (_pendingAcks.TryDequeue(out (SynapseConnection Connection, ushort Sequence) item))
            _ = _sender.SendAckAsync(item.Connection, item.Sequence, cancellationToken);
    }

    private static void RemoveExpiredEntries<TKey>(ConcurrentDictionary<TKey, long> dictionary, long nowTicks, long staleTicks)
        where TKey : notnull
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
