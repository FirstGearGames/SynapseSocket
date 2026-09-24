using SynapseSocket.Connections;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Event arguments for <see cref="SynapseManager.ConnectionEstablished"/>, <see cref="SynapseManager.ConnectionClosed"/> and
/// <see cref="SynapseManager.ConnectionReleased"/>.
/// </summary>
public struct ConnectionEventArgs
{
    /// <summary>
    /// The connection that was established, closed or released.
    /// </summary>
    public SynapseConnection Connection { get; private set; }

    /// <summary>
    /// Initialises a new instance of <see cref="ConnectionEventArgs"/>.
    /// </summary>
    public ConnectionEventArgs(SynapseConnection synapseConnection)
    {
        Connection = synapseConnection;
    }
}
