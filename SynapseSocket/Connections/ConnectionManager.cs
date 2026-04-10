using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;

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
    private readonly ConcurrentDictionary<EndPointKey, SynapseConnection> _byEndPoint = new();

    private readonly ConcurrentDictionary<ulong, SynapseConnection> _bySignature = new();

    /// <summary>
    /// Tries to find an existing connection by its remote endpoint.
    /// </summary>
    public bool TryGet(IPEndPoint endPoint, out SynapseConnection? connection)
    {
        return _byEndPoint.TryGetValue(new(endPoint), out connection);
    }

    /// <summary>
    /// Tries to find a connection by signature.
    /// </summary>
    public bool TryGetBySignature(ulong signature, out SynapseConnection? connection)
        => _bySignature.TryGetValue(signature, out connection);

    /// <summary>
    /// Registers a new connection.
    /// Returns the existing one if already present.
    /// </summary>
    public SynapseConnection GetOrAdd(IPEndPoint endPoint, ulong signature, Func<IPEndPoint, ulong, SynapseConnection> factory)
    {
        EndPointKey endPointKey = new(endPoint);
        SynapseConnection synapseConnection = _byEndPoint.GetOrAdd(endPointKey, _ => factory(endPoint, signature));
        _bySignature[signature] = synapseConnection;
        return synapseConnection;
    }

    /// <summary>
    /// Removes and returns a connection by endpoint.
    /// </summary>
    public bool Remove(IPEndPoint endPoint, out SynapseConnection? removedSynapseConnection)
    {
        bool wasRemoved = _byEndPoint.TryRemove(new(endPoint), out removedSynapseConnection);
        if (wasRemoved && removedSynapseConnection is not null) _bySignature.TryRemove(removedSynapseConnection.Signature, out _);
        return wasRemoved;
    }

    /// <summary>
    /// Enumerates all currently tracked connections.
    /// Snapshot is taken at call time.
    /// </summary>
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
        public EndPointKey(IPEndPoint endPoint) { _endPoint = endPoint; }

        public bool Equals(EndPointKey other)
        {
            if (_endPoint is null || other._endPoint is null) return ReferenceEquals(_endPoint, other._endPoint);
            return _endPoint.Port == other._endPoint.Port && _endPoint.Address.Equals(other._endPoint.Address);
        }

        public override bool Equals(object? obj) => obj is EndPointKey endPointKey && Equals(endPointKey);
        public override int GetHashCode() => _endPoint is null ? 0 : unchecked(_endPoint.Address.GetHashCode() * 397) ^ _endPoint.Port;

        private readonly IPEndPoint _endPoint;
    }
}
