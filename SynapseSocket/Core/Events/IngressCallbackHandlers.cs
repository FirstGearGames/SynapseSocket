using System;
using System.Net;
using SynapseSocket.Connections;
using SynapseSocket.Security;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Fired by the ingress path when a payload has been delivered to a connection.
/// </summary>
/// <param name="synapseConnection">The connection the payload arrived on.</param>
/// <param name="payload">The complete payload, valid only for the duration of the callback.</param>
/// <param name="isReliable">True when the payload was sent reliably.</param>
/// <param name="isPayloadRented">
/// True when <paramref name="payload"/> is backed by a buffer the ingress path rented from
/// <see cref="System.Buffers.ArrayPool{T}"/> for this delivery alone. Ownership transfers to the subscriber, which
/// must return the buffer once the delivery completes. False when the buffer belongs to the ingress engine itself
/// (the zero-copy unreliable fast path under
/// <see cref="SynapseSocket.Core.Configuration.SynapseConfig.CopyReceivedPayloads"/>), in which case the subscriber
/// must not return it: the engine keeps reading and writing it for every later datagram and returns it at shutdown.
/// </param>
internal delegate void PayloadDeliveredHandler(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool isReliable, bool isPayloadRented);

/// <summary>
/// Fired by the ingress path when a connection is established or closed.
/// </summary>
internal delegate void ConnectionHandler(SynapseConnection synapseConnection);

/// <summary>
/// Fired by the ingress path when a connection attempt is rejected before it could be established.
/// </summary>
internal delegate void ConnectionFailedCallbackHandler(IPEndPoint? endPoint, ConnectionRejectedReason connectionRejectedReason, string? message);

/// <summary>
/// Fired by the ingress path when a violation is detected.
/// </summary>
internal delegate void ViolationCallbackHandler(IPEndPoint endPoint, ulong signature, ViolationReason violationReason, int packetSize, string? details, ViolationAction initialViolationAction);

/// <summary>
/// Raised when the ingress path receives a datagram whose first byte is not a recognised
/// Synapse <see cref="SynapseSocket.Packets.PacketType"/> value, and
/// <see cref="SynapseSocket.Core.Configuration.SecurityConfig.AllowUnknownPackets"/> is true.
/// Allows external protocols (e.g. a rendezvous/beacon client) to piggyback on the Synapse UDP
/// socket so that the NAT mapping opened by talking to the external service is the same mapping
/// used for P2P traffic.
/// <para>
/// The handler must return <see cref="FilterResult.Allowed"/> to accept the packet.
/// Any other value is treated as a rejection and raises a
/// <see cref="SynapseSocket.Core.Events.ViolationReason.UnknownPacket"/> violation.
/// When multiple subscribers are attached, the last subscriber's return value is used.
/// </para>
/// <para>
/// The <paramref name="packet"/> segment references the internal receive buffer and is only
/// valid for the duration of the callback. Copy any bytes the handler needs to retain.
/// </para>
/// </summary>
/// <param name="fromEndPoint">The source endpoint of the datagram.</param>
/// <param name="packet">The full raw packet bytes, including the leading type byte.</param>
/// <returns>
/// <see cref="FilterResult.Allowed"/> to accept the packet; any other value to reject it and
/// raise a violation.
/// </returns>
public delegate FilterResult UnknownPacketReceivedHandler(IPEndPoint fromEndPoint, ArraySegment<byte> packet);
