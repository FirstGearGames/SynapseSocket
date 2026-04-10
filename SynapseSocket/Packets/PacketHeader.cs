using System;
using System.Runtime.CompilerServices;

namespace SynapseSocket.Packets;

/// <summary>
/// Wire-format header helpers for Synapse packets.
/// Layout:
///   [0]      : PacketFlags (1 byte)
///   [1..2]   : Sequence number (UInt16, little-endian) - only when Reliable or Ack
///   [3..4]   : Segment Id (UInt16) - only when Segmented
///   [5]      : Segment Index (Byte) - only when Segmented
///   [6]      : Segment Count (Byte) - only when Segmented
///   [...]    : Payload
/// All fields after the flag byte are optional and appear in the order above,
/// only if their corresponding flag is present. Explicit little-endian ordering
/// is used for cross-platform consistency.
/// </summary>
public static class PacketHeader
{
    /// <summary>
    /// Size in bytes of the mandatory flag field.
    /// </summary>
    public const int FlagSize = 1;

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
    public const int MaxHeaderSize = FlagSize + SequenceSize + SegmentSize;

    /// <summary>
    /// Computes the header size for a given flag combination.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeHeaderSize(PacketFlags flags)
    {
        int headerSize = FlagSize;
        if ((flags & (PacketFlags.Reliable | PacketFlags.Ack)) != 0) headerSize += SequenceSize;
        if ((flags & PacketFlags.Segmented) != 0) headerSize += SegmentSize;
        return headerSize;
    }

    /// <summary>
    /// Writes a header into the supplied buffer starting at offset 0.
    /// Returns the number of bytes written.
    /// </summary>
    public static int Write(Span<byte> buffer, PacketFlags flags, ushort sequence, ushort segmentId, byte segmentIndex, byte segmentCount)
    {
        int offset = 0;
        buffer[offset++] = (byte)flags;

        if ((flags & (PacketFlags.Reliable | PacketFlags.Ack)) != 0)
        {
            buffer[offset++] = (byte)(sequence & 0xFF);
            buffer[offset++] = (byte)((sequence >> 8) & 0xFF);
        }

        if ((flags & PacketFlags.Segmented) != 0)
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
    /// Use this as the single source of truth for building a complete wire packet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildPacket(Span<byte> destination, PacketFlags flags, ushort sequence, ushort segmentId, byte segmentIndex, byte segmentCount, ReadOnlySpan<byte> payload)
    {
        int headerSize = Write(destination, flags, sequence, segmentId, segmentIndex, segmentCount);
        payload.CopyTo(destination[headerSize..]);
        return headerSize + payload.Length;
    }

    /// <summary>
    /// Reads a header from the supplied buffer. Returns the number of bytes consumed.
    /// Throws if the buffer is too small for the declared flags.
    /// </summary>
    public static int Read(ReadOnlySpan<byte> buffer, out PacketFlags flags, out ushort sequence, out ushort segmentId, out byte segmentIndex, out byte segmentCount)
    {
        if (buffer.Length < FlagSize) throw new ArgumentException("Buffer too small for header.");

        int offset = 0;
        flags = (PacketFlags)buffer[offset++];
        sequence = 0;
        segmentId = 0;
        segmentIndex = 0;
        segmentCount = 0;

        if ((flags & (PacketFlags.Reliable | PacketFlags.Ack)) != 0)
        {
            if (buffer.Length < offset + SequenceSize) throw new ArgumentException("Buffer too small for sequence.");
            sequence = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            offset += 2;
        }

        if ((flags & PacketFlags.Segmented) != 0)
        {
            if (buffer.Length < offset + SegmentSize) throw new ArgumentException("Buffer too small for segment info.");
            segmentId = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            segmentIndex = buffer[offset + 2];
            segmentCount = buffer[offset + 3];
            offset += 4;
        }

        return offset;
    }
}