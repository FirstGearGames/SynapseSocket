using System;
using System.Net;
using CodeBoost.CodeAnalysis;
using CodeBoost.Performance;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Event arguments for <see cref="SynapseManager.PacketSent"/>.
/// Fires after the send has completed — <see cref="Payload"/> is the original caller-supplied segment.
/// Do not retain this instance after the handler returns; it is returned to the pool immediately after.
/// </summary>
public sealed class PacketSentEventArgs : EventArgs, IPoolResettable
{
    /// <summary>
    /// The remote endpoint the packet was sent to.
    /// </summary>
    [PoolResettableMember]
    public IPEndPoint EndPoint { get; private set; }

    /// <summary>
    /// The original payload segment supplied by the caller.
    /// <see cref="ArraySegment{T}.Count"/> gives the logical byte count.
    /// </summary>
    public ArraySegment<byte> Payload { get; private set; }

    /// <summary>
    /// True if the packet was sent via the reliable channel.
    /// </summary>
    public bool Reliable { get; private set; }

    public PacketSentEventArgs() { }

    /// <summary>
    /// Initialises the instance for reuse via the object pool.
    /// </summary>
    public void Initialize(IPEndPoint endPoint, ArraySegment<byte> payload, bool reliable)
    {
        EndPoint = endPoint;
        Payload = payload;
        Reliable = reliable;
    }

    public void OnReturn()
    {
        EndPoint = null;
        Payload = default;
    }

    public void OnRent() { }
}
