using System;
using System.Buffers;
using System.Collections.Generic;
using CodeBoost.CodeAnalysis;
using CodeBoost.Extensions;
using CodeBoost.Performance;

namespace SynapseSocket.Packets;

/// <summary>
/// Receive-side segmentation helper. Feeds arriving segment packets into per-stream reassembly
/// buffers and emits the fully rebuilt payload when all segments have arrived.
/// Instances are managed by <see cref="ResettableObjectPool{T}"/>; rent via the pool,
/// call <see cref="PacketSegmenter.Initialize"/> before use, and return via the pool when done.
/// </summary>
public sealed class PacketReassembler : PacketSegmenter
{
    private readonly Dictionary<ushort, SegmentAssembly> _currentSegments = new();
    private readonly object _lock = new();

    /// <summary>
    /// Feeds a received segment into the reassembly buffer.
    /// Returns true and outputs the fully reassembled payload when the final segment arrives.
    /// The caller is responsible for returning <paramref name="assembledSegments"/>.Array to
    /// <see cref="ArrayPool{T}.Shared"/> when done.
    /// The completed <see cref="SegmentAssembly"/> is automatically returned to the pool.
    /// If a protocol violation is detected (e.g. the same segmentId is seen with a different
    /// segmentCount or reliability flag than previously declared), <paramref name="isProtocolViolation"/>
    /// is set to true, the stale assembly is evicted, and the method returns false. Callers should
    /// treat this as grounds for kicking/blacklisting the peer.
    /// </summary>
    public bool TryReassemble(ushort segmentId, byte segmentIndex, byte segmentCount, ReadOnlySpan<byte> segmentData, bool isReliable, out ArraySegment<byte> assembledSegments, out bool isProtocolViolation)
    {
        assembledSegments = default;
        isProtocolViolation = false;

        if (segmentCount == 0 || segmentCount > MaximumSegments || segmentIndex >= segmentCount)
            return false;

        lock (_lock)
        {
            if (!_currentSegments.TryGetValue(segmentId, out SegmentAssembly? segmentAssembly))
            {
                segmentAssembly = ResettableObjectPool<SegmentAssembly>.Rent();
                segmentAssembly.Initialize(segmentCount, isReliable);
                _currentSegments[segmentId] = segmentAssembly;
            }
            else if (segmentAssembly.SegmentCount != segmentCount || segmentAssembly.IsReliable != isReliable)
            {
                // Same segmentId reused with a different declared segmentCount or reliability flag.
                // This is either a sender bug or a malicious attempt to desync reassembly state.
                // Evict the stale assembly and signal a protocol violation to the caller.
                _currentSegments.Remove(segmentId);
                ResettableObjectPool<SegmentAssembly>.Return(segmentAssembly);
                isProtocolViolation = true;

                return false;
            }

            segmentAssembly.Add(segmentIndex, segmentData);

            if (segmentAssembly.TryGetAssembledSegments(out assembledSegments))
            {
                _currentSegments.Remove(segmentId);
                ResettableObjectPool<SegmentAssembly>.Return(segmentAssembly);

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes and returns to pool incomplete segment assemblies that have exceeded the timeout.
    /// This ensures that assemblies from connections that disconnect before completing are not held indefinitely.
    /// </summary>
    public void RemoveExpiredSegments(long nowTicks, long timeoutTicks)
    {
        lock (_lock)
        {
            List<ushort> toRemove = ListPool<ushort>.Rent();
            try
            {
                foreach (KeyValuePair<ushort, SegmentAssembly> keyValuePair in _currentSegments)
                {
                    if (nowTicks - keyValuePair.Value.FirstReceivedTicks > timeoutTicks)
                        toRemove.Add(keyValuePair.Key);
                }
                foreach (ushort id in toRemove)
                {
                    if (_currentSegments.Remove(id, out SegmentAssembly? evicted))
                        ResettableObjectPool<SegmentAssembly>.Return(evicted);
                }
            }
            finally
            {
                ListPool<ushort>.Return(toRemove);
            }
        }
    }

    /// <inheritdoc/>
    public override void OnReturn()
    {
        lock (_lock)
        {
            foreach (SegmentAssembly segmentAssembly in _currentSegments.Values)
                ResettableObjectPool<SegmentAssembly>.Return(segmentAssembly);
            _currentSegments.Clear();
        }
        base.OnReturn();
    }

    private sealed class SegmentAssembly : IPoolResettable
    {
        public int SegmentCount => _segmentCount;
        public long FirstReceivedTicks;
        public bool IsReliable;

        // _segments is rented from ListPool on Initialize; each slot is pre-filled with null
        // so segments can be stored by index as they arrive (out-of-order safe).
        // _lengths tracks each segment's actual data length (ArrayPool may over-allocate).
        [PoolResettableMember]
        private List<ArraySegment<byte>>? _segments;
        /// <summary>
        /// Expected count of segments.
        /// </summary>
        private int _segmentCount;
        /// <summary>
        /// Count of received segments.
        /// </summary>
        private int _receivedCount;
        /// <summary>
        /// Total length of added segments.
        /// </summary>
        private int _totalLength;
        public SegmentAssembly() { }
        
        public void Initialize(byte segmentCount, bool isReliable)
        {
            _segmentCount = segmentCount;
            IsReliable = isReliable;

            _segments = ListPool<ArraySegment<byte>>.Rent();
            for (int i = _segments.Count; i < segmentCount; i++)
                _segments.Add(null);
        }

        public void Add(byte segmentIndex, ReadOnlySpan<byte> segmentData)
        {
            if (_segments![segmentIndex].Array is not null)
                return;

            if (_receivedCount == 0)
                FirstReceivedTicks = DateTime.UtcNow.Ticks;

            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(segmentData.Length);
            segmentData.CopyTo(rentedBuffer);

            int segmentLength = segmentData.Length;
            _segments[segmentIndex] = new(rentedBuffer, offset: 0, segmentLength);

            _receivedCount++;
            _totalLength += segmentLength;
        }

        /// <summary>
        /// Copies all segments into a single rented buffer and returns it as an
        /// <see cref="ArraySegment{T}"/> scoped to the exact reassembled length.
        /// The caller is responsible for returning <see cref="ArraySegment{T}.Array"/> to
        /// <see cref="ArrayPool{T}.Shared"/> when done.
        /// </summary>
        public bool TryGetAssembledSegments(out ArraySegment<byte> assembledSegment)
        {
            // Exit if all segments have not been received.
            if (_receivedCount != _segmentCount)
            {
                assembledSegment = default;
                return false;
            }

            byte[] reassembledBytes = ArrayPool<byte>.Shared.Rent(_totalLength);

            int offset = 0;
            for (int i = 0; i < _segmentCount; i++)
            {
                int segmentLength = _segments![i].Count;
                Buffer.BlockCopy(_segments![i].Array!, srcOffset: 0, dst: reassembledBytes, offset, count: segmentLength);

                offset += segmentLength;
            }

            assembledSegment = new(reassembledBytes, offset: 0, _totalLength);
            return true;
        }

        /// <inheritdoc/>
        public void OnRent() { }

        /// <inheritdoc/>
        public void OnReturn()
        {
            if (_segments is not null)
            {
                foreach (ArraySegment<byte> arraySegment in _segments)
                    arraySegment.PoolArrayIntoShared();
                ListPool<ArraySegment<byte>>.ReturnAndNullifyReference(ref _segments);
            }
            _segmentCount = 0;
            _receivedCount = 0;
            _totalLength = 0;
            FirstReceivedTicks = 0;
            IsReliable = false;
        }
    }
}