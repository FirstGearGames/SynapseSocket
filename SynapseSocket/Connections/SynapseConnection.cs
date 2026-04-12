using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using CodeBoost.CodeAnalysis;
using CodeBoost.Extensions;
using CodeBoost.Performance;
using SynapseSocket.Packets;

namespace SynapseSocket.Connections;

/// <summary>
/// Represents the state of a single remote peer session, including reliable send/receive windows, keep-alive timestamps, and signature binding.
/// </summary>
public sealed class SynapseConnection
{
    /// <summary>
    /// Remote endpoint of this connection.
    /// </summary>
    public IPEndPoint RemoteEndPoint { get; }
    /// <summary>
    /// The computed signature binding this connection to a physical identity.
    /// </summary>
    public ulong Signature { get; }
    /// <summary>
    /// Current lifecycle state.
    /// </summary>
    public ConnectionState State { get; internal set; }
    /// <summary>
    /// UTC ticks of the last received packet from this peer.
    /// </summary>
    public long LastReceivedTicks { get; internal set; }
    /// <summary>
    /// UTC ticks of the last sent keep-alive to this peer.
    /// </summary>
    public long LastKeepAliveSentTicks { get; internal set; }
    /// <summary>
    /// Number of consecutive keep-alives sent since the last received packet.
    /// Used to compute exponential backoff on the keep-alive send interval.
    /// Reset to zero whenever any inbound packet is received from this peer.
    /// </summary>
    internal int UnansweredKeepAlives;
    /// <summary>
    /// Next outbound reliable sequence number.
    /// </summary>
    internal ushort NextOutgoingSequence;
    /// <summary>
    /// Next expected inbound reliable sequence number (for ordered delivery).
    /// </summary>
    internal ushort NextExpectedSequence;
    /// <summary>
    /// Pending unacked reliable packets keyed by sequence.
    /// </summary>
    internal readonly ConcurrentDictionary<ushort, PendingReliable> PendingReliableQueue = [];
    /// <summary>
    /// Out-of-order reliable packets awaiting delivery.
    /// </summary>
    internal readonly Dictionary<ushort, ArraySegment<byte>> ReorderBuffer = [];
    /// <summary>
    /// Gate for reorder buffer and sequence manipulation.
    /// </summary>
    internal readonly object ReliableLock = new();
    /// <summary>
    /// Send-side splitter, rented from <see cref="CodeBoost.Performance.ResettableObjectPool{T}"/>
    /// on the first segmented send and returned to the pool on disconnect.
    /// Null until the first segmented send is issued on this connection.
    /// </summary>
    internal PacketSplitter? Splitter;
    /// <summary>
    /// Receive-side reassembler, rented from <see cref="CodeBoost.Performance.ResettableObjectPool{T}"/>
    /// on the first segmented receive and returned to the pool on disconnect.
    /// Null until the first segmented packet is received on this connection.
    /// </summary>
    internal PacketReassembler? Reassembler;

    /// <summary>
    /// Creates a new connection record.
    /// </summary>
    /// <param name="remoteEndPoint">The peer's remote endpoint.</param>
    /// <param name="signature">The 64-bit signature that uniquely identifies this peer.</param>
    public SynapseConnection(IPEndPoint remoteEndPoint, ulong signature)
    {
        RemoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
        Signature = signature;
        State = ConnectionState.Pending;
        LastReceivedTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// A reliable packet that has been sent but not yet acknowledged.
    /// Instances are managed by <see cref="ResettableObjectPool{T}"/>; rent via
    /// <see cref="ResettableObjectPool{T}.Rent"/> and return via <see cref="ReleasePendingReliable"/>
    /// so both the payload buffers and the <see cref="PendingReliable"/> object itself are recycled.
    /// For segmented sends, <see cref="Segments"/> holds rented <see cref="ArraySegment{T}"/>s whose backing arrays must be returned to <see cref="ArrayPool{T}.Shared"/> on ACK or eviction.
    /// <see cref="SegmentCount"/> is the logical count — <see cref="Segments"/> may be a larger rented array.
    /// </summary>
    internal sealed class PendingReliable : IPoolResettable
    {
        /// <summary>
        /// Sequence number of the pending packet.
        /// </summary>
        [PoolResettableMember]
        public ushort Sequence { get; private set; } // Unused?
        /// <summary>
        /// Rented packet buffer (header + payload) for unsegmented reliable sends. Null for segmented sends.
        /// Returned to <see cref="ArrayPool{T}.Shared"/> by <see cref="ReleasePendingReliable"/>.
        /// </summary>
        [PoolResettableMember]
        public byte[]? Payload { get; private set; }
        /// <summary>
        /// Total wire length of the packet, or of each segment when segmented.
        /// </summary>
        [PoolResettableMember]
        public int PacketLength { get; private set; }
        /// <summary>
        /// Per-segment buffers for segmented sends. Null for unsegmented packets.
        /// </summary>
        [PoolResettableMember]
        public List<ArraySegment<byte>> Segments { get; private set; }
        /// <summary>
        /// Logical number of valid entries in <see cref="Segments"/>. May be less than the array length.
        /// </summary>
        [PoolResettableMember]
        public int SegmentCount { get; private set; }
        /// <summary>
        /// UTC ticks when this packet was last sent or retransmitted.
        /// </summary>
        [PoolResettableMember]
        public long SentTicks;
        /// <summary>
        /// Number of retransmission attempts so far.
        /// </summary>
        [PoolResettableMember]
        public int Retries;

        [PoolResettableMethod]
        public void Initialize(ushort sequence, List<ArraySegment<byte>> segments, int segmentCount, long sentTicks)
        {
            Sequence = sequence;
            Segments = segments;
            SegmentCount = segmentCount;
            SentTicks = sentTicks;
        }

        [PoolResettableMethod]
        public void Initialize(ushort sequence, byte[] payload, int packetLength, long sentTicks)
        {
            Sequence = sequence;
            Payload = payload;
            PacketLength = packetLength;
            SentTicks = sentTicks;
        }

        /// <inheritdoc/>
        public void OnRent() { }

        /// <inheritdoc/>
        public void OnReturn()
        {
            Sequence = 0;
            Retries = 0;
            PacketLength = 0;
            SegmentCount = 0;
            SentTicks = 0;
            
            /* Only Segments or Payload will have value, never both. */
            if (Segments is not null)
            {
                foreach (ArraySegment<byte> arraySegment in Segments)
                    arraySegment.PoolArrayIntoShared();

                ListPool<ArraySegment<byte>>.Return(Segments);
                Segments = null;
            }
            else if (Payload is not null)
            {
                ArrayPool<byte>.Shared.Return(Payload);
                Payload = null;
            }
        }
    }

    /// <summary>
    /// Returns all pooled memory held by <paramref name="pendingReliable"/> back to <see cref="ArrayPool{T}.Shared"/>
    /// and returns the <see cref="PendingReliable"/> instance itself to its <see cref="ResettableObjectPool{T}"/>.
    /// For segmented sends, returns each segment's backing array and the outer segments array.
    /// Safe to call from any context (ingress ACK path, maintenance sweep, or on kick).
    /// </summary>
    internal static void ReleasePendingReliable(PendingReliable pendingReliable)
    {
        ResettableObjectPool<PendingReliable>.Return(pendingReliable);
    }

    /// <summary>
    /// Drains the pending reliable queue of <paramref name="synapseConnection"/>, releasing every entry's
    /// pooled buffers and returning each <see cref="PendingReliable"/> to its pool.
    /// Call on connection teardown to avoid leaking rented buffers.
    /// </summary>
    internal static void DrainPendingReliableQueue(SynapseConnection synapseConnection)
    {
        foreach (KeyValuePair<ushort, PendingReliable> entry in synapseConnection.PendingReliableQueue)
            ReleasePendingReliable(entry.Value);

        synapseConnection.PendingReliableQueue.Clear();
    }
}