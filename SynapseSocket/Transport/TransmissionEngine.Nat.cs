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
    /// <summary>
    /// Sends a NAT registration packet to the rendezvous server for the given session.
    /// </summary>
    /// <param name="target">The rendezvous server endpoint to register with.</param>
    /// <param name="sessionId">The session identifier to register.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    /// <returns>A task that completes when the registration packet has been handed to the socket.</returns>
    public Task SendNatRegisterAsync(IPEndPoint target, string sessionId, CancellationToken cancellationToken)
    {
        return SendNatServerPacketAsync(target, NatPacketType.Register, sessionId, cancellationToken);
    }

    /// <summary>
    /// Sends a NAT heartbeat packet to the rendezvous server to keep the session alive.
    /// </summary>
    /// <param name="target">The rendezvous server endpoint.</param>
    /// <param name="sessionId">The session identifier associated with this client.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    /// <returns>A task that completes when the heartbeat packet has been handed to the socket.</returns>
    public Task SendNatHeartbeatAsync(IPEndPoint target, string sessionId, CancellationToken cancellationToken)
    {
        return SendNatServerPacketAsync(target, NatPacketType.Heartbeat, sessionId, cancellationToken);
    }

    /// <summary>
    /// Builds and sends a NAT server packet (register or heartbeat) with the session identifier
    /// encoded as a fixed-length ASCII payload.
    /// </summary>
    /// <param name="target">The rendezvous server endpoint.</param>
    /// <param name="packetType">The NAT packet type byte written at the start of the payload.</param>
    /// <param name="sessionId">The session identifier to encode in the packet.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    /// <returns>A task that completes when the packet has been handed to the socket.</returns>
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

    /// <summary>
    /// Sends a minimal NAT probe (extended header, no body) to open a NAT mapping on the remote side.
    /// </summary>
    /// <param name="target">The remote endpoint to probe.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    /// <returns>A task that completes when the probe packet has been handed to the socket.</returns>
    public Task SendNatProbeAsync(IPEndPoint target, CancellationToken cancellationToken)
    {
        const PacketFlags Flags = PacketFlags.Extended;
        int headerSize = PacketHeader.ComputeHeaderSize(Flags);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Flags, 0, 0, 0, 0);
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, headerSize), target, cancellationToken);
    }
}