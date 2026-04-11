using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CodeBoost.Performance;
using SynapseSocket.Connections;
using SynapseSocket.Diagnostics;
using SynapseSocket.Packets;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Transport;

/// <summary>
/// Transmission Engine (Sender).
/// Manages outgoing packet flow for both the unreliable and reliable channels.
/// Immediate processing (no batching) per the spec's no-batching policy.
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
    }

    /// <summary>
    /// Sends raw bytes to the target endpoint, routing through the latency simulator.
    /// </summary>
    /// <param name="segment">The wire-ready bytes to send.</param>
    /// <param name="target">The remote endpoint to send to.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public Task SendRawAsync(ArraySegment<byte> segment, IPEndPoint target, CancellationToken cancellationToken)
    {
        return _latencySimulator.ProcessAsync(segment.Array!, segment.Count, target, SendDirectAsync, cancellationToken);
    }

    /// <summary>
    /// Sends an unreliable, unsegmented payload to the connection's remote endpoint.
    /// Builds a header-only packet, copies the payload after it, and sends immediately.
    /// </summary>
    /// <param name="synapseConnection">The target connection.</param>
    /// <param name="payload">The application payload to send.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    internal async Task SendUnreliableUnsegmentedAsync(SynapseConnection synapseConnection, ArraySegment<byte> payload, CancellationToken cancellationToken)
    {
        const PacketType Type = PacketType.None;
        int totalLength = PacketHeader.ComputeHeaderSize(Type) + payload.Count;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            int written = PacketHeader.BuildPacket(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0, payload.AsSpan());
            await SendRawAsync(new(rentedBuffer, 0, written), synapseConnection.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
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
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    internal async Task SendReliableUnsegmentedAsync(SynapseConnection synapseConnection, ArraySegment<byte> payload, CancellationToken cancellationToken)
    {
        if (synapseConnection.PendingReliableQueue.Count >= _config.Reliable.MaximumPending)
            throw new InvalidOperationException("Reliable backpressure limit reached.");

        ushort sequence;

        lock (synapseConnection.ReliableLock)
            sequence = synapseConnection.NextOutgoingSequence++;

        const PacketType Type = PacketType.Reliable;
        int totalLength = PacketHeader.ComputeHeaderSize(Type) + payload.Count;

        byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
        int written = PacketHeader.BuildPacket(packetBuffer.AsSpan(), Type, sequence, 0, 0, 0, payload.AsSpan());

        SynapseConnection.PendingReliable pendingReliable = ResettableObjectPool<SynapseConnection.PendingReliable>.Rent();
        pendingReliable.Sequence = sequence;
        pendingReliable.Payload = packetBuffer;
        pendingReliable.PacketLength = written;
        pendingReliable.SentTicks = DateTime.UtcNow.Ticks;
        pendingReliable.Retries = 0;
        synapseConnection.PendingReliableQueue[sequence] = pendingReliable;

        await SendRawAsync(new(packetBuffer, 0, written), synapseConnection.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits a payload into wire-ready segments and sends them all.
    /// For reliable sends, the segment array is stored in <see cref="SynapseConnection.PendingReliable"/>
    /// and its lifetime is managed by the retransmission sweep and ACK handler.
    /// For unreliable sends, the backing buffer is returned to the pool after the last send completes.
    /// </summary>
    /// <param name="synapseConnection">The target connection.</param>
    /// <param name="payload">The application payload to split and send.</param>
    /// <param name="isReliable">True to send segments reliably with retransmission; false for unreliable delivery.</param>
    /// <param name="splitter">The <see cref="PacketSplitter"/> instance used to produce the segment array.</param>
    /// <param name="cancellationToken">Token to cancel the send operations.</param>
    internal async Task SendSegmentedAsync(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable, PacketSplitter splitter, CancellationToken cancellationToken)
    {
        if (isReliable && synapseConnection.PendingReliableQueue.Count >= _config.Reliable.MaximumPending)
            throw new InvalidOperationException("Reliable backpressure limit reached.");

        ushort sequence = 0;

        if (isReliable)
        {
            lock (synapseConnection.ReliableLock)
                sequence = synapseConnection.NextOutgoingSequence++;
        }

        ArraySegment<byte>[] segments = splitter.Split(payload.AsSpan(), isReliable, out int segmentCount, sequence);

        if (isReliable)
        {
            SynapseConnection.PendingReliable pendingReliable = ResettableObjectPool<SynapseConnection.PendingReliable>.Rent();
            pendingReliable.Sequence = sequence;
            pendingReliable.Segments = segments;
            pendingReliable.SegmentCount = segmentCount;
            pendingReliable.SentTicks = DateTime.UtcNow.Ticks;
            pendingReliable.Retries = 0;
            synapseConnection.PendingReliableQueue[sequence] = pendingReliable;

            // Segments are now owned by PendingReliable; do NOT return them here.
            for (int i = 0; i < segmentCount; i++)
                await SendRawAsync(segments[i], synapseConnection.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                for (int i = 0; i < segmentCount; i++)
                    await SendRawAsync(segments[i], synapseConnection.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // All segments share one backing buffer — only segments[0].Array needs returning.
                if (segmentCount > 0 && segments[0].Array is not null)
                    ArrayPool<byte>.Shared.Return(segments[0].Array!);

                ArrayPool<ArraySegment<byte>>.Shared.Return(segments);
            }
        }
    }

    /// <summary>
    /// Sends a reliable-channel acknowledgement for the given sequence number.
    /// </summary>
    /// <param name="synapseConnection">The connection to acknowledge.</param>
    /// <param name="sequence">The sequence number being acknowledged.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    /// <returns>A task that completes when the ACK packet has been handed to the socket.</returns>
    public Task SendAckAsync(SynapseConnection synapseConnection, ushort sequence, CancellationToken cancellationToken)
    {
        const PacketType Type = PacketType.Ack;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, sequence, 0, 0, 0);
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint, cancellationToken);
    }

    /// <summary>
    /// Sends a handshake packet with a 4-byte cryptographic nonce in the payload.
    /// </summary>
    /// <param name="target">The remote endpoint to send the handshake to.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    /// <returns>A task that completes when the handshake packet has been handed to the socket.</returns>
    public Task SendHandshakeAsync(IPEndPoint target, CancellationToken cancellationToken)
    {
        const PacketType Type = PacketType.Handshake;
        const int NonceSize = 4;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        int totalSize = headerSize + NonceSize;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
        RandomNumberGenerator.Fill(rentedBuffer.AsSpan(headerSize, NonceSize));
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, totalSize), target, cancellationToken);
    }

    /// <summary>
    /// Sends a keep-alive heartbeat to the connection's remote endpoint.
    /// </summary>
    /// <param name="synapseConnection">The connection to send the heartbeat to.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    /// <returns>A task that completes when the keep-alive packet has been handed to the socket.</returns>
    public Task SendKeepAliveAsync(SynapseConnection synapseConnection, CancellationToken cancellationToken)
    {
        const PacketType Type = PacketType.KeepAlive;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint, cancellationToken);
    }

    /// <summary>
    /// Sends a disconnect notification to the connection's remote endpoint.
    /// </summary>
    /// <param name="synapseConnection">The connection being torn down.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    /// <returns>A task that completes when the disconnect packet has been handed to the socket.</returns>
    public Task SendDisconnectAsync(SynapseConnection synapseConnection, CancellationToken cancellationToken)
    {
        const PacketType Type = PacketType.Disconnect;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint, cancellationToken);
    }

    /// <summary>
    /// Sends bytes directly over the appropriate socket (IPv6 when available, otherwise IPv4)
    /// and records the sent byte count in telemetry.
    /// </summary>
    /// <param name="buffer">The buffer containing the bytes to send.</param>
    /// <param name="length">Number of bytes to send from the start of <paramref name="buffer"/>.</param>
    /// <param name="target">The remote endpoint to send to.</param>
    private async Task SendDirectAsync(byte[] buffer, int length, IPEndPoint target)
    {
        Socket socket = target.AddressFamily == AddressFamily.InterNetworkV6 && _ipv6Socket is not null ? _ipv6Socket : _ipv4Socket;
        int bytesSent = await socket.SendToAsync(new(buffer, 0, length), SocketFlags.None, target).ConfigureAwait(false);
        _telemetry.OnSent(bytesSent);
    }

    /// <summary>
    /// Sends a packet and returns its backing buffer to the shared <see cref="ArrayPool{T}"/>
    /// once the send completes, guaranteeing the rental is returned even if an exception occurs.
    /// </summary>
    /// <param name="segment">The wire-ready bytes to send; <see cref="ArraySegment{T}.Array"/> is returned to the pool after sending.</param>
    /// <param name="target">The remote endpoint to send to.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    private async Task SendAndPoolBufferAsync(ArraySegment<byte> segment, IPEndPoint target, CancellationToken cancellationToken)
    {
        try
        {
            await SendRawAsync(segment, target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(segment.Array!, clearArray: false);
        }
    }
}