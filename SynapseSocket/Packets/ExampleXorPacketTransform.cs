using System;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace SynapseSocket.Packets;

/// <summary>
/// A worked example of <see cref="IPacketTransform"/>, and nothing more.
/// <para>
/// <b>This provides no security of any kind and must never be used to protect real traffic.</b> It masks each payload
/// with a four-byte XOR value and then writes that very value into the packet in front of the masked bytes, so anyone
/// who can read the packet can undo the masking with no key, no secret, and no effort. It also authenticates nothing:
/// a tampered or mismatched packet produces garbage rather than an error. Treat it purely as a template for the shape
/// of a transform, then replace it with a real construction (an authenticated cipher such as AES-GCM) before shipping.
/// </para>
/// <para>
/// What it does usefully demonstrate is the length accounting a transform has to get right. The mask makes every
/// payload exactly four bytes longer, <see cref="ReservedBytes"/> declares those four bytes, and the engine deducts
/// them from <see cref="SynapseSocket.Core.Configuration.SynapseConfig.MaximumTransmissionUnit"/> in advance, so a
/// masked datagram can never outgrow the configured MTU.
/// </para>
/// </summary>
/// <remarks>
/// The transformed payload is laid out as the four-byte mask, little endian, followed by the masked bytes, which are
/// exactly as long as the originals. A fresh mask is drawn per packet so identical payloads do not produce identical
/// datagrams, which is worth seeing on the wire; it buys no secrecy, because the mask ships beside the data it masks.
/// Both peers need only to be running this same transform, since there is no key to agree on.
/// </remarks>
public sealed class ExampleXorPacketTransform : IPacketTransform
{
    /// <inheritdoc/>
    public uint ReservedBytes => MaskSize;
    /// <summary>
    /// Size in bytes of the mask written ahead of every masked payload.
    /// </summary>
    private const int MaskSize = 4;

    /// <inheritdoc/>
    public bool TryTransform(PacketTransformDirection packetTransformDirection, PacketType packetType, IPEndPoint endPoint, ReadOnlySpan<byte> source, Span<byte> destination, out int writtenLength)
    {
        if (packetTransformDirection == PacketTransformDirection.Outbound)
        {
            Span<byte> mask = destination[..MaskSize];
            RandomNumberGenerator.Fill(mask);

            ApplyMask(source, destination.Slice(MaskSize, source.Length), BinaryPrimitives.ReadUInt32LittleEndian(mask));

            writtenLength = MaskSize + source.Length;

            return true;
        }

        writtenLength = 0;

        // Rejecting here is the only failure this example can detect. A real transform rejects on a failed integrity
        // check as well, which is what turns a tampered packet into a dropped one instead of a corrupt delivery.
        if (source.Length < MaskSize)
            return false;

        uint receivedMask = BinaryPrimitives.ReadUInt32LittleEndian(source);
        ReadOnlySpan<byte> maskedPayload = source[MaskSize..];

        ApplyMask(maskedPayload, destination[..maskedPayload.Length], receivedMask);

        writtenLength = maskedPayload.Length;

        return true;
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/>, XORing each byte against the matching
    /// byte of <paramref name="mask"/>. Masking and unmasking are the same operation, so both directions call this.
    /// </summary>
    /// <param name="source">The bytes to mask.</param>
    /// <param name="destination">The buffer to write the masked bytes into, exactly as long as <paramref name="source"/>.</param>
    /// <param name="mask">The four-byte mask, applied repeatedly across the payload.</param>
    private static void ApplyMask(ReadOnlySpan<byte> source, Span<byte> destination, uint mask)
    {
        for (int i = 0; i < source.Length; i++)
            destination[i] = (byte)(source[i] ^ (byte)(mask >> ((i & 3) * 8)));
    }
}
