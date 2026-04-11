using System;
using System.Net;
using CodeBoost.CodeAnalysis;
using CodeBoost.Performance;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Event arguments for <see cref="SynapseManager.ConnectionFailed"/>.
/// Obtain instances via <see cref="ConnectionFailedEventArgsPool.Rent"/>; do not retain after the handler returns.
/// </summary>
public sealed class ConnectionFailedEventArgs : EventArgs, IPoolResettable
{
    /// <summary>
    /// The remote endpoint involved in the failure, if known.
    /// </summary>
    [PoolResettableMember]
    public IPEndPoint? EndPoint { get; private set; }

    /// <summary>
    /// The reason the connection was rejected or failed.
    /// </summary>
    public ConnectionRejectedReason Reason { get; private set; }

    /// <summary>
    /// An optional message providing additional context.
    /// </summary>
    [PoolResettableMember]
    public string? Message { get; private set; }

    /// <summary>
    /// Initialises a new instance of <see cref="ConnectionFailedEventArgs"/>.
    /// </summary>
    public ConnectionFailedEventArgs() { }

    /// <summary>
    /// Initialises the instance for reuse via the object pool.
    /// </summary>
    public void Initialize(IPEndPoint? endPoint, ConnectionRejectedReason connectionRejectedReason, string? message)
    {
        EndPoint = endPoint;
        Reason = connectionRejectedReason;
        Message = message;
    }

    /// <inheritdoc/>
    public void OnReturn()
    {
        EndPoint = null;
        Message = null;
    }
    /// <inheritdoc/>
    public void OnRent()
    {
    }
}
