using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using CodeBoost.Performance;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Connections;

/// <summary>
/// Handles the lifecycle and state of active sessions.
/// Thread-safe.
/// </summary>
public sealed class ConnectionManager
{
    /// <summary>
    /// Current live connection count.
    /// </summary>
    public int Count => _byEndPoint.Count;
    /// <summary>
    /// Raised when two connections produce the same 64-bit signature (birthday-bound collision).
    /// The newer connection wins the reverse-lookup slot. Subscribe for telemetry; no corrective action is taken automatically.
    /// </summary>
    public event SignatureCollisionDelegate? SignatureCollisionDetected;
    /// <summary>
    /// Maps a connection's EndPointKey to its <see cref="SynapseConnection"/>.
    /// </summary>
    private readonly ConcurrentDictionary<EndPointKey, SynapseConnection> _byEndPoint = [];
    /// <summary>
    /// Maps a connection's 64-bit signature to its <see cref="SynapseConnection"/>.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, SynapseConnection> _bySignature = [];
    /// <summary>
    /// Connections as an index-based collection.
    /// </summary>
    private readonly List<SynapseConnection> _connections = [];

    /// <summary>
    /// Tries to find an existing connection by its remote endpoint.
    /// </summary>
    /// <param name="endPoint">The remote endpoint to look up.</param>
    /// <param name="connection">When this method returns, contains the matching connection, or null if not found.</param>
    /// <returns>True if a connection was found for the given endpoint; otherwise false.</returns>
    public bool TryGet(IPEndPoint endPoint, out SynapseConnection? connection)
    {
        return _byEndPoint.TryGetValue(new(endPoint), out connection);
    }

    /// <summary>
    /// Tries to find a connection by signature.
    /// </summary>
    /// <param name="signature">The 64-bit signature to look up.</param>
    /// <param name="connection">When this method returns, contains the matching connection, or null if not found.</param>
    /// <returns>True if a connection was found for the given signature; otherwise false.</returns>
    public bool TryGetBySignature(ulong signature, out SynapseConnection? connection) => _bySignature.TryGetValue(signature, out connection);

    /// <summary>
    /// Registers a new connection.
    /// Returns the existing one if already present.
    /// </summary>
    /// <param name="endPoint">The remote endpoint identifying the peer.</param>
    /// <param name="signature">The 64-bit signature associated with the peer.</param>
    /// <param name="factory">Factory invoked to create a new connection if one does not already exist for the given endpoint.</param>
    /// <returns>The existing connection for the endpoint, or the newly created one.</returns>
    public SynapseConnection GetOrAdd(IPEndPoint endPoint, ulong signature)
    {
        EndPointKey endPointKey = new(endPoint);

        if (!_byEndPoint.TryGetValue(endPointKey, out SynapseConnection synapseConnection))
        {
            synapseConnection = ResettableObjectPool<SynapseConnection>.Rent();

            int connectionsIndex = _connections.Count;
            synapseConnection.Initialize(endPoint, signature, connectionsIndex);

            _byEndPoint[endPointKey] = synapseConnection;
            _connections.Add(synapseConnection);
        }

        if (!_bySignature.TryAdd(signature, synapseConnection))
        {
            // Two distinct endpoints produced the same 64-bit signature.
            // Overwrite so reverse lookup stays current, but surface the collision.
            _bySignature[signature] = synapseConnection;
            SignatureCollisionDetected?.Invoke(signature);
        }

        return synapseConnection;
    }

    /// <summary>
    /// Removes and returns a connection by endpoint.
    /// </summary>
    /// <param name="endPoint">The remote endpoint of the connection to remove.</param>
    /// <param name="removedSynapseConnection">When this method returns, contains the removed connection, or null if not found.</param>
    /// <returns>True if the connection was found and removed; otherwise false.</returns>
    public bool Remove(IPEndPoint endPoint, out SynapseConnection? removedSynapseConnection)
    {
        bool isRemoved = _byEndPoint.TryRemove(new(endPoint), out removedSynapseConnection);

        if (isRemoved && removedSynapseConnection is not null)
        {
            _bySignature.TryRemove(removedSynapseConnection.Signature, out _);

            int connectionsIndex = removedSynapseConnection.ConnectionsIndex;
            if (connectionsIndex is not SynapseConnection.UnsetConnectionsIndex)
            {
                int lastConnectionsIndex = _connections.Count - 1;

                /* If connectionsIndex is the not last entry then
                 * move the last connections entry to connectionsIndex
                 * and update the ConnectionsIndex member for the moved
                 * connection. */
                if (connectionsIndex < lastConnectionsIndex)
                {
                    SynapseConnection otherConnection = _connections[lastConnectionsIndex];
                    otherConnection.ConnectionsIndex = connectionsIndex;

                    _connections[connectionsIndex] = otherConnection;
                    removedSynapseConnection.ConnectionsIndex = SynapseConnection.UnsetConnectionsIndex;
                }

                _connections.RemoveAt(connectionsIndex);
            }
        }

        return isRemoved;
    }

    /// <summary>
    /// Enumerates all currently tracked connections.
    /// Snapshot is taken at call time.
    /// </summary>
    /// <returns>A sequence containing all active <see cref="SynapseConnection"/> instances at the moment of enumeration.</returns>
    //todo replace this with iteratable version. see if other timed checks can be done here, such as keep alive.
    public IEnumerable<SynapseConnection> Snapshot()
    {
        foreach (KeyValuePair<EndPointKey, SynapseConnection> keyValuePair in _byEndPoint)
            yield return keyValuePair.Value;
    }

    /// <summary>
    /// Key wrapper that compares IPEndPoint by address+port without boxing.
    /// </summary>
    private readonly struct EndPointKey : IEquatable<EndPointKey>
    {
        /// <summary>
        /// The wrapped remote endpoint.
        /// </summary>
        private readonly IPEndPoint _endPoint;

        /// <summary>
        /// Initializes a new <see cref="EndPointKey"/> wrapping the given endpoint.
        /// </summary>
        /// <param name="endPoint">The remote endpoint to wrap.</param>
        public EndPointKey(IPEndPoint endPoint)
        {
            _endPoint = endPoint;
        }

        /// <summary>
        /// Compares this key to another by address and port.
        /// </summary>
        /// <param name="other">The other key to compare against.</param>
        /// <returns>True if both keys represent the same address and port; otherwise false.</returns>
        public bool Equals(EndPointKey other)
        {
            if (_endPoint is null || other._endPoint is null)
                return ReferenceEquals(_endPoint, other._endPoint);

            return _endPoint.Port == other._endPoint.Port && _endPoint.Address.Equals(other._endPoint.Address);
        }

        /// <summary>
        /// Compares this key to a boxed object.
        /// </summary>
        /// <param name="obj">The object to compare against.</param>
        /// <returns>True if <paramref name="obj"/> is an <see cref="EndPointKey"/> that equals this instance; otherwise false.</returns>
        public override bool Equals(object? obj) => obj is EndPointKey endPointKey && Equals(endPointKey);

        /// <summary>
        /// Returns a hash code derived from the endpoint's address and port.
        /// </summary>
        /// <returns>A 32-bit signed integer hash code.</returns>
        public override int GetHashCode() => _endPoint is null ? 0 : unchecked(_endPoint.Address.GetHashCode() * 397) ^ _endPoint.Port;
    }
}