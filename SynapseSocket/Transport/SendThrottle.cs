using System;
using System.Collections.Generic;
using CodeBoost.Performance;
using SynapseSocket.Connections;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Transport;

/// <summary>
/// One connection's send throttle: the release currently open to it, and the queue of wire-ready datagrams waiting for a later
/// release, oldest first. See <see cref="SendThrottleConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// A datagram the engine builds for the queue is built straight into a block of the queue's own, so queueing costs no copy beyond
/// the one every send already makes. Blocks are rented from <see cref="TrackedArrayPool{T0}"/> only when a datagram first has to
/// wait, and all of them go back once the queue drains, so an idle connection holds none.
/// </para>
/// <para>
/// A datagram whose bytes already live elsewhere is queued by reference instead: the segments of an unreliable split, whose
/// backing array goes back to the pool once the last of them is sent, and a reliable packet, whose bytes belong to its
/// <see cref="SynapseConnection.PendingReliable"/>. A queued reliable packet holds its resend timer at
/// <see cref="SynapseConnection.PendingReliable.UnsetSentTicks"/> until it actually leaves.
/// </para>
/// <para>
/// Used only on the thread that calls <see cref="Core.SynapseManager.Poll"/>, like the rest of the connection.
/// </para>
/// </remarks>
internal sealed class SendThrottle
{
    /// <summary>
    /// The bytes of every datagram in the queue.
    /// </summary>
    internal int QueuedBytes { get; private set; }
    /// <summary>
    /// True when no datagram is waiting.
    /// </summary>
    internal bool IsEmpty => _queuedDatagrams.Count == 0;
    /// <summary>
    /// The waiting datagrams, oldest first.
    /// </summary>
    private readonly Queue<QueuedDatagram> _queuedDatagrams = new();
    /// <summary>
    /// The blocks holding datagrams built for the queue, oldest first. The last is the one being written.
    /// </summary>
    private readonly Queue<byte[]> _blocks = new();
    /// <summary>
    /// The block being written, the last of <see cref="_blocks"/>, or null when the queue holds no block.
    /// </summary>
    private byte[]? _writeBlock;
    /// <summary>
    /// The offset in <see cref="_writeBlock"/> the next datagram is written at.
    /// </summary>
    private int _writeOffset;
    /// <summary>
    /// When the open release began, or <see cref="UnsetReleaseStartedTicks"/> before the first.
    /// </summary>
    private long _releaseStartedTicks = UnsetReleaseStartedTicks;
    /// <summary>
    /// The bytes sent in the open release.
    /// </summary>
    private int _releaseSentBytes;
    /// <summary>
    /// The size of each block rented for the queue, unless a single datagram is larger.
    /// </summary>
    private const int BlockSize = 64 * 1024;
    /// <summary>
    /// The value of <see cref="_releaseStartedTicks"/> before the first release.
    /// </summary>
    private const long UnsetReleaseStartedTicks = long.MinValue;

    /// <summary>
    /// Opens a new release when <paramref name="minimumReleaseIntervalTicks"/> have passed since the open one began, or when none
    /// has begun.
    /// </summary>
    /// <param name="nowTicks">The current time in <see cref="DateTime.Ticks"/> units.</param>
    /// <param name="minimumReleaseIntervalTicks">The least time between one release beginning and the next.</param>
    internal void OpenReleaseIfDue(long nowTicks, long minimumReleaseIntervalTicks)
    {
        if (_releaseStartedTicks != UnsetReleaseStartedTicks && nowTicks - _releaseStartedTicks < minimumReleaseIntervalTicks)
            return;

        _releaseStartedTicks = nowTicks;
        _releaseSentBytes = 0;
    }

    /// <summary>
    /// Counts a datagram against the open release when the release can still cover it.
    /// </summary>
    /// <param name="length">The datagram's length.</param>
    /// <param name="maximumBytesPerRelease">The most bytes one release sends.</param>
    /// <returns>True when the datagram may be sent now. The first datagram of a release always may, so a release smaller than a datagram still makes progress.</returns>
    internal bool TryCountRelease(int length, int maximumBytesPerRelease)
    {
        if (_releaseSentBytes != 0 && _releaseSentBytes + length > maximumBytesPerRelease)
            return false;

        _releaseSentBytes += length;

        return true;
    }

    /// <summary>
    /// Counts a datagram sent outside the release's limit against the open release, so the releases after it still wait their turn.
    /// </summary>
    /// <param name="length">The datagram's length.</param>
    internal void CountForcedRelease(int length) => _releaseSentBytes += length;

    /// <summary>
    /// Returns space for a datagram of <paramref name="length"/> at the end of the queue's blocks, renting a block when the one
    /// being written cannot hold it. The caller builds the datagram into the space and then calls <see cref="EnqueueReserved"/>.
    /// </summary>
    /// <param name="length">The datagram's length.</param>
    /// <returns>The space to build the datagram into.</returns>
    internal ArraySegment<byte> Reserve(int length)
    {
        if (_writeBlock is null || _writeBlock.Length - _writeOffset < length)
        {
            _writeBlock = TrackedArrayPool<byte>.Rent(Math.Max(BlockSize, length));
            _writeOffset = 0;
            _blocks.Enqueue(_writeBlock);
        }

        return new(_writeBlock, _writeOffset, length);
    }

    /// <summary>
    /// Queues the datagram built into the space the last <see cref="Reserve"/> returned.
    /// </summary>
    /// <param name="writtenLength">The bytes the datagram took, at most the length reserved.</param>
    internal void EnqueueReserved(int writtenLength)
    {
        _queuedDatagrams.Enqueue(new(new(_writeBlock!, _writeOffset, writtenLength), isInBlock: true, arrayToReturn: null, pendingReliableToStart: null));
        _writeOffset += writtenLength;
        QueuedBytes += writtenLength;
    }

    /// <summary>
    /// Queues a datagram whose bytes live outside the queue's blocks.
    /// </summary>
    /// <param name="segment">The datagram.</param>
    /// <param name="arrayToReturn">An array to return to <see cref="TrackedArrayPool{T0}"/> once this datagram is sent, or null.</param>
    /// <param name="pendingReliableToStart">A reliable packet whose resend timer starts once this datagram is sent, or null.</param>
    internal void EnqueueReference(ArraySegment<byte> segment, byte[]? arrayToReturn, SynapseConnection.PendingReliable? pendingReliableToStart)
    {
        _queuedDatagrams.Enqueue(new(segment, isInBlock: false, arrayToReturn, pendingReliableToStart));
        QueuedBytes += segment.Count;
    }

    /// <summary>
    /// Reads the length of the oldest datagram.
    /// </summary>
    /// <param name="length">The oldest datagram's length.</param>
    /// <returns>True when a datagram is waiting.</returns>
    internal bool TryPeekLength(out int length)
    {
        if (_queuedDatagrams.Count == 0)
        {
            length = 0;

            return false;
        }

        length = _queuedDatagrams.Peek().Segment.Count;

        return true;
    }

    /// <summary>
    /// Takes the oldest datagram off the queue. Its bytes stay valid until <see cref="Complete"/> is called for it.
    /// </summary>
    /// <returns>The oldest datagram.</returns>
    /// <remarks>The datagrams are taken in the order the blocks were written, so every block before this datagram's has been sent and completed, and goes back to the pool here.</remarks>
    internal QueuedDatagram Dequeue()
    {
        QueuedDatagram queuedDatagram = _queuedDatagrams.Dequeue();
        QueuedBytes -= queuedDatagram.Segment.Count;

        if (queuedDatagram.IsInBlock)
        {
            while (!ReferenceEquals(_blocks.Peek(), queuedDatagram.Segment.Array))
                TrackedArrayPool<byte>.Return(_blocks.Dequeue());
        }

        return queuedDatagram;
    }

    /// <summary>
    /// Finishes a datagram taken by <see cref="Dequeue"/> once it has been sent, or once sending it failed.
    /// </summary>
    /// <param name="queuedDatagram">The datagram.</param>
    /// <param name="nowTicks">The current time in <see cref="DateTime.Ticks"/> units, which starts a reliable packet's resend timer.</param>
    internal void Complete(in QueuedDatagram queuedDatagram, long nowTicks)
    {
        if (queuedDatagram.ArrayToReturn is not null)
            TrackedArrayPool<byte>.Return(queuedDatagram.ArrayToReturn);

        if (queuedDatagram.PendingReliableToStart is not null)
            queuedDatagram.PendingReliableToStart.SentTicks = nowTicks;

        if (_queuedDatagrams.Count == 0)
            ReturnBlocks();
    }

    /// <summary>
    /// Discards every waiting datagram and returns the queue's memory, and forgets the open release.
    /// </summary>
    /// <remarks>A reliable packet's bytes belong to its <see cref="SynapseConnection.PendingReliable"/>, which the connection releases straight after this.</remarks>
    internal void Clear()
    {
        while (_queuedDatagrams.Count > 0)
        {
            byte[]? arrayToReturn = _queuedDatagrams.Dequeue().ArrayToReturn;

            if (arrayToReturn is not null)
                TrackedArrayPool<byte>.Return(arrayToReturn);
        }

        QueuedBytes = 0;
        ReturnBlocks();

        _releaseStartedTicks = UnsetReleaseStartedTicks;
        _releaseSentBytes = 0;
    }

    /// <summary>
    /// Returns every block to the pool.
    /// </summary>
    private void ReturnBlocks()
    {
        while (_blocks.Count > 0)
            TrackedArrayPool<byte>.Return(_blocks.Dequeue());

        _writeBlock = null;
        _writeOffset = 0;
    }

    /// <summary>
    /// One datagram waiting in the queue.
    /// </summary>
    internal readonly struct QueuedDatagram
    {
        /// <summary>
        /// The wire-ready datagram.
        /// </summary>
        public readonly ArraySegment<byte> Segment;
        /// <summary>
        /// True when the datagram lives in one of the queue's blocks.
        /// </summary>
        public readonly bool IsInBlock;
        /// <summary>
        /// An array to return to <see cref="TrackedArrayPool{T0}"/> once the datagram is sent, or null.
        /// </summary>
        public readonly byte[]? ArrayToReturn;
        /// <summary>
        /// A reliable packet whose resend timer starts once the datagram is sent, or null.
        /// </summary>
        public readonly SynapseConnection.PendingReliable? PendingReliableToStart;

        /// <summary>
        /// Creates a queued datagram.
        /// </summary>
        /// <param name="segment">The wire-ready datagram.</param>
        /// <param name="isInBlock">True when the datagram lives in one of the queue's blocks.</param>
        /// <param name="arrayToReturn">An array to return to the pool once the datagram is sent, or null.</param>
        /// <param name="pendingReliableToStart">A reliable packet whose resend timer starts once the datagram is sent, or null.</param>
        public QueuedDatagram(ArraySegment<byte> segment, bool isInBlock, byte[]? arrayToReturn, SynapseConnection.PendingReliable? pendingReliableToStart)
        {
            Segment = segment;
            IsInBlock = isInBlock;
            ArrayToReturn = arrayToReturn;
            PendingReliableToStart = pendingReliableToStart;
        }
    }
}
