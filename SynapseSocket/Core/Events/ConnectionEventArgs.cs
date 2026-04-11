using System;
using CodeBoost.CodeAnalysis;
using CodeBoost.Performance;
using SynapseSocket.Connections;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Event arguments for <see cref="SynapseManager.ConnectionEstablished"/> and <see cref="SynapseManager.ConnectionClosed"/>.
/// Obtain instances via <see cref="ConnectionEventArgsPool.Rent"/>; do not retain after the handler returns.
/// </summary>
public sealed class ConnectionEventArgs : EventArgs, IPoolResettable
{
    /// <summary>
    /// The connection that was established or closed.
    /// </summary>
    [PoolResettableMember]
    public SynapseConnection Connection { get; private set; }

    /// <summary>
    /// Initialises a new instance of <see cref="ConnectionEventArgs"/>.
    /// </summary>
    public ConnectionEventArgs() { }

    /// <summary>
    /// Initialises the instance for reuse via the object pool.
    /// </summary>
    public void Initialize(SynapseConnection synapseConnection)
    {
        Connection = synapseConnection;
    }

    /// <inheritdoc/>
    public void OnReturn()
    {
        Connection = null;
    }

    /// <inheritdoc/>
    public void OnRent()
    {
    }
}
