using System;

namespace SynapseSocket.Packets;

/// <summary>
/// Bit-flag signaling byte used as the first byte of every Synapse packet.
/// Packed into a single byte to minimize wire-data footprint.
/// </summary>
[Flags]
public enum PacketFlags : byte
{
    /// <summary>
    /// No flags set.
    /// </summary>
    None = 0,

    /// <summary>
    /// Reliable channel: ordered, guaranteed delivery.
    /// Header contains a sequence number.
    /// </summary>
    Reliable = 1 << 0,

    /// <summary>
    /// Acknowledgment packet for a previously sent reliable packet.
    /// </summary>
    Ack = 1 << 1,

    /// <summary>
    /// Handshake / connection request or response.
    /// </summary>
    Handshake = 1 << 2,

    /// <summary>
    /// Keep-alive heartbeat.
    /// </summary>
    KeepAlive = 1 << 3,

    /// <summary>
    /// Graceful disconnect notification.
    /// </summary>
    Disconnect = 1 << 4,

    /// <summary>
    /// Packet is a segment of a larger segmented payload.
    /// </summary>
    Segmented = 1 << 5,

    /// <summary>
    /// Contains authentication / signature challenge data.
    /// </summary>
    Auth = 1 << 6,

    /// <summary>
    /// NAT punch probe: when set as the sole flag this packet opens a NAT table mapping without initiating a full handshake.
    /// </summary>
    Extended = 1 << 7
}