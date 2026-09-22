using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using CodeBoost.CodeAnalysis;
using CodeBoost.Extensions;
using CodeBoost.Performance;
using SynapseSocket.Core;
using SynapseSocket.Packets;
using SynapseSocket.Security;
using SynapseSocket.Transport;

/* ReturnAndNullifyReference takes the field by ref and writes null back into it, which the nullable annotations
 * on a non-nullable pool type cannot express. The nulling is the point: it is what stops a returned instance from
 * being reachable through a connection that has gone back to the pool. */
#pragma warning disable CS8601

namespace SynapseSocket.Connections;

/// <summary>
/// Represents the state of a single remote peer session, including reliable send/receive windows, keep-alive timestamps, and signature binding.
/// </summary>
public sealed partial class SynapseConnection : IPoolResettable
{
    /// <summary>
    /// Remote endpoint of this connection.
    /// </summary>
    [PoolResettableMember]
    public IPEndPoint RemoteEndPoint { get; private set; }
#if NET8_0_OR_GREATER
    /// <summary>
    /// <see cref="RemoteEndPoint"/> serialized once at <see cref="Initialize"/>, so the allocation-free
    /// <see cref="System.Net.Sockets.Socket"/> SocketAddress send and receive overloads never re-serialize the endpoint per
    /// datagram. Unavailable on netstandard2.1, whose Socket API has no SocketAddress overloads.
    /// </summary>
    [PoolResettableMember]
    public SocketAddress RemoteSocketAddress { get; private set; }
#endif
    /// <summary>
    /// The computed signature binding this connection to a physical identity.
    /// </summary>
    [PoolResettableMember]
    public ulong Signature { get; private set; }
    /// <summary>
    /// Index of this connection within <see cref="ConnectionManager._connections"/>.
    /// </summary>
    [PoolResettableMember]
    public int ConnectionsIndex { get; internal set; } = UnsetConnectionsIndex;
    /// <summary>
    /// Current lifecycle state.
    /// </summary>
    [PoolResettableMember]
    public ConnectionState State { get; internal set; }
    /// <summary>
    /// Monotonic ticks of the last received packet from this peer. Drives timeout detection.
    /// </summary>
    [PoolResettableMember]
    public long LastReceivedTicks { get; internal set; }
    /// <summary>
    /// Monotonic ticks of the last packet of any type sent to this peer, stamped by every connection-addressed send.
    /// Drives keep-alive scheduling: every datagram we send refreshes the peer's timeout, so a heartbeat is only owed
    /// by a side that has gone quiet. Scheduling off this rather than <see cref="LastReceivedTicks"/> is what keeps a
    /// receive-only peer emitting heartbeats. Such a peer takes a steady inbound stream and transmits nothing of its
    /// own, so its last-received is always fresh while its last-sent is not, and scheduling off last-received would
    /// leave it silent until the far side timed it out.
    /// </summary>
    [PoolResettableMember]
    public long LastSentTicks { get; internal set; }
    /// <summary>
    /// Monotonic ticks of the last sent keep-alive to this peer.
    /// </summary>
    [PoolResettableMember]
    public long LastKeepAliveSentTicks { get; internal set; }
    /// <summary>
    /// Monotonic ticks at which this connection was created, which is when its handshake began: sent by an outgoing connect, or
    /// received for an inbound one. Drives the handshake timeout while the connection is <see cref="ConnectionState.Pending"/>.
    /// </summary>
    /// <remarks>
    /// The handshake timeout is measured from here rather than from <see cref="LastReceivedTicks"/> because a pending
    /// connection still takes inbound traffic of other kinds. A peer whose handshake reply was lost considers the session
    /// connected and keeps sending, which would otherwise refresh the pending connection indefinitely.
    /// </remarks>
    [PoolResettableMember]
    internal long HandshakeStartedTicks { get; private set; }

    /// <summary>
    /// Monotonic ticks of the last handshake this side sent to the peer. An inbound handshake arriving within a short
    /// window of this stamp is the peer's answer rather than a fresh request, and must not reset the session or
    /// draw another handshake in reply, otherwise two peers running this code answer each other indefinitely.
    /// </summary>
    [PoolResettableMember]
    public long LastHandshakeSentTicks { get; internal set; }

    /// <summary>
    /// Number of consecutive keep-alives sent since the last received packet.
    /// Used to compute exponential backoff on the keep-alive send interval.
    /// Reset to zero whenever any inbound packet is received from this peer.
    /// </summary>
    [PoolResettableMember]
    internal int UnansweredKeepAlives;
    /// <summary>
    /// Next outbound reliable sequence number.
    /// </summary>
    [PoolResettableMember]
    internal ushort NextOutgoingSequence;
    /// <summary>
    /// Next expected inbound reliable sequence number (for ordered delivery).
    /// </summary>
    [PoolResettableMember]
    internal ushort NextExpectedSequence;
    /// <summary>
    /// Pending unacked reliable packets keyed by sequence. The engine is single-threaded (driven by the
    /// host's poll), so a plain <see cref="Dictionary{TKey,TValue}"/> is safe and no longer races.
    /// </summary>
    [PoolResettableMember]
    internal readonly Dictionary<ushort, PendingReliable> PendingReliableQueue = new();
    /// <summary>
    /// Outbound ACK sequence numbers queued for batch delivery when ACK batching is enabled.
    /// </summary>
    [PoolResettableMember]
    internal readonly Queue<ushort> PendingAcks = new();
    /// <summary>
    /// Out-of-order reliable packets awaiting delivery.
    /// </summary>
    /// <remarks>
    /// Keyed rather than a ring buffer over the sequence space. The space is 16-bit but the live window is capped at
    /// <see cref="SynapseSocket.Core.Configuration.SecurityConfig.MaximumOutOfOrderReliablePackets"/>, 64 by
    /// default, and is sparse, since entries exist only for sequences past a gap. A ring sized to the sequence
    /// space would be 65,536 slots to hold at most 64 live ones; a ring sized to the window needs wrap handling for
    /// no measurable gain at this size.
    /// </remarks>
    [PoolResettableMember]
    internal readonly Dictionary<ushort, ArraySegment<byte>> ReorderBuffer = [];
    /// <summary>
    /// Send-side splitter, rented from <see cref="CodeBoost.Performance.ResettableObjectPool{T}"/>
    /// on the first segmented send and returned to the pool on disconnect.
    /// Null until the first segmented send is issued on this connection.
    /// </summary>
    [PoolResettableMember]
    internal PacketSplitter? Splitter;
    /// <summary>
    /// Receive-side reassembler, rented from <see cref="CodeBoost.Performance.ResettableObjectPool{T}"/>
    /// on the first segmented receive and returned to the pool on disconnect.
    /// Null until the first segmented packet is received on this connection.
    /// </summary>
    [PoolResettableMember]
    internal PacketReassembler? Reassembler;
    /// <summary>
    /// Reference to the shared transmission engine used to send ACKs during batch flush.
    /// Set on connection establishment and cleared on disconnect.
    /// </summary>
    [PoolResettableMember]
    internal TransmissionEngine? TransmissionEngine;
    /// <summary>
    /// True once this connection has been torn down and queued for return to the pool, but before the return has
    /// actually happened. Guards against a second teardown queueing the same instance twice, which would hand one
    /// object to two future renters.
    /// </summary>
    [PoolResettableMember]
    internal bool IsPendingPoolReturn;
    /// <summary>
    /// Handshake attempts made while this connection has been Pending, including the initial one from Connect.
    /// </summary>
    [PoolResettableMember]
    internal uint HandshakeAttempts;
    /// <summary>
    /// Value used when ConnectionsIndex is not set.
    /// </summary>
    public const int UnsetConnectionsIndex = -1;

    /// <summary>
    /// Creates an uninitialised instance. Connections are rented from a pool and configured by
    /// <see cref="Initialize"/>, so the constructor deliberately does nothing.
    /// </summary>
    // ReSharper disable once EmptyConstructor
    public SynapseConnection() { }

    /// <summary>
    /// Creates a new connection record.
    /// </summary>
    /// <param name="remoteEndPoint">The peer's remote endpoint.</param>
    /// <param name="signature">The 64-bit signature that uniquely identifies this peer.</param>
    /// <param name="connectionsIndex">Index this connection occupies in the manager's connection list, or <see cref="UnsetConnectionsIndex"/> when it holds no slot.</param>
    public void Initialize(IPEndPoint remoteEndPoint, ulong signature, int connectionsIndex)
    {
        RemoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
#if NET8_0_OR_GREATER
        RemoteSocketAddress = remoteEndPoint.Serialize();
#endif
        Signature = signature;
        ConnectionsIndex = connectionsIndex;
        State = ConnectionState.Pending;

        long nowTicks = Clock.Ticks;

        HandshakeStartedTicks = nowTicks;

        LastReceivedTicks = nowTicks;
        // Seeded so a brand-new connection does not owe a keep-alive on its very first maintenance sweep.
        LastSentTicks = nowTicks;
    }

    /// <summary>
    /// Dequeues all pending ACK sequence numbers and sends each via <see cref="TransmissionEngine"/>.
    /// Called by the engine's maintenance step when ACK batching is enabled.
    /// </summary>
    internal void SendPendingAcks()
    {
        if (PendingAcks.Count == 0)
            return;

        TransmissionEngine?.SendAcks(this, PendingAcks);
    }

    /// <summary>
    /// Returns all pooled memory held by <paramref name="pendingReliable"/> back to <see cref="ArrayPool{T}.Shared"/>
    /// and returns the <see cref="PendingReliable"/> instance itself to its <see cref="ResettableObjectPool{T}"/>.
    /// Safe to call from any context (ingress ACK path, maintenance sweep, or on kick).
    /// </summary>
    internal static void ReleasePendingReliable(PendingReliable pendingReliable)
    {
        ResettableObjectPool<PendingReliable>.Return(pendingReliable);
    }

    /// <summary>
    /// Resets all per-session state for a reconnecting peer without returning the connection to the pool.
    /// Clears sequence numbers, the reorder buffer, pending ACKs, the pending reliable queue, and segmenters.
    /// Sets <see cref="State"/> to <see cref="ConnectionState.Disconnected"/> so the caller
    /// can re-initialise it through the normal handshake path.
    /// </summary>
    internal void ResetForReconnect()
    {
        ReleasePooledResources();

        NextOutgoingSequence = 0;
        NextExpectedSequence = 0;

        UnansweredKeepAlives = 0;
        LastKeepAliveSentTicks = 0;
        LastHandshakeSentTicks = 0;
        State = ConnectionState.Disconnected;
    }

    /// <summary>
    /// Releases every pooled buffer and pooled helper this connection holds, leaving its identity
    /// (<see cref="RemoteEndPoint"/>, <see cref="Signature"/>, <see cref="ConnectionsIndex"/>) intact so callers can
    /// still remove it from the lookup tables and raise <c>ConnectionClosed</c> with it afterwards.
    /// <para>
    /// This is the single cleanup implementation. Every path that tears a connection down calls it, which is what
    /// stops teardown paths from silently disagreeing about which of the three resource families they release.
    /// Safe to call more than once: every collection is cleared and every pooled helper is nulled as it is returned.
    /// </para>
    /// </summary>
    [PoolResettableMethod]
    private void ReleasePooledResources()
    {
        PendingAcks.Clear();

        foreach (KeyValuePair<ushort, PendingReliable> entry in PendingReliableQueue)
            ReleasePendingReliable(entry.Value);

        PendingReliableQueue.Clear();

        foreach (ArraySegment<byte> reorderSegment in ReorderBuffer.Values)
            reorderSegment.PoolArrayIntoShared();

        ReorderBuffer.Clear();

        ResettableObjectPool<PacketSplitter>.ReturnAndNullifyReference(ref Splitter);
        ResettableObjectPool<PacketReassembler>.ReturnAndNullifyReference(ref Reassembler);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The single place a connection is cleared. Every teardown funnels through
    /// <c>SynapseManager.TeardownConnection</c>, which queues the instance for return; the pool then calls this.
    /// <para>
    /// The engine guarantees only that <b>nothing inside Synapse</b> still references the instance when it is
    /// returned. Table entries are removed, NAT punches retired, and the actual return deferred to the end of
    /// <c>Poll</c> so no in-flight engine frame is holding it. An application that keeps the reference handed to it by
    /// <c>Connect</c>, <c>PacketReceivedEventArgs.Connection</c> or <c>ConnectionEventArgs.Connection</c> past the
    /// close notification is holding a recycled object, and that is the application's responsibility.
    /// </para>
    /// </remarks>
    public void OnReturn()
    {
        IsPendingPoolReturn = false;
        HandshakeAttempts = 0;
        RemoteEndPoint = null;
#if NET8_0_OR_GREATER
        RemoteSocketAddress = null;
#endif
        TransmissionEngine = null;
        Signature = SecurityProvider.UnsetSignature;
        ConnectionsIndex = UnsetConnectionsIndex;
        State = ConnectionState.Disconnected;

        HandshakeStartedTicks = 0;
        LastReceivedTicks = 0;
        LastSentTicks = 0;
        LastKeepAliveSentTicks = 0;
        LastHandshakeSentTicks = 0;
        UnansweredKeepAlives = 0;

        NextOutgoingSequence = 0;
        NextExpectedSequence = 0;

        ReleasePooledResources();

        /* Security. */
        _inboundRateCountersResetTick = 0;
        _receivedByPacketCount = 0;
        _receivedByBytesCount = 0;
    }

    /// <inheritdoc/>
    public void OnRent() { }

    /// <summary>
    /// A reliable packet that has been sent but not yet acknowledged.
    /// Instances are managed by <see cref="ResettableObjectPool{T}"/>; rent via
    /// <see cref="ResettableObjectPool{T}.Rent"/> and return via <see cref="ReleasePendingReliable"/>
    /// so all pooled buffers and the <see cref="PendingReliable"/> object itself are recycled.
    /// <see cref="Segments"/> holds the wire-ready slices; <see cref="BackingArray"/> is the single
    /// rented buffer that backs all of them and is the only array returned to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    internal sealed class PendingReliable : IPoolResettable
    {
        /// <summary>
        /// Wire-ready slices of <see cref="BackingArray"/>, one per logical segment.
        /// For unsegmented sends this list contains exactly one entry.
        /// </summary>
        [PoolResettableMember]
        public List<ArraySegment<byte>> Segments { get; private set; }
        /// <summary>
        /// The single rented buffer that backs all entries in <see cref="Segments"/>.
        /// Returned to <see cref="ArrayPool{T}.Shared"/> on ACK or eviction.
        /// </summary>
        [PoolResettableMember]
        public byte[]? BackingArray { get; private set; }
        /// <summary>
        /// Monotonic ticks when this packet was last sent or retransmitted.
        /// </summary>
        [PoolResettableMember]
        public long SentTicks;
        /// <summary>
        /// Number of retransmission attempts so far.
        /// </summary>
        [PoolResettableMember]
        public int Retries;
        /// <summary>
        /// One bit per segment index the peer has confirmed. Allocated once with the instance and reused, so
        /// selective retransmission costs no allocation.
        /// </summary>
        [PoolResettableMember]
        public readonly byte[] AckedSegments = new byte[PacketHeader.SegmentAckBitmapSize];
        /// <summary>
        /// Segments this entry carries; 1 for an unsegmented send.
        /// </summary>
        [PoolResettableMember]
        public int SegmentCount;

        /// <summary>
        /// Initialises this instance for a reliable send.
        /// </summary>
        /// <param name="segments">Rented list of wire-ready slices of <paramref name="backingArray"/>.</param>
        /// <param name="backingArray">The single rented buffer backing all entries in <paramref name="segments"/>.</param>
        /// <param name="sentTicks">Monotonic ticks at the time of the initial send.</param>
        [PoolResettableMethod]
        public void Initialize(List<ArraySegment<byte>> segments, byte[] backingArray, long sentTicks)
        {
            Segments = segments;
            BackingArray = backingArray;
            SentTicks = sentTicks;
            SegmentCount = segments.Count;

            Array.Clear(AckedSegments, 0, AckedSegments.Length);
        }

        /// <summary>
        /// True when the peer has confirmed the segment at <paramref name="index"/>.
        /// </summary>
        public bool IsSegmentAcked(int index) => (AckedSegments[index >> 3] & (1 << (index & 7))) != 0;

        /// <summary>
        /// Marks confirmed segments from a peer bitmap.
        /// </summary>
        /// <returns>True when every segment is now confirmed.</returns>
        public bool ApplyAckedBitmap(ReadOnlySpan<byte> bitmap)
        {
            int copyLength = Math.Min(bitmap.Length, AckedSegments.Length);

            for (int i = 0; i < copyLength; i++)
                AckedSegments[i] |= bitmap[i];

            for (int i = 0; i < SegmentCount; i++)
            {
                if (!IsSegmentAcked(i))
                    return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public void OnReturn()
        {
            Retries = 0;
            SentTicks = 0;
            SegmentCount = 0;

            Array.Clear(AckedSegments, 0, AckedSegments.Length);

            if (BackingArray is not null)
            {
                ArrayPool<byte>.Shared.Return(BackingArray);
                BackingArray = null;
            }

            if (Segments is not null)
            {
                ListPool<ArraySegment<byte>>.Return(Segments);
                Segments = null;
            }
        }

        /// <inheritdoc/>
        public void OnRent() { }
    }
}

