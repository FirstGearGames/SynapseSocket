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

    /// <summary>
    /// Creates a new ingress engine bound to the provided socket.
    /// </summary>
    public IngressEngine(Socket socket, SynapseConfig config, SecurityProvider security, ConnectionManager connections, TransmissionEngine sender, Telemetry telemetry)
    {
        _socket = socket;
        _config = config;
        _security = security;
        _connections = connections;
        _sender = sender;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Starts the async receive loop on the thread pool.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        return Task.Run(() => ReceiveLoopAsync(cancellationToken), cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        EndPoint anyEndPoint = _socket.AddressFamily == AddressFamily.InterNetworkV6 ? new(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);

        // Always receive into a max-UDP-sized buffer so that oversized datagrams are not silently
        // truncated by the kernel - we want to see them so the security layer can raise an Oversized violation.
        const int MaximumUdpDatagramSize = 65535;

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(MaximumUdpDatagramSize);
            bool isBufferHandedOff = false;
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
                // Established connections skip signature recomputation and blacklist lookup - those
                // only apply at handshake time. Size and rate-limit checks still run for all senders.
                FilterResult filterResult;
                ulong signature;
                if (_connections.TryGet(fromEndPoint, out SynapseConnection? inspectedConnection) && inspectedConnection is not null)
                {
                    signature = inspectedConnection.Signature;
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

                ProcessPacket(rentedBuffer, receivedLength, fromEndPoint, cancellationToken, ref isBufferHandedOff);
            }
            catch (OperationCanceledException)
            {
                // Shutdown in progress - exit cleanly.
                break;
            }
            catch (Exception unexpectedException)
            {
                // Unexpected bug in the processing path. Surface it and keep the loop alive
                // so a single bad packet cannot silently kill the receive loop.
                UnhandledException?.Invoke(unexpectedException);
            }
            finally
            {
                // When CopyReceivedUnreliablePayload is false, ownership of rentedBuffer is transferred to
                // the PayloadDelivered subscriber (via OnPayloadDelivered), which returns it to the pool.
                if (!isBufferHandedOff)
                    ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: false);
            }
        }
        IsRunning = false;
    }

    /// <summary>
    /// Parses a single received datagram and routes it to the appropriate handler
    /// (handshake, data, ack, disconnect, keep-alive, or NAT probe/server).
    /// </summary>
    private void ProcessPacket(byte[] buffer, int length, IPEndPoint fromEndPoint, CancellationToken cancellationToken, ref bool isBufferHandedOff)
    {
        PacketFlags flags;
        ushort sequence;
        ushort segmentId;
        byte segmentIndex;
        byte segmentCount;
        int headerSize;

        try
        {
            headerSize = PacketHeader.Read(buffer.AsSpan(0, length), out flags, out sequence, out segmentId, out segmentIndex, out segmentCount);
        }
        catch
        {
            _telemetry.OnDroppedIn();
            ulong signature = _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);
            ViolationOccurred?.Invoke(fromEndPoint, signature, ViolationReason.Malformed, length, "Header parse failure", ViolationAction.KickAndBlacklist);
            return;
        }

        // Handshake from a new peer.
        if ((flags & PacketFlags.Handshake) != 0)
        {
            ProcessHandshake(fromEndPoint, buffer, headerSize, length, cancellationToken);
            return;
        }

        // Extended flag only — NAT probe (no payload) or rendezvous-server message (has payload).
        if (flags == PacketFlags.Extended)
        {
            if (length > headerSize)
                ProcessNatServerPacket(fromEndPoint, buffer.AsSpan(headerSize, length - headerSize));
            else
                ProcessNatProbe(fromEndPoint, cancellationToken);
            return;
        }

        if (!_connections.TryGet(fromEndPoint, out SynapseConnection? synapseConnection) || synapseConnection is null)
        {
            // Not yet established - drop silently.
            _telemetry.OnDroppedIn();
            return;
        }

        synapseConnection.LastReceivedTicks = DateTime.UtcNow.Ticks;

        if ((flags & PacketFlags.Disconnect) != 0)
        {
            synapseConnection.State = ConnectionState.Disconnected;
            _connections.Remove(fromEndPoint, out _);
            ConnectionClosed?.Invoke(synapseConnection);
            // PeerDisconnect is a polite-cause violation: surface it on ViolationDetected with an Ignore default
            // (we already removed/closed the connection above, so there is nothing further to kick or blacklist).
            ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.PeerDisconnect, 0, null, ViolationAction.Ignore);
            return;
        }

        if ((flags & PacketFlags.KeepAlive) != 0)
            return;

        if ((flags & PacketFlags.Ack) != 0)
        {
            if (synapseConnection.PendingReliableQueue.TryRemove(sequence, out SynapseConnection.PendingReliable? acked))
                SynapseConnection.ReturnPendingReliableBuffers(acked);
            return;
        }

        int payloadLength = length - headerSize;
        if (payloadLength < 0)
        {
            _telemetry.OnDroppedIn();
            ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.Malformed, length, "Negative payload length", ViolationAction.KickAndBlacklist);
            return;
        }

        // Validate the declared segment assembly size before allocating anything.
        // A malicious client could claim a large segmentCount to force a large reassembly.
        if ((flags & PacketFlags.Segmented) != 0 && _config.MaximumReassembledPacketSize > 0)
        {
            if (segmentCount * _config.MaximumTransmissionUnit > _config.MaximumReassembledPacketSize)
            {
                _telemetry.OnDroppedIn();
                ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature,
                    ViolationReason.Oversized, length,
                    $"Declared segment assembly ({segmentCount} * {_config.MaximumTransmissionUnit} bytes) exceeds MaximumReassembledPacketSize",
                    ViolationAction.KickAndBlacklist);
                return;
            }
        }

        // Reliable payload: deliver in order.
        // Segmented reliable messages delay the ACK until all segments are reassembled so
        // the sender retransmits the full set if any segment goes missing.
        if ((flags & PacketFlags.Reliable) != 0)
        {
            byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
            Buffer.BlockCopy(buffer, headerSize, payloadBuffer, 0, payloadLength);
            ArraySegment<byte> payload = new(payloadBuffer, 0, payloadLength);

            if ((flags & PacketFlags.Segmented) != 0)
            {
                if (_config.MaximumSegments != SynapseConfig.DisabledMaximumSegments)
                {
                    PacketReassembler reassembler = GetOrRentReassembler(synapseConnection);
                    if (reassembler.TryReassemble(segmentId, segmentIndex, segmentCount, payload, reliable: true, out ArraySegment<byte> assembledPayload, out bool isProtocolViolation))
                    {
                        _ = _sender.SendAckAsync(synapseConnection, sequence, cancellationToken);
                        DeliverOrdered(synapseConnection, sequence, assembledPayload, isReliable: true);
                    }
                    else if (isProtocolViolation)
                    {
                        _telemetry.OnDroppedIn();
                        ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature,
                            ViolationReason.Malformed, length,
                            $"Segment {segmentId} resent with mismatched segmentCount/reliability",
                            ViolationAction.KickAndBlacklist);
                        ArrayPool<byte>.Shared.Return(payloadBuffer);
                        return;
                    }
                }
                ArrayPool<byte>.Shared.Return(payloadBuffer);
                return;
            }

            _ = _sender.SendAckAsync(synapseConnection, sequence, cancellationToken);
            DeliverOrdered(synapseConnection, sequence, payload, isReliable: true);
            return;
        }

        // Unreliable.
        if ((flags & PacketFlags.Segmented) != 0)
        {
            if (_config.MaximumSegments != SynapseConfig.DisabledMaximumSegments)
            {
                byte[] segmentPayloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                Buffer.BlockCopy(buffer, headerSize, segmentPayloadBuffer, 0, payloadLength);
                PacketReassembler reassembler = GetOrRentReassembler(synapseConnection);
                if (reassembler.TryReassemble(segmentId, segmentIndex, segmentCount, new(segmentPayloadBuffer, 0, payloadLength), reliable: false, out ArraySegment<byte> assembledPayload, out bool isProtocolViolation))
                {
                    PayloadDelivered?.Invoke(synapseConnection, assembledPayload, false);
                }
                else if (isProtocolViolation)
                {
                    _telemetry.OnDroppedIn();
                    ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature,
                        ViolationReason.Malformed, length,
                        $"Segment {segmentId} resent with mismatched segmentCount/reliability",
                        ViolationAction.KickAndBlacklist);
                    ArrayPool<byte>.Shared.Return(segmentPayloadBuffer);
                    return;
                }
                ArrayPool<byte>.Shared.Return(segmentPayloadBuffer);
            }
            // Segmented but segmentation disabled - drop silently.
            return;
        }

        // Unreliable non-segmented: either pass the ingress buffer directly (zero-copy) or
        // copy the payload into a fresh pool buffer (safe for immediate caller reuse).
        // When handing off, isBufferHandedOff signals the receive loop not to return rentedBuffer —
        // ownership passes to the PayloadDelivered subscriber, which returns it after callbacks.
        if (!_config.CopyReceivedUnreliablePayload)
        {
            isBufferHandedOff = true;
            PayloadDelivered?.Invoke(synapseConnection, new(buffer, headerSize, payloadLength), false);
        }
        else
        {
            byte[] payloadCopyBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
            Buffer.BlockCopy(buffer, headerSize, payloadCopyBuffer, 0, payloadLength);
            PayloadDelivered?.Invoke(synapseConnection, new(payloadCopyBuffer, 0, payloadLength), false);
        }
    }

    /// <summary>
    /// Returns the existing reassembler for <paramref name="synapseConnection"/>, or rents a fresh one
    /// from the pool and atomically assigns it. If two threads race, the loser's instance is returned
    /// to the pool and the winner's instance is used.
    /// </summary>
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
    /// Delivers in-order reliable payloads and drains any consecutive buffered packets from the reorder buffer.
    /// </summary>
    private void DeliverOrdered(SynapseConnection synapseConnection, ushort sequence, ArraySegment<byte> payload, bool isReliable)
    {
        List<ArraySegment<byte>>? toDeliver = null;

        lock (synapseConnection.ReliableGate)
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
                        $"Reorder buffer cap ({_config.MaximumOutOfOrderReliablePackets}) exceeded",
                        ViolationAction.KickAndBlacklist);
                    return;
                }
                synapseConnection.ReorderBuffer.TryAdd(sequence, payload);
            }
        }

        // Callbacks happen OUTSIDE the lock so user handlers are free
        // to call back into the engine (e.g., SendReliableAsync) safely.
        if (toDeliver is not null)
        {
            foreach (ArraySegment<byte> deliverPayload in toDeliver)
                PayloadDelivered?.Invoke(synapseConnection, deliverPayload, isReliable);
        }
    }

    /// <summary>
    /// Evicts stale entries from the NAT probe response-time dictionary.
    /// </summary>
    private void RemoveExpiredProbeLimitEntries(long nowTicks, long staleTicks) =>
        RemoveExpiredEntries(_natProbeLastResponseTicks, nowTicks, staleTicks);

    /// <summary>
    /// Parses a NAT server packet body into an <see cref="IPEndPoint"/> (address family byte + address + port).
    /// Returns null if the body is malformed or too short.
    /// </summary>
    private static IPEndPoint? TryParsePeerEndPoint(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1)
            return null;
        byte addrFamily = body[0];
        ReadOnlySpan<byte> rest = body[1..];
        if (addrFamily == 4 && rest.Length >= 6)
        {
            IPAddress ip = new(rest[..4]);
            ushort port = (ushort)(rest[4] | (rest[5] << 8));
            return new(ip, port);
        }
        if (addrFamily == 6 && rest.Length >= 18)
        {
            IPAddress ip = new(rest[..16]);
            ushort port = (ushort)(rest[16] | (rest[17] << 8));
            return new(ip, port);
        }
        return null;
    }

    /// <summary>
    /// Processes an inbound handshake: checks blacklist, connection cap, replay cache,
    /// and signature validation. Registers the connection on success and sends a handshake-ack.
    /// </summary>
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

        // Replay check: the nonce embedded in handshakePayload makes each legitimate handshake's
        // signature unique. A replayed packet carries the same bytes -> same signature -> reject.
        long nowTicks = DateTime.UtcNow.Ticks;
        if (!_seenHandshakes.TryAdd(signature, nowTicks))
        {
            // Exact same bytes received again - replay.
            ConnectionFailed?.Invoke(fromEndPoint, ConnectionRejectedReason.SignatureRejected, "Handshake replay detected");
            return;
        }

        // Periodic eviction: keep the replay cache from growing without bound.
        long lastHandshakeEvict = Volatile.Read(ref _lastHandshakeEvictionTicks);
        if (nowTicks - lastHandshakeEvict > TimeSpan.TicksPerMinute)
            if (Interlocked.CompareExchange(ref _lastHandshakeEvictionTicks, nowTicks, lastHandshakeEvict) == lastHandshakeEvict)
                RemoveExpiredHandshakeEntries(nowTicks, _config.Connection.TimeoutMilliseconds * TimeSpan.TicksPerMillisecond * 2);

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
    private void RemoveExpiredHandshakeEntries(long nowTicks, long staleTicks) =>
        RemoveExpiredEntries(_seenHandshakes, nowTicks, staleTicks);

    /// <summary>
    /// Generic helper that removes entries from a <see cref="ConcurrentDictionary{TKey, Long}"/>
    /// whose tick-valued values are older than <paramref name="staleTicks"/> relative to <paramref name="nowTicks"/>.
    /// </summary>
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
        private readonly ulong _upper64;
        private readonly ulong _lower64;

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

        public bool Equals(IpKey other) => _upper64 == other._upper64 && _lower64 == other._lower64;
        public override bool Equals(object? obj) => obj is IpKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_upper64, _lower64);
    }
}
