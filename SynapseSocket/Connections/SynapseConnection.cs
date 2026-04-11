using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
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
    /// For segmented sends, <see cref="Segments"/> holds rented <see cref="ArraySegment{T}"/>s whose backing arrays must be returned to <see cref="ArrayPool{T}.Shared"/> on ACK or eviction.
    /// <see cref="SegmentCount"/> is the logical count — <see cref="Segments"/> may be a larger rented array.
    /// </summary>
    internal sealed class PendingReliable
    {
        public ushort Sequence;
        public byte[] Payload = [];
        public int PacketLength;
        public ArraySegment<byte>[]? Segments;
        public int SegmentCount;
        public long SentTicks;
        public int Retries;
    }

    /// <summary>
    /// Returns all pooled memory held by <paramref name="pendingReliable"/> back to <see cref="ArrayPool{T}.Shared"/>.
    /// For segmented sends, returns each segment's backing array and the outer segments array.
    /// Safe to call from any context (ingress ACK path, maintenance sweep, or on kick).
    /// </summary>
    internal static void ReturnPendingReliableBuffers(PendingReliable pendingReliable)
    {
        if (pendingReliable.Segments is not null)
        {
            // All segments share a single backing buffer; returning segments[0].Array is sufficient.
            if (pendingReliable.SegmentCount > 0 && pendingReliable.Segments[0].Array is not null)
                ArrayPool<byte>.Shared.Return(pendingReliable.Segments[0].Array!);

            ArrayPool<ArraySegment<byte>>.Shared.Return(pendingReliable.Segments);
            pendingReliable.Segments = null;
        }
    }
}