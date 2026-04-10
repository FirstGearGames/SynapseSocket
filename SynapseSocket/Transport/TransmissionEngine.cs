using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly Socket _ipv4Socket;
    private readonly Socket? _ipv6Socket;
    private readonly SynapseConfig _config;
    private readonly Telemetry _telemetry;
    private readonly LatencySimulator _latency;

    public TransmissionEngine(Socket ipv4Socket, Socket? ipv6Socket, SynapseConfig config, Telemetry telemetry, LatencySimulator latency)
    {
        _ipv4Socket = ipv4Socket ?? throw new ArgumentNullException(nameof(ipv4Socket));
        _ipv6Socket = ipv6Socket;
        _config = config;
        _telemetry = telemetry;
        _latency = latency;
    }

    public Task SendRawAsync(ArraySegment<byte> segment, IPEndPoint target, CancellationToken cancellationToken)
    {
        return _latency.ProcessAsync(segment.Array!, segment.Count, target, SendDirectAsync, cancellationToken);
    }

    internal async Task SendUnreliableUnsegmentedAsync(SynapseConnection synapseConnection, ArraySegment<byte> payload, CancellationToken cancellationToken)
    {
        const PacketFlags Flags = PacketFlags.None;
        int totalLength = PacketHeader.ComputeHeaderSize(Flags) + payload.Count;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            int written = PacketHeader.BuildPacket(rentedBuffer.AsSpan(), Flags, 0, 0, 0, 0, payload.AsSpan());
            await SendRawAsync(new(rentedBuffer, 0, written), synapseConnection.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: false);
        }
    }

    internal async Task SendReliableUnsegmentedAsync(SynapseConnection synapseConnection, ArraySegment<byte> payload, CancellationToken cancellationToken)
    {
        if (synapseConnection.PendingReliableQueue.Count >= _config.Reliable.MaximumPending)
            throw new InvalidOperationException("Reliable backpressure limit reached.");

        ushort sequence;
        lock (synapseConnection.ReliableGate)
            sequence = synapseConnection.NextOutgoingSequence++;

        const PacketFlags Flags = PacketFlags.Reliable;
        int totalLength = PacketHeader.ComputeHeaderSize(Flags) + payload.Count;

        byte[] packetBuffer = new byte[totalLength];
        int written = PacketHeader.BuildPacket(packetBuffer.AsSpan(), Flags, sequence, 0, 0, 0, payload.AsSpan());

        SynapseConnection.PendingReliable pendingReliable = new()
        {
            Sequence = sequence,
            Payload = packetBuffer,
            Length = written,
            SentTicks = DateTime.UtcNow.Ticks,
            Retries = 0
        };
        synapseConnection.PendingReliableQueue[sequence] = pendingReliable;

        await SendRawAsync(new(packetBuffer, 0, written), synapseConnection.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits a payload into wire-ready segments and sends them all.
    /// For reliable sends, the segment array is stored in <see cref="SynapseConnection.PendingReliable"/>
    /// and its lifetime is managed by the retransmission sweep and ACK handler.
    /// For unreliable sends, the backing buffer is returned to the pool after the last send completes.
    /// </summary>
    internal async Task SendSegmentedAsync(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable, PacketSplitter splitter, CancellationToken cancellationToken)
    {
        if (isReliable && synapseConnection.PendingReliableQueue.Count >= _config.Reliable.MaximumPending)
            throw new InvalidOperationException("Reliable backpressure limit reached.");

        ushort sequence = 0;
        if (isReliable)
        {
            lock (synapseConnection.ReliableGate)
                sequence = synapseConnection.NextOutgoingSequence++;
        }

        ArraySegment<byte>[] segments = splitter.Split(payload.AsSpan(), isReliable, out int segmentCount, sequence);

        if (isReliable)
        {
            SynapseConnection.PendingReliable pendingReliable = new()
            {
                Sequence = sequence,
                Segments = segments,
                SegmentCount = segmentCount,
                SentTicks = DateTime.UtcNow.Ticks,
                Retries = 0
            };
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

    public Task SendAckAsync(SynapseConnection synapseConnection, ushort sequence, CancellationToken cancellationToken)
    {
        const PacketFlags Flags = PacketFlags.Ack;
        int headerSize = PacketHeader.ComputeHeaderSize(Flags);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Flags, sequence, 0, 0, 0);
        return SendAndReturnAsync(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint, cancellationToken);
    }

    /// <summary>
    /// Sends a handshake packet with a 4-byte cryptographic nonce in the payload.
    /// </summary>
    public Task SendHandshakeAsync(IPEndPoint target, CancellationToken cancellationToken)
    {
        const PacketFlags Flags = PacketFlags.Handshake;
        const int NonceSize = 4;
        int headerSize = PacketHeader.ComputeHeaderSize(Flags);
        int totalSize = headerSize + NonceSize;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Flags, 0, 0, 0, 0);
        RandomNumberGenerator.Fill(rentedBuffer.AsSpan(headerSize, NonceSize));
        return SendAndReturnAsync(new(rentedBuffer, 0, totalSize), target, cancellationToken);
    }

    public Task SendKeepAliveAsync(SynapseConnection synapseConnection, CancellationToken cancellationToken)
    {
        const PacketFlags Flags = PacketFlags.KeepAlive;
        int headerSize = PacketHeader.ComputeHeaderSize(Flags);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Flags, 0, 0, 0, 0);
        return SendAndReturnAsync(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint, cancellationToken);
    }

    public Task SendDisconnectAsync(SynapseConnection synapseConnection, CancellationToken cancellationToken)
    {
        const PacketFlags Flags = PacketFlags.Disconnect;
        int headerSize = PacketHeader.ComputeHeaderSize(Flags);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Flags, 0, 0, 0, 0);
        return SendAndReturnAsync(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint, cancellationToken);
    }

    private async Task SendDirectAsync(byte[] buffer, int length, IPEndPoint target)
    {
        Socket socket = target.AddressFamily == AddressFamily.InterNetworkV6 && _ipv6Socket != null ? _ipv6Socket : _ipv4Socket;
        int bytesSent = await socket.SendToAsync(new(buffer, 0, length), SocketFlags.None, target).ConfigureAwait(false);
        _telemetry.OnSent(bytesSent);
    }

    private async Task SendAndReturnAsync(ArraySegment<byte> segment, IPEndPoint target, CancellationToken cancellationToken)
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