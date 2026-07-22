using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CodeBoost.Performance;
using SynapseSocket.Connections;
using SynapseSocket.Diagnostics;
using SynapseSocket.Packets;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Transport;

/// <summary>
/// Transmission Engine (Sender).
/// Manages outgoing packet flow for both the unreliable and reliable channels.
/// Sends are synchronous and immediate (blocking <see cref="Socket.SendTo(byte[], int, int, SocketFlags, EndPoint)"/>)
/// — the engine is single-threaded and driven by the host's poll, so there are no async continuations.
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
    /// The single remote the engine's socket is OS-connected to when <see cref="SynapseConfig.ConnectedSocketEnabled"/> engaged,
    /// or null for the ordinary any-target mode. Sends to it go through the endpoint-free Send call — no per-datagram target
    /// serialization on any runtime.
    /// </summary>
    private IPEndPoint? _connectedRemoteEndPoint;
    /// <summary>
    /// The OS-connected socket sends to <see cref="_connectedRemoteEndPoint"/> ride, resolved once when the connection is made.
    /// </summary>
    private Socket? _connectedSocket;
#if NET8_0_OR_GREATER
    /// <summary>
    /// Serialized form of each send target, built once per endpoint. The EndPoint-based SendTo serializes the target into a
    /// fresh SocketAddress on every call — two allocations per sent datagram for endpoints that never change — while the
    /// SocketAddress overload sends with none.
    /// </summary>
    private readonly Dictionary<IPEndPoint, SocketAddress> _serializedSendTargets = [];
    /// <summary>
    /// Ceiling for <see cref="_serializedSendTargets"/>. Steady-state targets are the connected peers, so the cap only ever
    /// engages under a flood of transient handshake targets; clearing simply re-serializes on the next send.
    /// </summary>
    private const int MaximumSerializedSendTargets = 4096;
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

        _latencySimulator.Process(segment, target, DateTime.UtcNow.Ticks, _sendDirect);
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
            SendRaw(new(rentedBuffer, 0, written), synapseConnection.RemoteEndPoint);
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
        pendingReliable.Initialize(segments, packetBuffer, DateTime.UtcNow.Ticks);

        synapseConnection.PendingReliableQueue[sequence] = pendingReliable;

        SendRaw(segments[0], synapseConnection.RemoteEndPoint);
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

        if (isReliable)
        {
            SynapseConnection.PendingReliable pendingReliable = ResettableObjectPool<SynapseConnection.PendingReliable>.Rent();
            pendingReliable.Initialize(segments, backingBuffer, DateTime.UtcNow.Ticks);

            synapseConnection.PendingReliableQueue[sequence] = pendingReliable;

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
        SendAndPoolBuffer(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint);
    }

    /// <summary>
    /// Sends a handshake packet with an 8-byte cryptographic nonce in the payload.
    /// </summary>
    /// <param name="target">The remote endpoint to send the handshake to.</param>
    public void SendHandshake(IPEndPoint target)
    {
        const PacketType Type = PacketType.Handshake;
        const int NonceSize = 8;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        int totalSize = headerSize + NonceSize;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
        RandomNumberGenerator.Fill(rentedBuffer.AsSpan(headerSize, NonceSize));
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
        SendAndPoolBuffer(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint);
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
        SendAndPoolBuffer(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint);
    }

    /// <summary>
    /// Sends bytes directly over the appropriate socket (IPv6 when available, otherwise IPv4)
    /// and records the sent byte count in telemetry.
    /// </summary>
    /// <param name="segment">The packet data to send, including offset and length.</param>
    /// <param name="target">The remote endpoint to send to.</param>
    private void SendDirect(ArraySegment<byte> segment, IPEndPoint target)
    {
        /* A connected socket sends through the endpoint-free Send call — the SendTo paths below serialize the target per
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
        /* The SocketAddress overload sends without serializing the endpoint; the EndPoint overload below re-serializes the
         * same stable per-connection endpoint on every datagram. netstandard2.1 has no SocketAddress overload, so only the
         * modern build takes this path. */
        if (!_serializedSendTargets.TryGetValue(target, out SocketAddress? serializedTarget))
        {
            if (_serializedSendTargets.Count >= MaximumSerializedSendTargets)
                _serializedSendTargets.Clear();

            serializedTarget = target.Serialize();
            _serializedSendTargets.Add(target, serializedTarget);
        }

        int bytesSent = socket.SendTo(segment.AsSpan(), SocketFlags.None, serializedTarget);
#else
        int bytesSent = socket.SendTo(segment.Array!, segment.Offset, segment.Count, SocketFlags.None, target);
#endif
        _telemetry.OnSent(bytesSent);
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
}
