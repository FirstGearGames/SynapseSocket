using System;
using System.Buffers.Binary;
using System.Net;

namespace SynapseBeacon.Wire;

/// <summary>
/// Shared wire-format helpers for SynapseBeacon packets.
/// Layout is flat: <c>[BeaconPacketType (1 byte)] [payload]</c> with no framing or sequence fields.
/// </summary>
public static class BeaconWireFormat
{
    /// <summary>
    /// Byte count required to write a beacon session ID on the wire.
    /// Session IDs are encoded as a 4-byte big-endian unsigned integer.
    /// </summary>
    public const int SessionIdBytes = sizeof(uint);

    /// <summary>
    /// Maximum wire size for a peer-endpoint payload: address family (1) + IPv6 address (16) + port (2).
    /// </summary>
    public const int MaxPeerEndPointBytes = 1 + 16 + 2;

    /// <summary>
    /// Bytes of client-chosen nonce carried by a request and echoed by the reply that answers it.
    /// <para>
    /// A beacon reply is accepted on the strength of its source address alone, which is forgeable over UDP. The
    /// nonce is what ties a reply to a request this client actually made: an off-path attacker never sees it, so
    /// it cannot produce a reply that will be accepted.
    /// </para>
    /// </summary>
    public const int NonceBytes = 8;

    /// <summary>
    /// Bytes of the return-routability cookie the server issues in a <see cref="BeaconPacketType.JoinChallenge"/>
    /// and requires back on the join that follows.
    /// <para>
    /// The cookie is a truncated keyed MAC over the joiner's own address, so only a joiner that genuinely receives
    /// at the address it claimed can present one. That is what stops a forged join from pointing a host's
    /// hole-punch burst at a third party, and it costs the server no per-joiner state to check.
    /// </para>
    /// </summary>
    public const int CookieBytes = 8;

    /// <summary>
    /// Wire size of a first-hand <see cref="BeaconPacketType.JoinSession"/>: type, session ID and nonce.
    /// </summary>
    public const int UnprovenJoinBytes = 1 + SessionIdBytes + NonceBytes;

    /// <summary>
    /// Wire size of a <see cref="BeaconPacketType.JoinSession"/> carrying a cookie back.
    /// </summary>
    public const int ProvenJoinBytes = UnprovenJoinBytes + CookieBytes;

    /// <summary>
    /// Writes a type byte followed by a 4-byte big-endian session ID into <paramref name="destination"/>.
    /// Returns the total number of bytes written.
    /// </summary>
    public static int WriteTypeAndSessionId(Span<byte> destination, BeaconPacketType type, uint sessionId)
    {
        destination[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(1, SessionIdBytes), sessionId);
        return 1 + SessionIdBytes;
    }

    /// <summary>
    /// Reads a 4-byte big-endian session ID from the payload slice immediately following the type byte.
    /// Returns false if <paramref name="payload"/> is too short.
    /// </summary>
    public static bool TryReadSessionId(ReadOnlySpan<byte> payload, out uint sessionId)
    {
        if (payload.Length < SessionIdBytes)
        {
            sessionId = 0;
            return false;
        }

        sessionId = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, SessionIdBytes));
        return true;
    }

    /// <summary>
    /// Writes a peer-endpoint payload into <paramref name="destination"/>:
    /// address family (1 byte: 4 or 6), address bytes, then port (2 bytes LE).
    /// Returns the number of bytes written, or 0 if the address could not be serialised.
    /// </summary>
    public static int WritePeerEndPoint(Span<byte> destination, IPEndPoint peer)
    {
        int addressFamilyOffset = 0;
        int offset = 1;

        if (!peer.Address.TryWriteBytes(destination.Slice(offset), out int addressLength))
            return 0;

        destination[addressFamilyOffset] = addressLength == 4 ? (byte)4 : (byte)6;
        offset += addressLength;
        destination[offset++] = (byte)(peer.Port & 0xFF);
        destination[offset++] = (byte)((peer.Port >> 8) & 0xFF);
        return offset;
    }

    /// <summary>
    /// Returns the number of bytes the encoded peer endpoint occupies at the start of <paramref name="body"/>,
    /// or 0 when it is malformed. Used to locate data that follows the endpoint, such as an echoed nonce.
    /// </summary>
    public static int MeasurePeerEndPoint(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1)
            return 0;

        byte addressFamily = body[0];

        if (addressFamily == 4 && body.Length >= 1 + 4 + 2)
            return 1 + 4 + 2;

        if (addressFamily == 6 && body.Length >= 1 + 16 + 2)
            return 1 + 16 + 2;

        return 0;
    }

    /// <summary>
    /// Parses a peer-endpoint payload. Returns <see langword="null"/> when the body is malformed or too short.
    /// </summary>
    public static IPEndPoint? TryReadPeerEndPoint(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1)
            return null;

        byte addressFamily = body[0];
        ReadOnlySpan<byte> rest = body.Slice(1);

        if (addressFamily == 4 && rest.Length >= 6)
        {
            IPAddress ip = new(rest.Slice(0, 4));
            ushort port = (ushort)(rest[4] | (rest[5] << 8));
            return new(ip, port);
        }

        if (addressFamily == 6 && rest.Length >= 18)
        {
            IPAddress ip = new(rest.Slice(0, 16));
            ushort port = (ushort)(rest[16] | (rest[17] << 8));
            return new(ip, port);
        }

        return null;
    }
}
