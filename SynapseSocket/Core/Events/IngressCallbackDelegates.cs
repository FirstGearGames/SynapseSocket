using System;
using System.Net;
using SynapseSocket.Connections;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Internal callback: a payload has been delivered from the ingress path.
/// </summary>
public delegate void PayloadDeliveredDelegate(SynapseConnection synapseConnection, ArraySegment<byte> payload, bool reliable);

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
/// Internal callback: a NAT rendezvous server has reported the peer's external endpoint.
/// </summary>
public delegate void NatPeerReadyDelegate(IPEndPoint peerEndPoint);

/// <summary>
/// Internal callback: a NAT rendezvous server rejected the registration because the session is full.
/// </summary>
public delegate void NatSessionFullDelegate();
