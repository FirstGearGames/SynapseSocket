using System;
using System.Net;
using CodeBoost.CodeAnalysis;
using CodeBoost.Performance;
using SynapseSocket.Connections;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Event arguments for <see cref="SynapseManager.ViolationDetected"/>.
/// Obtain instances via <see cref="ViolationEventArgsPool.Rent"/>; do not retain after the handler returns.
/// </summary>
public sealed class ViolationEventArgs : EventArgs, IPoolResettable
{
    /// <summary>
    /// The remote endpoint that triggered the violation.
    /// </summary>
    [PoolResettableMember]
    public IPEndPoint EndPoint { get; private set; }

    /// <summary>
    /// The computed signature of the offending peer.
    /// </summary>
    public ulong Signature { get; private set; }

    /// <summary>
    /// The reason this violation was raised.
    /// </summary>
    public ViolationReason Reason { get; private set; }

    /// <summary>
    /// The active connection for this peer, if one exists.
    /// Null for pre-connect violations.
    /// </summary>
    [PoolResettableMember]
    public SynapseConnection? Connection { get; private set; }

    /// <summary>
    /// The size of the offending packet in bytes.
    /// </summary>
    public int PacketSize { get; private set; }

    /// <summary>
    /// Optional details string providing additional context.
    /// </summary>
    [PoolResettableMember]
    public string? Details { get; private set; }

    /// <summary>
    /// The action the engine will take after all handlers return.
    /// Handlers may override this value to customize the response.
    /// </summary>
    public ViolationAction Action;

    public ViolationEventArgs() { }

    /// <summary>
    /// Initialises the instance for reuse via the object pool.
    /// </summary>
    public void Initialize(IPEndPoint endPoint, ulong signature, ViolationReason violationReason,
        SynapseConnection? synapseConnection, int packetSize, string? details, ViolationAction initialAction)
    {
        EndPoint = endPoint;
        Signature = signature;
        Reason = violationReason;
        Connection = synapseConnection;
        PacketSize = packetSize;
        Details = details;
        Action = initialAction;
    }

    public void OnReturn()
    {
        EndPoint = null;
        Connection = null;
        Details = null;
    }

    public void OnRent() { }
}
