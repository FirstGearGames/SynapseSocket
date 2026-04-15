using System;
using System.Net;
using SynapseSocket.Connections;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Internal callback: a payload has been delivered from the ingress path.
/// </summary>
public delegate void PayloadDeliveredDelegate(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable);

/// <summary>
/// Internal callback: a connection was established or closed on the ingress path.
/// </summary>
public delegate void ConnectionDelegate(SynapseConnection synapseConnection);

/// <summary>
/// Internal callback: a connection attempt was rejected before it could be established.
/// </summary>
public delegate void ConnectionFailedCallbackDelegate(IPEndPoint? endPoint, ConnectionRejectedReason connectionRejectedReason, string? message);

/// <summary>
/// Internal callback: a violation was detected on the ingress path.
/// </summary>
public delegate void ViolationCallbackDelegate(IPEndPoint endPoint, ulong signature, ViolationReason violationReason, int packetSize, string? details, ViolationAction initialViolationAction);

/// <summary>
/// Raised when the ingress path receives a datagram whose first byte is not a recognised
/// Synapse <see cref="SynapseSocket.Packets.PacketType"/> value. Allows external protocols
/// (e.g. a rendezvous/beacon client) to piggyback on the Synapse UDP socket so that the NAT
/// mapping opened by talking to the external service is the same mapping used for P2P traffic.
/// <para>
/// The <paramref name="packet"/> segment references the internal receive buffer and is only
/// valid for the duration of the callback. Copy any bytes the handler needs to retain.
/// </para>
/// </summary>
/// <param name="fromEndPoint">The source endpoint of the datagram.</param>
/// <param name="packet">The full raw packet bytes, including the leading type byte.</param>
public delegate void UnknownPacketReceivedDelegate(IPEndPoint fromEndPoint, ArraySegment<byte> packet);
