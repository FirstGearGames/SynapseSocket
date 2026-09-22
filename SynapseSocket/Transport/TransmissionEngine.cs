using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CodeBoost.Performance;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Diagnostics;
using SynapseSocket.Packets;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Transport;

/// <summary>
/// Transmission Engine (Sender).
/// Manages outgoing packet flow for both the unreliable and reliable channels.
/// Sends are synchronous and immediate (blocking <see cref="Socket.SendTo(byte[], int, int, SocketFlags, EndPoint)"/>).
/// The engine is single-threaded and driven by the host's poll, so there are no async continuations.
/// </summary>
public sealed partial class TransmissionEngine
{
    /// <summary>
    /// Primary UDP socket used for all outbound traffic; also handles IPv6 when no dedicated IPv6 socket is provided.
    /// </summary>
    private readonly Socket _ipv4Socket;
    /// <summary>
    /// Optional dedicated IPv6 UDP socket. When non-null, IPv6 datagrams are routed through this socket.
    /// </summary>
    private readonly Socket? _ipv6Socket;
    /// <summary>
    /// Engine configuration snapshot.
    /// </summary>
    private readonly SynapseConfig _config;
    /// <summary>
    /// Telemetry counters for sent-byte and packet tracking.
    /// </summary>
    private readonly Telemetry _telemetry;
    /// <summary>
    /// Latency simulator that may artificially delay or drop outbound packets for testing purposes.
    /// </summary>
    private readonly LatencySimulator _latencySimulator;
    /// <summary>
    /// True if the LatencySimulator is enabled.
    /// </summary>
    private readonly bool _isLatencySimulatorEnabled;
    /// <summary>
    /// Cached delegate for <see cref="SendDirect"/>, handed to the latency simulator to avoid a per-call allocation.
    /// </summary>
    private readonly Action<ArraySegment<byte>, IPEndPoint> _sendDirect;
    /// <summary>
    /// Optional layer that rewrites each outbound payload immediately before the socket write, or null when
    /// <see cref="SynapseConfig.PacketTransform"/> was left unset.
    /// </summary>
    private readonly IPacketTransform? _packetTransform;
    /// <summary>
    /// Scratch buffer the transformed packet is assembled into, allocated once and reused for every send.
    /// Null when no transform is configured, so an engine without one allocates nothing.
    /// </summary>
    /// <remarks>
    /// The engine is single-threaded and the buffer is consumed by the socket write that immediately follows the
    /// transform, so one buffer for the whole engine is enough.
    /// </remarks>
    private readonly byte[]? _transformBuffer;
    /// <summary>
    /// The single remote the engine's socket is OS-connected to when <see cref="SynapseConfig.ConnectedSocketEnabled"/> engaged,
    /// or null for the ordinary any-target mode. Sends to it go through the endpoint-free Send call, no per-datagram target
    /// serialization on any runtime.
    /// </summary>
    private IPEndPoint? _connectedRemoteEndPoint;
    /// <summary>
    /// The OS-connected socket that sends to <see cref="_connectedRemoteEndPoint"/>, resolved once when the
    /// connection is made.
    /// </summary>
    private Socket? _connectedSocket;
#if NET8_0_OR_GREATER
    /// <summary>
    /// Serialized form of each send target, built once per endpoint. The EndPoint-based SendTo serializes the target into a
    /// fresh SocketAddress on every call, two allocations per sent datagram for endpoints that never change, while the
    /// SocketAddress overload sends with none.
    /// </summary>
    private readonly Dictionary<IPEndPoint, SocketAddress> _serializedSendTargets = new(Core.IPEndPointComparer.Default);
    /// <summary>
    /// Ceiling for <see cref="_serializedSendTargets"/>. Steady-state targets are the connected peers, so the cap only ever
    /// engages under a flood of transient handshake targets; clearing simply re-serializes on the next send.
    /// </summary>
    private const int MaximumSerializedSendTargets = 4096;
#else
    /// <summary>
    /// Raw sockaddr per send target, built once per endpoint and reused. The managed SendTo re-serialises the
    /// target into a fresh SocketAddress on every datagram; handing the syscall a prebuilt address costs nothing.
    /// The netstandard2.1 counterpart to the serialized-target cache, which that runtime has no SendTo overload for.
    /// </summary>
    private readonly Dictionary<IPEndPoint, NativeSendTarget> _nativeSendTargets = new(Core.IPEndPointComparer.Default);
    /// <summary>
    /// Ceiling for <see cref="_nativeSendTargets"/>, matching the serialized-target cache.
    /// </summary>
    private const int MaximumNativeSendTargets = 4096;
    /// <summary>
    /// True when this engine sends through the native binding.
    /// </summary>
    private readonly bool _useNativeSend;
#endif

    /// <summary>
    /// Creates a new transmission engine bound to the given sockets.
    /// </summary>
    /// <param name="ipv4Socket">The IPv4 UDP socket used for all outbound traffic.</param>
    /// <param name="ipv6Socket">Optional IPv6 UDP socket; falls back to <paramref name="ipv4Socket"/> when null.</param>
    /// <param name="config">Engine configuration snapshot.</param>
    /// <param name="telemetry">Telemetry counters for sent-byte and packet tracking.</param>
    /// <param name="latency">Latency simulator that may delay or drop outbound packets.</param>
    public TransmissionEngine(Socket ipv4Socket, Socket? ipv6Socket, SynapseConfig config, Telemetry telemetry, LatencySimulator latency)
    {
        _ipv4Socket = ipv4Socket ?? throw new ArgumentNullException(nameof(ipv4Socket));
        _ipv6Socket = ipv6Socket;
        _config = config;
        _telemetry = telemetry;

        _latencySimulator = latency;
        _isLatencySimulatorEnabled = _latencySimulator.IsEnabled;
        _sendDirect = SendDirect;
        _packetTransform = config.PacketTransform;

        // Sized for the largest packet the engine can legally frame plus the headroom the transform reserved,
        // so a transformed packet never runs out of destination.
        if (_packetTransform is not null)
            _transformBuffer = new byte[Math.Max(config.MaximumPacketSize, config.MaximumTransmissionUnit) + _packetTransform.ReservedBytes];

#if !NET8_0_OR_GREATER
        // net8.0 has no native send path to select: its SocketAddress SendTo overload already allocates nothing.
        _useNativeSend = config.NativeReceiveEnabled && NativeSocket.IsSupported;
#endif
    }

    /// <summary>
    /// Routes every future send addressed to the remote through the endpoint-free Send call on the OS-connected socket.
    /// </summary>
    /// <param name="connectedSocket">The socket that has been OS-connected to <paramref name="remoteEndPoint"/>.</param>
    /// <param name="remoteEndPoint">The remote the socket is connected to.</param>
    public void SetConnectedRemote(Socket connectedSocket, IPEndPoint remoteEndPoint)
    {
        _connectedSocket = connectedSocket;
        _connectedRemoteEndPoint = remoteEndPoint;
    }

    /// <summary>
    /// Sends raw bytes to the target endpoint, routing through the latency simulator when enabled.
    /// </summary>
    /// <param name="segment">The wire-ready bytes to send.</param>
    /// <param name="target">The remote endpoint to send to.</param>
    public void SendRaw(ArraySegment<byte> segment, IPEndPoint target)
    {
        if (!_isLatencySimulatorEnabled)
        {
            SendDirect(segment, target);
            return;
        }

        _latencySimulator.Process(segment, target, Clock.Ticks, _sendDirect);
    }

    /// <summary>
    /// Releases any latency-simulator-delayed packets whose due time has elapsed. Called once per engine poll.
    /// No-op when the simulator is disabled.
    /// </summary>
    /// <param name="nowTicks">Current time in <see cref="DateTime.Ticks"/>.</param>
    public void FlushDeferredSends(long nowTicks)
    {
        if (_isLatencySimulatorEnabled)
            _latencySimulator.Flush(nowTicks, _sendDirect);
    }

    /// <summary>
    /// Returns any still-parked latency-simulator buffers to the pool. Called on engine shutdown.
    /// </summary>
    public void ClearDeferredSends()
    {
        if (_isLatencySimulatorEnabled)
            _latencySimulator.Clear();
    }

    /// <summary>
    /// Sends an unreliable, unsegmented payload to the connection's remote endpoint.
    /// Builds a header-only packet, copies the payload after it, and sends immediately.
    /// </summary>
    /// <param name="synapseConnection">The target connection.</param>
    /// <param name="payload">The application payload to send.</param>
    internal void SendUnreliableUnsegmented(SynapseConnection synapseConnection, ArraySegment<byte> payload)
    {
        const PacketType Type = PacketType.None;
        int totalLength = PacketHeader.ComputeHeaderSize(Type) + payload.Count;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            int written = PacketHeader.BuildPacket(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0, payload.AsSpan());
            SendToConnection(new(rentedBuffer, 0, written), synapseConnection);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: false);
        }
    }

    /// <summary>
    /// Sends a reliable, unsegmented payload to the connection's remote endpoint.
    /// Assigns a sequence number, stores a <see cref="SynapseConnection.PendingReliable"/>
    /// entry for retransmission, and sends immediately.
    /// </summary>
    /// <param name="synapseConnection">The target connection.</param>
    /// <param name="payload">The application payload to send reliably.</param>
    internal void SendReliableUnsegmented(SynapseConnection synapseConnection, ArraySegment<byte> payload)
    {
        if (synapseConnection.PendingReliableQueue.Count >= _config.Reliable.MaximumPending)
            throw new InvalidOperationException("Reliable backpressure limit reached.");

        ushort sequence = synapseConnection.NextOutgoingSequence++;

        const PacketType Type = PacketType.Reliable;
        int totalLength = PacketHeader.ComputeHeaderSize(Type) + payload.Count;

        byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
        int written = PacketHeader.BuildPacket(packetBuffer.AsSpan(), Type, sequence, 0, 0, 0, payload.AsSpan());

        List<ArraySegment<byte>> segments = ListPool<ArraySegment<byte>>.Rent();
        segments.Add(new(packetBuffer, 0, written));

        SynapseConnection.PendingReliable pendingReliable = ResettableObjectPool<SynapseConnection.PendingReliable>.Rent();
        pendingReliable.Initialize(segments, packetBuffer, Clock.Ticks);

        ParkPendingReliable(synapseConnection, sequence, pendingReliable);

        SendToConnection(segments[0], synapseConnection);
    }

    /// <summary>
    /// Splits a payload into wire-ready segments and sends them all.
    /// For reliable sends, the segment array is stored in <see cref="SynapseConnection.PendingReliable"/>
    /// and its lifetime is managed by the retransmission sweep and ACK handler.
    /// For unreliable sends, the backing buffer is returned to the pool after the last send.
    /// </summary>
    /// <param name="synapseConnection">The target connection.</param>
    /// <param name="payload">The application payload to split and send.</param>
    /// <param name="isReliable">True to send segments reliably with retransmission; false for unreliable delivery.</param>
    /// <param name="splitter">The <see cref="PacketSplitter"/> instance used to produce the segment array.</param>
    internal void SendSegmented(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable, PacketSplitter splitter)
    {
        if (isReliable && synapseConnection.PendingReliableQueue.Count >= _config.Reliable.MaximumPending)
            throw new InvalidOperationException("Reliable backpressure limit reached.");

        ushort sequence = 0;

        if (isReliable)
            sequence = synapseConnection.NextOutgoingSequence++;

        List<ArraySegment<byte>> segments = splitter.Split(payload.AsSpan(), isReliable, out int segmentCount, sequence, out byte[] backingBuffer);

        // One last-sent stamp covers the whole burst rather than routing each segment through SendToConnection.
        // The segments leave back to back, so a per-segment clock read would buy nothing on a send of up to 255 of them.
        long nowTicks = Clock.Ticks;
        synapseConnection.LastSentTicks = nowTicks;

        if (isReliable)
        {
            SynapseConnection.PendingReliable pendingReliable = ResettableObjectPool<SynapseConnection.PendingReliable>.Rent();
            pendingReliable.Initialize(segments, backingBuffer, nowTicks);

            ParkPendingReliable(synapseConnection, sequence, pendingReliable);

            // Segments are now owned by PendingReliable; do NOT return them here.
            // When the latency simulator is enabled it copies each segment, so an independent random
            // delay per segment produces genuine out-of-order arrival at the receiver.
            for (int i = 0; i < segments.Count; i++)
                SendRaw(segments[i], synapseConnection.RemoteEndPoint);
        }
        // Unreliable does not need to retain buffers.
        else
        {
            try
            {
                for (int i = 0; i < segmentCount; i++)
                    SendRaw(segments[i], synapseConnection.RemoteEndPoint);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(backingBuffer);

                /* The reliable branch hands the list to PendingReliable, which returns it on ack or eviction.
                 * Nothing owns it here, so it has to go back explicitly, otherwise ListPool is permanently
                 * empty and every large unreliable send allocates a fresh list. */
                ListPool<ArraySegment<byte>>.Return(segments);
            }
        }
    }

    /// <summary>
    /// Sends a reliable-channel acknowledgement for the given sequence number.
    /// </summary>
    /// <param name="synapseConnection">The connection to acknowledge.</param>
    /// <param name="sequence">The sequence number being acknowledged.</param>
    public void SendAck(SynapseConnection synapseConnection, ushort sequence)
    {
        const PacketType Type = PacketType.Ack;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, sequence, 0, 0, 0);
        SendAndPoolBuffer(new(rentedBuffer, 0, headerSize), synapseConnection);
    }

    /// <summary>
    /// Drains <paramref name="pendingAcks"/> into as few datagrams as the MTU allows, packing sequences back to
    /// back after the type byte.
    /// </summary>
    /// <param name="synapseConnection">The connection being acknowledged.</param>
    /// <param name="pendingAcks">Queued sequence numbers; fully drained by this call.</param>
    /// <remarks>
    /// One datagram per acknowledged sequence makes batching pure loss: it adds the flush delay without saving any
    /// packets, and under a reliable flood it emits an outbound datagram for every inbound one.
    /// </remarks>
    internal void SendAcks(SynapseConnection synapseConnection, Queue<ushort> pendingAcks)
    {
        if (pendingAcks.Count == 0)
            return;

        int maximumPerDatagram = (int)((_config.MaximumTransmissionUnit - PacketHeader.TypeSize) / PacketHeader.SequenceSize);

        if (maximumPerDatagram < 1)
            maximumPerDatagram = 1;

        while (pendingAcks.Count > 0)
        {
            int sequenceCount = Math.Min(pendingAcks.Count, maximumPerDatagram);
            int totalLength = PacketHeader.TypeSize + (sequenceCount * PacketHeader.SequenceSize);
            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalLength);

            rentedBuffer[0] = (byte)PacketType.Ack;
            int offset = PacketHeader.TypeSize;

            for (int i = 0; i < sequenceCount; i++)
            {
                ushort sequence = pendingAcks.Dequeue();
                rentedBuffer[offset++] = (byte)(sequence & 0xFF);
                rentedBuffer[offset++] = (byte)((sequence >> 8) & 0xFF);
            }

            SendAndPoolBuffer(new(rentedBuffer, 0, totalLength), synapseConnection);
        }
    }

    /// <summary>
    /// Sends a selective acknowledgement for a reliable segmented message: the message sequence followed by a
    /// bitmap of the segment indices already held.
    /// </summary>
    /// <param name="synapseConnection">The connection being acknowledged.</param>
    /// <param name="sequence">The message sequence.</param>
    /// <param name="bitmap">Received-segment bitmap.</param>
    internal void SendSegmentAck(SynapseConnection synapseConnection, ushort sequence, ReadOnlySpan<byte> bitmap)
    {
        const PacketType Type = PacketType.SegmentAck;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        int totalLength = headerSize + bitmap.Length;

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, sequence, 0, 0, 0);
        bitmap.CopyTo(rentedBuffer.AsSpan(headerSize, bitmap.Length));

        SendAndPoolBuffer(new(rentedBuffer, 0, totalLength), synapseConnection);
    }

    /// <summary>
    /// Sends a handshake packet with an 8-byte cryptographic nonce in the payload.
    /// </summary>
    /// <param name="target">The remote endpoint to send the handshake to.</param>
    public void SendHandshake(IPEndPoint target)
    {
        Span<byte> nonce = stackalloc byte[PacketHeader.HandshakeNonceSize];
        RandomNumberGenerator.Fill(nonce);

        SendHandshakePayload(target, nonce);
    }

    /// <summary>
    /// Sends a handshake packet carrying an explicit payload, used for the return-routability challenge and for the
    /// proof answering it. Data packets are unaffected, only the handshake exchange carries these extra bytes.
    /// </summary>
    /// <param name="target">The remote endpoint to send to.</param>
    /// <param name="payload">The handshake payload to carry.</param>
    public void SendHandshakePayload(IPEndPoint target, ReadOnlySpan<byte> payload)
    {
        const PacketType Type = PacketType.Handshake;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        int totalSize = headerSize + payload.Length;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
        payload.CopyTo(rentedBuffer.AsSpan(headerSize, payload.Length));
        SendAndPoolBuffer(new(rentedBuffer, 0, totalSize), target);
    }

    /// <summary>
    /// Sends a keep-alive heartbeat to the connection's remote endpoint.
    /// </summary>
    /// <param name="synapseConnection">The connection to send the heartbeat to.</param>
    public void SendKeepAlive(SynapseConnection synapseConnection)
    {
        const PacketType Type = PacketType.KeepAlive;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
        SendAndPoolBuffer(new(rentedBuffer, 0, headerSize), synapseConnection);
    }

    /// <summary>
    /// Sends a disconnect notification to the connection's remote endpoint.
    /// </summary>
    /// <param name="synapseConnection">The connection being torn down.</param>
    public void SendDisconnect(SynapseConnection synapseConnection)
    {
        const PacketType Type = PacketType.Disconnect;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
        SendAndPoolBuffer(new(rentedBuffer, 0, headerSize), synapseConnection);
    }

    /// <summary>
    /// Parks <paramref name="pendingReliable"/> under <paramref name="sequence"/>, releasing whatever entry was
    /// already held there. A bare indexer assignment would drop the displaced entry silently, orphaning its backing
    /// array and its pooled segment list. That is reachable once
    /// <see cref="SynapseConnection.NextOutgoingSequence"/> wraps the 16-bit sequence space while an older entry is
    /// still unacknowledged.
    /// </summary>
    /// <param name="synapseConnection">Connection owning the pending-reliable table.</param>
    /// <param name="sequence">Sequence the entry is parked under.</param>
    /// <param name="pendingReliable">The entry taking ownership of the sequence.</param>
    private static void ParkPendingReliable(SynapseConnection synapseConnection, ushort sequence, SynapseConnection.PendingReliable pendingReliable)
    {
        if (synapseConnection.PendingReliableQueue.TryGetValue(sequence, out SynapseConnection.PendingReliable? displaced))
            SynapseConnection.ReleasePendingReliable(displaced);

        synapseConnection.PendingReliableQueue[sequence] = pendingReliable;
    }

    /// <summary>
    /// Sends bytes directly over the appropriate socket (IPv6 when available, otherwise IPv4)
    /// and records the sent byte count in telemetry.
    /// </summary>
    /// <param name="segment">The packet data to send, including offset and length.</param>
    /// <param name="target">The remote endpoint to send to.</param>
    private void SendDirect(ArraySegment<byte> segment, IPEndPoint target)
    {
        if (_packetTransform is not null && !TryTransformOutbound(ref segment, target))
            return;

        /* A connected socket sends through the endpoint-free Send call, the SendTo paths below serialize the target per
         * datagram (unavoidably so on Unity's Mono). Reference equality catches the steady state (every per-connection send
         * addresses the stored RemoteEndPoint instance); the value fallback covers a caller-built equal endpoint. */
        if (_connectedRemoteEndPoint is not null && (ReferenceEquals(target, _connectedRemoteEndPoint) || _connectedRemoteEndPoint.Equals(target)))
        {
            int connectedBytesSent = _connectedSocket!.Send(segment.Array!, segment.Offset, segment.Count, SocketFlags.None);
            _telemetry.OnSent(connectedBytesSent);

            return;
        }

        Socket socket = target.AddressFamily == AddressFamily.InterNetworkV6 && _ipv6Socket is not null ? _ipv6Socket : _ipv4Socket;

#if NET8_0_OR_GREATER
        /* The SocketAddress overload sends without serializing the endpoint; the EndPoint overload in the legacy branch
         * re-serializes the same stable per-connection endpoint on every datagram. netstandard2.1 has no SocketAddress
         * overload, which is why it reaches for the native syscall instead. */
        if (!_serializedSendTargets.TryGetValue(target, out SocketAddress? serializedTarget))
        {
            if (_serializedSendTargets.Count >= MaximumSerializedSendTargets)
                _serializedSendTargets.Clear();

            serializedTarget = target.Serialize();
            _serializedSendTargets.Add(target, serializedTarget);
        }

        int bytesSent = socket.SendTo(segment.AsSpan(), SocketFlags.None, serializedTarget);
#else
        if (_useNativeSend && segment.Array is not null)
        {
            if (!_nativeSendTargets.TryGetValue(target, out NativeSendTarget nativeTarget))
            {
                /* Bounded like the serialized-target cache: steady-state targets are the connected peers, so the
                 * cap only engages under a flood of transient handshake targets. */
                if (_nativeSendTargets.Count >= MaximumNativeSendTargets)
                    _nativeSendTargets.Clear();

                byte[] sockAddr = new byte[NativeSocket.SockAddrSize];

                if (NativeSocket.TryBuildSockAddr(target, sockAddr, out int builtLength))
                {
                    nativeTarget = new(sockAddr, builtLength);
                    _nativeSendTargets[target] = nativeTarget;
                }
            }

            // A default instance means the address could not be built; fall through to the managed path.
            if (nativeTarget.SockAddr is not null)
            {
                int nativeSent = NativeSocket.SendTo(socket, segment.Array, segment.Offset, segment.Count, nativeTarget.SockAddr, nativeTarget.Length);

                if (nativeSent >= 0)
                {
                    _telemetry.OnSent(nativeSent);
                    return;
                }
            }
        }

        int bytesSent = socket.SendTo(segment.Array!, segment.Offset, segment.Count, SocketFlags.None, target);
#endif
        _telemetry.OnSent(bytesSent);
    }

    /// <summary>
    /// Rewrites the payload of an outbound packet through <see cref="_packetTransform"/> and repoints
    /// <paramref name="segment"/> at the result. The header is copied through byte for byte, so the receiving side can
    /// still identify the packet before it reverses the transform.
    /// Datagrams whose leading byte is above <see cref="PacketType.NatChallenge"/> belong to an external protocol
    /// piggybacking on the socket via <see cref="SendRaw"/> and pass through untouched.
    /// </summary>
    /// <param name="segment">The wire-ready bytes to send, replaced by the transformed packet when one was produced.</param>
    /// <param name="target">The remote endpoint the packet is addressed to.</param>
    /// <returns>True to send <paramref name="segment"/>, or false when the transform discarded the packet.</returns>
    private bool TryTransformOutbound(ref ArraySegment<byte> segment, IPEndPoint target)
    {
        byte typeByte = segment.Array![segment.Offset];

        if (typeByte > (byte)PacketType.NatChallenge)
            return true;

        PacketType packetType = (PacketType)typeByte;
        int headerSize = PacketHeader.ComputeHeaderSize(packetType);

        // A caller-supplied packet too short for the header it declares is left alone rather than throwing here;
        // it is not something the engine's own framing can produce.
        if (segment.Count < headerSize)
            return true;

        byte[] transformBuffer = _transformBuffer!;
        Buffer.BlockCopy(segment.Array, segment.Offset, transformBuffer, 0, headerSize);

        bool isTransformed = _packetTransform!.TryTransform(PacketTransformDirection.Outbound, packetType, target, segment.AsSpan(headerSize), transformBuffer.AsSpan(headerSize), out int writtenLength);

        // The unsigned compare also catches a negative length, so a transform that misreports what it wrote cannot
        // push the socket into reading past the end of the buffer.
        if (!isTransformed || (uint)writtenLength > (uint)(transformBuffer.Length - headerSize))
            return false;

        segment = new(transformBuffer, 0, headerSize + writtenLength);

        return true;
    }

    /// <summary>
    /// Sends a packet and returns its backing buffer to the shared <see cref="ArrayPool{T}"/> afterwards,
    /// guaranteeing the rental is returned even if the send throws.
    /// </summary>
    /// <param name="segment">The wire-ready bytes to send; <see cref="ArraySegment{T}.Array"/> is returned to the pool after sending.</param>
    /// <param name="target">The remote endpoint to send to.</param>
    private void SendAndPoolBuffer(ArraySegment<byte> segment, IPEndPoint target)
    {
        try
        {
            SendRaw(segment, target);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(segment.Array!, clearArray: false);
        }
    }

    /// <summary>
    /// Sends to a connection's remote endpoint, stamping <see cref="SynapseConnection.LastSentTicks"/> first. Every send
    /// routed at a connection must go through this or <see cref="SendAndPoolBuffer(ArraySegment{byte}, SynapseConnection)"/>:
    /// the keep-alive sweep reads that stamp to decide whether this side still owes the peer a heartbeat, so a send that
    /// skips it would let the sweep emit a redundant one.
    /// </summary>
    /// <param name="segment">The wire-ready bytes to send.</param>
    /// <param name="synapseConnection">The connection being sent to.</param>
    private void SendToConnection(ArraySegment<byte> segment, SynapseConnection synapseConnection)
    {
        synapseConnection.LastSentTicks = Clock.Ticks;
        SendRaw(segment, synapseConnection.RemoteEndPoint);
    }

    /// <summary>
    /// Connection-addressed <see cref="SendAndPoolBuffer(ArraySegment{byte}, IPEndPoint)"/>: stamps
    /// <see cref="SynapseConnection.LastSentTicks"/>, then sends and returns the rental.
    /// </summary>
    /// <param name="segment">The wire-ready bytes to send; <see cref="ArraySegment{T}.Array"/> is returned to the pool after sending.</param>
    /// <param name="synapseConnection">The connection being sent to.</param>
    private void SendAndPoolBuffer(ArraySegment<byte> segment, SynapseConnection synapseConnection)
    {
        synapseConnection.LastSentTicks = Clock.Ticks;
        SendAndPoolBuffer(segment, synapseConnection.RemoteEndPoint);
    }

#if !NET8_0_OR_GREATER
    /// <summary>
    /// A peer's raw <c>sockaddr</c> and the number of bytes of it that are valid, built once per target and
    /// reused for every send. Stored by value in <see cref="_nativeSendTargets"/>, so a lookup allocates nothing.
    /// </summary>
    private readonly struct NativeSendTarget
    {
        /// <summary>
        /// Raw address bytes. Null on a default instance, meaning the address could not be built.
        /// </summary>
        public readonly byte[]? SockAddr;
        /// <summary>
        /// Valid bytes within <see cref="SockAddr"/>.
        /// </summary>
        public readonly int Length;

        /// <summary>
        /// Creates a target from a prebuilt address.
        /// </summary>
        /// <param name="sockAddr">Raw address bytes.</param>
        /// <param name="length">Valid bytes within <paramref name="sockAddr"/>.</param>
        public NativeSendTarget(byte[] sockAddr, int length)
        {
            SockAddr = sockAddr;
            Length = length;
        }
    }
#endif
}
