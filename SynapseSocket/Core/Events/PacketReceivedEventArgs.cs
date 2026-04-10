using System;
using CodeBoost.CodeAnalysis;
using CodeBoost.Performance;
using SynapseSocket.Connections;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Event arguments for <see cref="SynapseManager.PacketReceived"/>.
/// Obtain instances via <see cref="ResettableObjectPool{T}"/>; do not retain after the handler returns.
/// The <see cref="Payload"/> is backed by a pooled buffer and is only valid for the duration
/// of the handler — copy the data if you need to retain it beyond the callback.
/// </summary>
public sealed class PacketReceivedEventArgs : EventArgs, IPoolResettable
{
    /// <summary>
    /// The connection that sent the payload.
    /// </summary>
    [PoolResettableMember]
    public SynapseConnection Connection { get; private set; }

    /// <summary>
    /// The received payload. Backed by a pooled buffer — valid only for the duration of the handler.
    /// Copy if you need to retain the data beyond the callback.
    /// </summary>
    [PoolResettableMember]
    public ArraySegment<byte> Payload { get; private set; }

    /// <summary>
    /// True if the packet was delivered via the reliable channel.
    /// </summary>
    public bool Reliable { get; private set; }

    public PacketReceivedEventArgs() { }

    /// <summary>
    /// Initialises the instance for reuse via the object pool.
    /// </summary>
    public void Initialize(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool reliable)
    {
        Connection = synapseConnection;
        Payload = payload;
        Reliable = reliable;
    }

    public void OnReturn()
    {
        Connection = null;
        Payload = default;
    }
    public void OnRent()
    {
    }
}
