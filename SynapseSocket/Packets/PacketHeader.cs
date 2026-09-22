using System;
using System.Runtime.CompilerServices;

namespace SynapseSocket.Packets;

/// <summary>
/// Wire-format header helpers for Synapse packets.
/// Layout:
///   [0]      : PacketType (1 byte)
///   [1..2]   : Sequence number (UInt16, little-endian), only for <see cref="PacketType.Reliable"/>, <see cref="PacketType.Ack"/>, <see cref="PacketType.ReliableSegmented"/>
///   [3..4]   : Segment Id (UInt16), only for <see cref="PacketType.Segmented"/> and <see cref="PacketType.ReliableSegmented"/>
///   [5]      : Segment Index (Byte), only for <see cref="PacketType.Segmented"/> and <see cref="PacketType.ReliableSegmented"/>
///   [6]      : Segment Count (Byte), only for <see cref="PacketType.Segmented"/> and <see cref="PacketType.ReliableSegmented"/>
///   [...]    : Payload
/// Explicit little-endian ordering is used for cross-platform consistency.
/// </summary>
public static class PacketHeader
{
    /// <summary>
    /// Size in bytes of the mandatory type field.
    /// </summary>
    public const int TypeSize = 1;

    /// <summary>
    /// Size in bytes of the reliable sequence field when present.
    /// </summary>
    public const int SequenceSize = 2;

    /// <summary>
    /// Size in bytes of the segmentation fields when present.
    /// </summary>
    public const int SegmentSize = 4;

    /// <summary>
    /// Maximum theoretical header size (all optional fields present).
    /// </summary>
    public const int MaxHeaderSize = TypeSize + SequenceSize + SegmentSize;

    /// <summary>
    /// Bytes of segment bitmap carried by a <see cref="PacketType.SegmentAck"/>: one bit per segment index, and a
    /// message may hold at most 255 segments.
    /// </summary>
    public const int SegmentAckBitmapSize = 32;

    /// <summary>
    /// Payload bytes carried by an ordinary <see cref="PacketType.Handshake"/>: a random nonce.
    /// </summary>
    public const int HandshakeNonceSize = 8;

    /// <summary>
    /// Payload bytes carried by a handshake return-routability challenge and by the proof answering it:
    /// the peer's nonce echoed back, followed by an 8-byte token bound to the peer's address and a time bucket.
    /// <para>
    /// The payload length is what distinguishes the two forms on the wire, so no new packet type is needed and
    /// nothing is added to data packets. Direction disambiguates challenge from proof: a side holding a
    /// <see cref="SynapseSocket.Connections.ConnectionState.Pending"/> connection is being challenged, anyone else
    /// is presenting a proof.
    /// </para>
    /// </summary>
    public const int HandshakeChallengeSize = HandshakeNonceSize + 8;

    /// <summary>
    /// Computes the header size for a given packet type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeHeaderSize(PacketType type) => type switch
    {
        PacketType.Reliable          => TypeSize + SequenceSize,
        PacketType.Ack               => TypeSize + SequenceSize,
        PacketType.SegmentAck        => TypeSize + SequenceSize,
        PacketType.Segmented         => TypeSize + SegmentSize,
        PacketType.ReliableSegmented => TypeSize + SequenceSize + SegmentSize,
        _                            => TypeSize
    };

    /// <summary>
    /// Writes a header into the supplied buffer starting at offset 0.
    /// Returns the number of bytes written.
    /// </summary>
    public static int Write(Span<byte> buffer, PacketType type, ushort sequence, ushort segmentId, byte segmentIndex, byte segmentCount)
    {
        int offset = 0;
        buffer[offset++] = (byte)type;

        if (type is PacketType.Reliable or PacketType.Ack or PacketType.ReliableSegmented or PacketType.SegmentAck)
        {
            buffer[offset++] = (byte)(sequence & 0xFF);
            buffer[offset++] = (byte)((sequence >> 8) & 0xFF);
        }

        if (type == PacketType.Segmented || type == PacketType.ReliableSegmented)
        {
            buffer[offset++] = (byte)(segmentId & 0xFF);
            buffer[offset++] = (byte)((segmentId >> 8) & 0xFF);
            buffer[offset++] = segmentIndex;
            buffer[offset++] = segmentCount;
        }

        return offset;
    }

    /// <summary>
    /// Writes a header followed by <paramref name="payload"/> into <paramref name="destination"/>.
    /// Returns the total number of bytes written (header + payload).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildPacket(Span<byte> destination, PacketType type, ushort sequence, ushort segmentId, byte segmentIndex, byte segmentCount, ReadOnlySpan<byte> payload)
    {
        int headerSize = Write(destination, type, sequence, segmentId, segmentIndex, segmentCount);
        payload.CopyTo(destination[headerSize..]);
        return headerSize + payload.Length;
    }

    /// <summary>
    /// Reads a header from the supplied buffer without throwing.
    /// Returns false when the buffer is too small for the fields the declared type requires.
    /// </summary>
    /// <param name="buffer">The datagram bytes.</param>
    /// <param name="headerSize">On success, the number of bytes consumed by the header.</param>
    /// <param name="type">The declared packet type.</param>
    /// <param name="sequence">The sequence number, or 0 when the type carries none.</param>
    /// <param name="segmentId">The segment id, or 0 when the type carries none.</param>
    /// <param name="segmentIndex">The segment index, or 0 when the type carries none.</param>
    /// <param name="segmentCount">The segment count, or 0 when the type carries none.</param>
    /// <returns>True when the header parsed cleanly.</returns>
    /// <remarks>
    /// Deliberately returns a bool rather than throwing. This sits on the unauthenticated receive path, where a
    /// truncated header is something any peer can send for the price of a two-byte datagram; signalling that by
    /// throwing costs an allocation, a message string and two stack walks per packet, which is orders of magnitude
    /// more than the parse itself and is trivially floodable.
    /// </remarks>
    public static bool TryRead(ReadOnlySpan<byte> buffer, out int headerSize, out PacketType type, out ushort sequence, out ushort segmentId, out byte segmentIndex, out byte segmentCount)
    {
        headerSize = 0;
        type = PacketType.None;
        sequence = 0;
        segmentId = 0;
        segmentIndex = 0;
        segmentCount = 0;

        if (buffer.Length < TypeSize)
            return false;

        int offset = 0;
        type = (PacketType)buffer[offset++];

        if (type is PacketType.Reliable or PacketType.Ack or PacketType.ReliableSegmented or PacketType.SegmentAck)
        {
            if (buffer.Length < offset + SequenceSize)
                return false;

            sequence = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            offset += 2;
        }

        if (type == PacketType.Segmented || type == PacketType.ReliableSegmented)
        {
            if (buffer.Length < offset + SegmentSize)
                return false;

            segmentId = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            segmentIndex = buffer[offset + 2];
            segmentCount = buffer[offset + 3];
            offset += 4;
        }

        headerSize = offset;
        return true;
    }
}
