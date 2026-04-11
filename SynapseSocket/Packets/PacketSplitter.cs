using System;
using System.Buffers;
using System.Threading;
using CodeBoost.Performance;

namespace SynapseSocket.Packets;

/// <summary>
/// Send-side segmentation helper. Splits outbound payloads into wire-ready segment packets
/// packed into a single rented backing buffer.
/// Instances are managed by <see cref="ResettableObjectPool{T}"/>; rent via the pool,
/// call <see cref="PacketSegmenter.Initialize"/> before use, and return via the pool when done.
/// </summary>
public sealed class PacketSplitter : PacketSegmenter
{
    /// <summary>
    /// Monotonically increasing counter used to assign unique segment IDs to each split operation.
    /// </summary>
    private int _segmentIdCounter;

    /// <summary>
    /// Splits a payload into one or more wire-ready segments packed into a single rented backing buffer.
    /// Every element in the returned array is a slice of the same backing buffer —
    /// only <c>segments[0].Array</c> needs to be returned to <see cref="ArrayPool{T}.Shared"/>.
    /// The outer <see cref="ArraySegment{T}"/> array is rented from
    /// <see cref="ArrayPool{T}"/> of <see cref="ArraySegment{T}"/> and must also be returned separately.
    /// Use <paramref name="segmentCount"/> (not the array length) to iterate — the rented
    /// outer array may be larger than needed.
    /// </summary>
    public ArraySegment<byte>[] Split(ReadOnlySpan<byte> payload, bool isReliable, out int segmentCount, ushort sequence = 0)
    {
        int segmentPayloadSize = (int)MaximumTransmissionUnit - PacketHeader.FlagSize - PacketHeader.SegmentSize - (isReliable ? PacketHeader.SequenceSize : 0);
        if (segmentPayloadSize <= 0)
            throw new InvalidOperationException("MTU too small for segmentation headers.");

        int totalSegments = (payload.Length + segmentPayloadSize - 1) / segmentPayloadSize;
        if (totalSegments > (int)MaximumSegments)
            throw new InvalidOperationException("Payload requires " + totalSegments + " segments but limit is " + MaximumSegments + ".");

        segmentCount = totalSegments;
        ushort segmentId = (ushort)Interlocked.Increment(ref _segmentIdCounter);
        PacketFlags flags = PacketFlags.Segmented | (isReliable ? PacketFlags.Reliable : PacketFlags.None);
        int headerSize = PacketHeader.ComputeHeaderSize(flags);

        // Single backing buffer: all N segment packets packed contiguously.
        // N * headerSize is a slight over-estimate because the last segment payload may be smaller,
        // but renting a touch more is cheaper than computing the exact size.
        int totalBufferSize = totalSegments * headerSize + payload.Length;
        byte[] backingBuffer = ArrayPool<byte>.Shared.Rent(totalBufferSize);
        ArraySegment<byte>[] segments = ArrayPool<ArraySegment<byte>>.Shared.Rent(totalSegments);

        int bufferOffset = 0;
        for (int i = 0; i < totalSegments; i++)
        {
            int segmentStartOffset = i * segmentPayloadSize;
            int segmentLength = Math.Min(segmentPayloadSize, payload.Length - segmentStartOffset);
            int written = PacketHeader.BuildPacket(backingBuffer.AsSpan(bufferOffset), flags, sequence, segmentId, (byte)i, (byte)totalSegments, payload.Slice(segmentStartOffset, segmentLength));
            segments[i] = new(backingBuffer, bufferOffset, written);
            bufferOffset += written;
        }

        return segments;
    }

    /// <inheritdoc/>
    public override void OnReturn()
    {
        _segmentIdCounter = 0;
        base.OnReturn();
    }
}
