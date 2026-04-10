using System;
using System.Buffers;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
    public Task SendNatRegisterAsync(IPEndPoint target, string sessionId, CancellationToken cancellationToken)
    {
        return SendNatServerPacketAsync(target, NatPacketType.Register, sessionId, cancellationToken);
    }

    public Task SendNatHeartbeatAsync(IPEndPoint target, string sessionId, CancellationToken cancellationToken)
    {
        return SendNatServerPacketAsync(target, NatPacketType.Heartbeat, sessionId, cancellationToken);
    }

    private Task SendNatServerPacketAsync(IPEndPoint target, NatPacketType packetType, string sessionId, CancellationToken cancellationToken)
    {
        const PacketFlags Flags = PacketFlags.Extended;
        const int SessionIdBytes = ServerNatConfig.SessionIdLength;
        const int PayloadSize = 1 + SessionIdBytes;
        int headerSize = PacketHeader.ComputeHeaderSize(Flags);
        int totalSize = headerSize + PayloadSize;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        int offset = PacketHeader.Write(rentedBuffer.AsSpan(), Flags, 0, 0, 0, 0);
        rentedBuffer[offset++] = (byte)packetType;
        System.Text.Encoding.ASCII.GetBytes(sessionId, 0, SessionIdBytes, rentedBuffer, offset);
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, totalSize), target, cancellationToken);
    }

    public Task SendNatProbeAsync(IPEndPoint target, CancellationToken cancellationToken)
    {
        const PacketFlags Flags = PacketFlags.Extended;
        int headerSize = PacketHeader.ComputeHeaderSize(Flags);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Flags, 0, 0, 0, 0);
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, headerSize), target, cancellationToken);
    }
}