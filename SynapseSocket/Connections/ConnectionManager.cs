using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using SynapseSocket.Core;
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
    public int Count => _connectionsByEndPoint.Count;
    /// <summary>
    /// Raised when two connections produce the same 64-bit signature (birthday-bound collision).
    /// The newer connection wins the reverse-lookup slot. Subscribe for telemetry; no corrective action is taken automatically.
    /// </summary>
    public event SignatureCollisionHandler? SignatureCollisionDetected;
    /// <summary>
    /// Maps a connection's IPEndPoint to its <see cref="SynapseConnection"/>.
    /// </summary>
    public IReadOnlyDictionary<IPEndPoint, SynapseConnection> ConnectionsByEndPoint => _connectionsByEndPoint;
    private readonly ConcurrentDictionary<IPEndPoint, SynapseConnection> _connectionsByEndPoint = new(IPEndPointComparer.Default);
#if NET8_0_OR_GREATER
    /// <summary>
    /// Maps a connection's serialized <see cref="SocketAddress"/> to its <see cref="SynapseConnection"/>, so the
    /// allocation-free receive path resolves the sender without materializing an <see cref="IPEndPoint"/> per datagram.
    /// SocketAddress equality and hashing read the address buffer, so the reusable receive instance works as a lookup key.
    /// </summary>
    private readonly Dictionary<SocketAddress, SynapseConnection> _connectionsBySocketAddress = [];
#else
    /// <summary>
    /// Maps a hash of a connection's raw address bytes and port to its <see cref="SynapseConnection"/>, so a
    /// native receive can resolve an established peer straight from the <c>sockaddr</c> the kernel filled, with no
    /// managed endpoint materialised. The netstandard2.1 counterpart to the SocketAddress table, which that runtime
    /// has no ReceiveFrom overload for. Only the native receive path reads it, so the modern build does not build it.
    /// </summary>
    private readonly Dictionary<ulong, SynapseConnection> _connectionsByAddressKey = [];
#endif
    /// <summary>
    /// Maps a connection's 64-bit signature to its <see cref="SynapseConnection"/>.
    /// </summary>
    public IReadOnlyDictionary<ulong, SynapseConnection> ConnectionsBySignature => _connectionsBySignature;
    private readonly ConcurrentDictionary<ulong, SynapseConnection> _connectionsBySignature = [];
    /// <summary>
    /// Connections as an index-based collection.
    /// </summary>
    public IReadOnlyList<SynapseConnection> Connections => _connections;
    private readonly List<SynapseConnection> _connections = [];

    /// <summary>
    /// Registers a new connection.
    /// Returns the existing one if already present.
    /// </summary>
    /// <param name="endPoint">The remote endpoint identifying the peer.</param>
    /// <param name="signature">The 64-bit signature associated with the peer.</param>
    /// <param name="isFound">Receives true when a connection already existed for <paramref name="endPoint"/>, false when one was created.</param>
    /// <returns>The existing connection for the endpoint, or the newly created one.</returns>
    public SynapseConnection GetOrAdd(IPEndPoint endPoint, ulong signature, out bool isFound)
    {
        isFound = _connectionsByEndPoint.TryGetValue(endPoint, out SynapseConnection? synapseConnection);

        if (!isFound)
        {
            // Allocated, not rented: connection objects are never pooled. See SynapseManager.ReleaseTornDownConnections.
            synapseConnection = new();

            int connectionsIndex = _connections.Count;
            synapseConnection.Initialize(endPoint, signature, connectionsIndex);

            _connectionsByEndPoint[endPoint] = synapseConnection;
#if NET8_0_OR_GREATER
            _connectionsBySocketAddress[synapseConnection.RemoteSocketAddress] = synapseConnection;
#else
            _connectionsByAddressKey[Transport.NativeSocket.ComputeAddressKey(endPoint)] = synapseConnection!;
#endif
            _connections.Add(synapseConnection);
        }

        if (!_connectionsBySignature.TryAdd(signature, synapseConnection!))
        {
            // Two distinct endpoints produced the same 64-bit signature.
            // Overwrite so reverse lookup stays current, but surface the collision.
            _connectionsBySignature[signature] = synapseConnection!;
            SignatureCollisionDetected?.Invoke(signature);
        }

        return synapseConnection!;
    }

    /// <summary>
    /// Always creates a fresh <see cref="SynapseConnection"/> for <paramref name="endPoint"/>,
    /// removing any existing entry first. Used by the outbound connect path to guarantee a clean
    /// connection object on every call regardless of prior session state.
    /// </summary>
    /// <param name="endPoint">The remote endpoint to connect to.</param>
    /// <param name="signature">The 64-bit signature associated with the peer.</param>
    /// <returns>The newly created <see cref="SynapseConnection"/>.</returns>
    public SynapseConnection CreateNew(IPEndPoint endPoint, ulong signature)
    {
        if (_connectionsByEndPoint.TryRemove(endPoint, out SynapseConnection? old))
        {
#if NET8_0_OR_GREATER
            _connectionsBySocketAddress.Remove(old.RemoteSocketAddress);
#else
            _connectionsByAddressKey.Remove(Transport.NativeSocket.ComputeAddressKey(endPoint));
#endif
            RemoveFromConnections(old);

            _connectionsBySignature.TryRemove(old.Signature, out _);
        }

        // Allocated, not rented: connection objects are never pooled. See SynapseManager.ReleaseTornDownConnections.
        SynapseConnection synapseConnection = new();
        int connectionsIndex = _connections.Count;
        synapseConnection.Initialize(endPoint, signature, connectionsIndex);

        _connectionsByEndPoint[endPoint] = synapseConnection;
#if NET8_0_OR_GREATER
        _connectionsBySocketAddress[synapseConnection.RemoteSocketAddress] = synapseConnection;
#else
        _connectionsByAddressKey[Transport.NativeSocket.ComputeAddressKey(endPoint)] = synapseConnection;
#endif
        _connections.Add(synapseConnection);

        if (!_connectionsBySignature.TryAdd(signature, synapseConnection))
            _connectionsBySignature[signature] = synapseConnection;

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
        bool isRemoved = _connectionsByEndPoint.TryRemove(endPoint, out removedSynapseConnection);

        if (isRemoved && removedSynapseConnection is not null)
        {
#if NET8_0_OR_GREATER
            _connectionsBySocketAddress.Remove(removedSynapseConnection.RemoteSocketAddress);
#else
            _connectionsByAddressKey.Remove(Transport.NativeSocket.ComputeAddressKey(endPoint));
#endif
            _connectionsBySignature.TryRemove(removedSynapseConnection.Signature, out _);

            RemoveFromConnections(removedSynapseConnection);
        }

        return isRemoved;
    }

    /// <summary>
    /// Removes all connections from every lookup table. Called on engine shutdown after the connections' pooled
    /// buffers have been reclaimed.
    /// </summary>
    public void Clear()
    {
        _connectionsByEndPoint.Clear();
#if NET8_0_OR_GREATER
        _connectionsBySocketAddress.Clear();
#else
        _connectionsByAddressKey.Clear();
#endif
        _connectionsBySignature.Clear();
        _connections.Clear();
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Resolves a connection from a serialized sender address, without materializing an <see cref="IPEndPoint"/>.
    /// </summary>
    /// <param name="socketAddress">The sender's address, typically the receive path's reusable instance.</param>
    /// <param name="synapseConnection">When this method returns true, the resolved connection.</param>
    /// <returns>True when a connection is registered for the address.</returns>
    public bool TryGetBySocketAddress(SocketAddress socketAddress, out SynapseConnection? synapseConnection) => _connectionsBySocketAddress.TryGetValue(socketAddress, out synapseConnection);
#endif

    /// <summary>
    /// Unlinks a connection from the dense connections list with a swap-remove, keeping every surviving entry's <see cref="SynapseConnection.ConnectionsIndex"/> equal to its own slot and clearing the removed connection's.
    /// </summary>
    /// <param name="synapseConnection">The connection to unlink.</param>
    /// <remarks>
    /// Stated once rather than at each removal site, because both statements of it were wrong the same way: each moved the last entry into the freed slot and then removed the FREED slot, which deletes the entry just moved in and shifts the rest down, so the contents survive while every survivor's recorded index does not. The next removal then reads a stale index and evicts a live connection from the list or indexes past its end. Removing the tail is what makes the recorded indices true.
    /// </remarks>
    private void RemoveFromConnections(SynapseConnection synapseConnection)
    {
        int connectionsIndex = synapseConnection.ConnectionsIndex;

        if (connectionsIndex is SynapseConnection.UnsetConnectionsIndex)
            return;

        int lastConnectionsIndex = _connections.Count - 1;

        if (connectionsIndex < lastConnectionsIndex)
        {
            SynapseConnection movedSynapseConnection = _connections[lastConnectionsIndex];
            movedSynapseConnection.ConnectionsIndex = connectionsIndex;
            _connections[connectionsIndex] = movedSynapseConnection;
        }

        _connections.RemoveAt(lastConnectionsIndex);

        // Cleared unconditionally: a tail removal left the outgoing connection carrying a live-looking index, and nothing resets it.
        synapseConnection.ConnectionsIndex = SynapseConnection.UnsetConnectionsIndex;
    }

#if !NET8_0_OR_GREATER
    /// <summary>
    /// Resolves a connection from the hash of a raw sender address, without materialising an endpoint.
    /// Only the netstandard2.1 native receive path resolves senders this way.
    /// </summary>
    /// <param name="addressKey">Key produced by <see cref="Transport.NativeSocket.ComputeAddressKey(byte[], int)"/>.</param>
    /// <param name="synapseConnection">When this returns true, the resolved connection.</param>
    /// <returns>True when a connection is registered for the address.</returns>
    internal bool TryGetByAddressKey(ulong addressKey, out SynapseConnection? synapseConnection) => _connectionsByAddressKey.TryGetValue(addressKey, out synapseConnection);
#endif
}
