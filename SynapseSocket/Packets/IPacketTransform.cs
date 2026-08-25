using System;
using System.Net;

namespace SynapseSocket.Packets;

/// <summary>
/// Optional interface for rewriting the bytes of every Synapse packet as it enters or leaves the socket.
/// Supply an implementation on <see cref="SynapseSocket.Core.Configuration.SynapseConfig.PacketTransform"/> to layer encryption, compression, obfuscation,
/// or a custom integrity check underneath the engine without changing any send or receive call site.
/// <para>
/// A transform only ever sees the payload region of a packet. The Synapse header is copied through verbatim by the
/// engine and is never exposed, so the receiving side can still identify a packet before reversing the transform, and
/// external protocols that piggyback on the socket through <see cref="SynapseSocket.Core.SynapseManager.SendRaw"/> and
/// <see cref="SynapseSocket.Core.SynapseManager.UnknownPacketReceived"/> keep flowing untouched.
/// </para>
/// <para>
/// Both peers must run an equivalent transform. A datagram whose payload the receiving transform rejects is discarded
/// and raises a <see cref="SynapseSocket.Core.Events.ViolationReason.TransformRejected"/> violation.
/// </para>
/// </summary>
/// <remarks>
/// The engine is single-threaded and driven by the host's poll, so implementations do not need to be thread-safe.
/// <see cref="TryTransform"/> runs once per datagram in each direction, which makes it the hottest user-supplied
/// callback in the engine; keep it allocation-free.
/// </remarks>
public interface IPacketTransform
{
    /// <summary>
    /// The largest number of bytes this transform may add to an outbound payload.
    /// The engine subtracts this from <see cref="SynapseSocket.Core.Configuration.SynapseConfig.MaximumTransmissionUnit"/> to produce the effective MTU it
    /// packs packets against, which guarantees a transformed datagram still fits the configured MTU on the wire.
    /// The reduced value is what <see cref="SynapseSocket.Core.SynapseManager.MaximumTransmissionUnit"/> and
    /// <see cref="SynapseSocket.Core.SynapseManager.MaximumPayloadSize"/> report.
    /// </summary>
    uint ReservedBytes { get; }

    /// <summary>
    /// Rewrites one packet payload in the given direction.
    /// </summary>
    /// <param name="packetTransformDirection">Whether the packet is on its way to the socket or has just arrived from it.</param>
    /// <param name="packetType">
    /// The type of the packet the payload belongs to, read from the header the engine preserves.
    /// A transform keyed on a session secret should pass <see cref="PacketType.Handshake"/>,
    /// <see cref="PacketType.NatProbe"/>, and <see cref="PacketType.NatChallenge"/> through unchanged, because those
    /// packets travel before a session exists.
    /// </param>
    /// <param name="endPoint">The remote peer the packet is addressed to, or was received from.</param>
    /// <param name="source">The payload bytes to read, excluding the Synapse header. May be empty for control packets.</param>
    /// <param name="destination">
    /// The buffer to write the rewritten payload into, always at least <paramref name="source"/> length plus
    /// <see cref="ReservedBytes"/> bytes long.
    /// </param>
    /// <param name="writtenLength">The number of bytes written to <paramref name="destination"/>.</param>
    /// <returns>
    /// True when the payload was rewritten and <paramref name="writtenLength"/> is valid.
    /// False to discard the packet, which is how an inbound transform reports a failed integrity or authentication check.
    /// </returns>
    bool TryTransform(PacketTransformDirection packetTransformDirection, PacketType packetType, IPEndPoint endPoint, ReadOnlySpan<byte> source, Span<byte> destination, out int writtenLength);
}
