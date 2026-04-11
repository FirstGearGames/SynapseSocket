# NAT Probe Challenge-Response Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `PacketFlags`/`NatPacketType` with a clean `PacketType` enum and add stateless HMAC challenge-response to NAT probe handling to eliminate the amplification vector.

**Architecture:** `PacketType` is a plain `byte` enum with one value per packet type — no flag combining. `PacketHeader` switches on the value. `IngressEngine.ProcessPacket` uses two switch blocks (pre- and post-connection). The NAT probe path computes a server-secret HMAC token and only sends a handshake after the peer echoes it back correctly.

**Tech Stack:** C# / .NET Standard 2.1, `System.Security.Cryptography.HMACSHA256`

---

### Task 1: Create `PacketType.cs`

**Files:**
- Create: `SynapseSocket/Packets/PacketType.cs`

- [ ] **Create the file**

```csharp
namespace SynapseSocket.Packets;

/// <summary>
/// Identifies the type of a Synapse wire packet.
/// Encoded as the first byte of every packet header.
/// Optional header fields follow based on type:
/// <list type="bullet">
/// <item><see cref="Reliable"/>, <see cref="Ack"/>, <see cref="ReliableSegmented"/> — sequence number (2 bytes LE).</item>
/// <item><see cref="Segmented"/> — segment ID (2 bytes LE), segment index (1 byte), segment count (1 byte).</item>
/// <item><see cref="ReliableSegmented"/> — sequence number, then segment fields.</item>
/// </list>
/// All other types carry no additional header fields; any further bytes are payload.
/// </summary>
public enum PacketType : byte
{
    /// <summary>Unreliable, unsegmented data payload.</summary>
    None = 0,

    /// <summary>Reliable, unsegmented data payload. Header includes a sequence number.</summary>
    Reliable = 1,

    /// <summary>Acknowledgment for a reliable packet. Header includes the acknowledged sequence number.</summary>
    Ack = 2,

    /// <summary>Handshake or handshake acknowledgment. Payload contains a 4-byte nonce.</summary>
    Handshake = 3,

    /// <summary>Keep-alive heartbeat. No payload.</summary>
    KeepAlive = 4,

    /// <summary>Graceful disconnect notification. No payload.</summary>
    Disconnect = 5,

    /// <summary>Unreliable, segmented data payload. Header includes segment fields.</summary>
    Segmented = 6,

    /// <summary>Reliable, segmented data payload. Header includes a sequence number then segment fields.</summary>
    ReliableSegmented = 7,

    /// <summary>NAT punch probe. Sent to open a NAT table mapping. No payload.</summary>
    NatProbe = 8,

    /// <summary>NAT challenge or challenge echo. Payload is an 8-byte HMAC token.</summary>
    NatChallenge = 9,

    /// <summary>Registers with a NAT rendezvous server. Payload is the ASCII session ID.</summary>
    NatRegister = 10,

    /// <summary>Keep-alive heartbeat to a NAT rendezvous server. Payload is the ASCII session ID.</summary>
    NatHeartbeat = 11,

    /// <summary>Rendezvous server acknowledges a heartbeat. No payload.</summary>
    NatHeartbeatAck = 12,

    /// <summary>Rendezvous server reports the peer's external endpoint. Payload: address family byte + IP bytes + port (2 bytes LE).</summary>
    NatPeerReady = 13,

    /// <summary>Rendezvous server rejects a registration because the session is full. No payload.</summary>
    NatSessionFull = 14,
}
```

- [ ] **Commit**
```
git add SynapseSocket/Packets/PacketType.cs
git commit -m "Add PacketType enum replacing PacketFlags and NatPacketType"
```

---

### Task 2: Update `PacketHeader.cs`

**Files:**
- Modify: `SynapseSocket/Packets/PacketHeader.cs`

- [ ] **Replace the entire file**

```csharp
using System;
using System.Runtime.CompilerServices;

namespace SynapseSocket.Packets;

/// <summary>
/// Wire-format header helpers for Synapse packets.
/// Layout:
///   [0]      : PacketType (1 byte)
///   [1..2]   : Sequence number (UInt16, little-endian) — only for <see cref="PacketType.Reliable"/>, <see cref="PacketType.Ack"/>, <see cref="PacketType.ReliableSegmented"/>
///   [3..4]   : Segment Id (UInt16) — only for <see cref="PacketType.Segmented"/> and <see cref="PacketType.ReliableSegmented"/>
///   [5]      : Segment Index (Byte) — only for <see cref="PacketType.Segmented"/> and <see cref="PacketType.ReliableSegmented"/>
///   [6]      : Segment Count (Byte) — only for <see cref="PacketType.Segmented"/> and <see cref="PacketType.ReliableSegmented"/>
///   [...]    : Payload
/// Explicit little-endian ordering is used for cross-platform consistency.
/// </summary>
public static class PacketHeader
{
    /// <summary>
    /// Size in bytes of the mandatory type field.
    /// </summary>
    public const int TypeSize = 1;

    /// <summary>
    /// Size in bytes of the reliable sequence field when present.
    /// </summary>
    public const int SequenceSize = 2;

    /// <summary>
    /// Size in bytes of the segmentation fields when present.
    /// </summary>
    public const int SegmentSize = 4;

    /// <summary>
    /// Maximum theoretical header size (all optional fields present).
    /// </summary>
    public const int MaxHeaderSize = TypeSize + SequenceSize + SegmentSize;

    /// <summary>
    /// Computes the header size for a given packet type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeHeaderSize(PacketType type) => type switch
    {
        PacketType.Reliable          => TypeSize + SequenceSize,
        PacketType.Ack               => TypeSize + SequenceSize,
        PacketType.Segmented         => TypeSize + SegmentSize,
        PacketType.ReliableSegmented => TypeSize + SequenceSize + SegmentSize,
        _                            => TypeSize
    };

    /// <summary>
    /// Writes a header into the supplied buffer starting at offset 0.
    /// Returns the number of bytes written.
    /// </summary>
    public static int Write(Span<byte> buffer, PacketType type, ushort sequence, ushort segmentId, byte segmentIndex, byte segmentCount)
    {
        int offset = 0;
        buffer[offset++] = (byte)type;

        if (type == PacketType.Reliable || type == PacketType.Ack || type == PacketType.ReliableSegmented)
        {
            buffer[offset++] = (byte)(sequence & 0xFF);
            buffer[offset++] = (byte)((sequence >> 8) & 0xFF);
        }

        if (type == PacketType.Segmented || type == PacketType.ReliableSegmented)
        {
            buffer[offset++] = (byte)(segmentId & 0xFF);
            buffer[offset++] = (byte)((segmentId >> 8) & 0xFF);
            buffer[offset++] = segmentIndex;
            buffer[offset++] = segmentCount;
        }

        return offset;
    }

    /// <summary>
    /// Writes a header followed by <paramref name="payload"/> into <paramref name="destination"/>.
    /// Returns the total number of bytes written (header + payload).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildPacket(Span<byte> destination, PacketType type, ushort sequence, ushort segmentId, byte segmentIndex, byte segmentCount, ReadOnlySpan<byte> payload)
    {
        int headerSize = Write(destination, type, sequence, segmentId, segmentIndex, segmentCount);
        payload.CopyTo(destination[headerSize..]);
        return headerSize + payload.Length;
    }

    /// <summary>
    /// Reads a header from the supplied buffer. Returns the number of bytes consumed.
    /// Throws if the buffer is too small for the declared type.
    /// </summary>
    public static int Read(ReadOnlySpan<byte> buffer, out PacketType type, out ushort sequence, out ushort segmentId, out byte segmentIndex, out byte segmentCount)
    {
        if (buffer.Length < TypeSize) throw new ArgumentException("Buffer too small for header.");

        int offset = 0;
        type = (PacketType)buffer[offset++];
        sequence = 0;
        segmentId = 0;
        segmentIndex = 0;
        segmentCount = 0;

        if (type == PacketType.Reliable || type == PacketType.Ack || type == PacketType.ReliableSegmented)
        {
            if (buffer.Length < offset + SequenceSize) throw new ArgumentException("Buffer too small for sequence.");
            sequence = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            offset += 2;
        }

        if (type == PacketType.Segmented || type == PacketType.ReliableSegmented)
        {
            if (buffer.Length < offset + SegmentSize) throw new ArgumentException("Buffer too small for segment info.");
            segmentId = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            segmentIndex = buffer[offset + 2];
            segmentCount = buffer[offset + 3];
            offset += 4;
        }

        return offset;
    }
}
```

- [ ] **Commit**
```
git add SynapseSocket/Packets/PacketHeader.cs
git commit -m "Update PacketHeader to use PacketType with switch-based header logic"
```

---

### Task 3: Update `PacketSplitter.cs`

**Files:**
- Modify: `SynapseSocket/Packets/PacketSplitter.cs`

- [ ] **Replace the `Split` method body** (lines 32–65)

```csharp
public ArraySegment<byte>[] Split(ReadOnlySpan<byte> payload, bool isReliable, out int segmentCount, ushort sequence = 0)
{
    PacketType type = isReliable ? PacketType.ReliableSegmented : PacketType.Segmented;
    int segmentPayloadSize = (int)MaximumTransmissionUnit - PacketHeader.ComputeHeaderSize(type);

    if (segmentPayloadSize <= 0)
        throw new InvalidOperationException("MTU too small for segmentation headers.");

    int totalSegments = (payload.Length + segmentPayloadSize - 1) / segmentPayloadSize;

    if (totalSegments > (int)MaximumSegments)
        throw new InvalidOperationException("Payload requires " + totalSegments + " segments but limit is " + MaximumSegments + ".");

    segmentCount = totalSegments;
    ushort segmentId = (ushort)Interlocked.Increment(ref _segmentIdCounter);
    int headerSize = PacketHeader.ComputeHeaderSize(type);

    int totalBufferSize = totalSegments * headerSize + payload.Length;
    byte[] backingBuffer = ArrayPool<byte>.Shared.Rent(totalBufferSize);
    ArraySegment<byte>[] segments = ArrayPool<ArraySegment<byte>>.Shared.Rent(totalSegments);

    int bufferOffset = 0;

    for (int i = 0; i < totalSegments; i++)
    {
        int segmentStartOffset = i * segmentPayloadSize;
        int segmentLength = Math.Min(segmentPayloadSize, payload.Length - segmentStartOffset);
        int written = PacketHeader.BuildPacket(backingBuffer.AsSpan(bufferOffset), type, sequence, segmentId, (byte)i, (byte)totalSegments, payload.Slice(segmentStartOffset, segmentLength));
        segments[i] = new(backingBuffer, bufferOffset, written);
        bufferOffset += written;
    }

    return segments;
}
```

- [ ] **Commit**
```
git add SynapseSocket/Packets/PacketSplitter.cs
git commit -m "Update PacketSplitter to use PacketType.ReliableSegmented"
```

---

### Task 4: Update `TransmissionEngine.cs`

**Files:**
- Modify: `SynapseSocket/Transport/TransmissionEngine.cs`

- [ ] **Replace all `PacketFlags` usages** — update the six send methods

`SendUnreliableUnsegmentedAsync` (line 78):
```csharp
internal async Task SendUnreliableUnsegmentedAsync(SynapseConnection synapseConnection, ArraySegment<byte> payload, CancellationToken cancellationToken)
{
    const PacketType Type = PacketType.None;
    int totalLength = PacketHeader.ComputeHeaderSize(Type) + payload.Count;
    byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
    try
    {
        int written = PacketHeader.BuildPacket(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0, payload.AsSpan());
        await SendRawAsync(new(rentedBuffer, 0, written), synapseConnection.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: false);
    }
}
```

`SendReliableUnsegmentedAsync` (line 102):
```csharp
internal async Task SendReliableUnsegmentedAsync(SynapseConnection synapseConnection, ArraySegment<byte> payload, CancellationToken cancellationToken)
{
    if (synapseConnection.PendingReliableQueue.Count >= _config.Reliable.MaximumPending)
        throw new InvalidOperationException("Reliable backpressure limit reached.");

    ushort sequence;

    lock (synapseConnection.ReliableLock)
        sequence = synapseConnection.NextOutgoingSequence++;

    const PacketType Type = PacketType.Reliable;
    int totalLength = PacketHeader.ComputeHeaderSize(Type) + payload.Count;

    byte[] packetBuffer = new byte[totalLength];
    int written = PacketHeader.BuildPacket(packetBuffer.AsSpan(), Type, sequence, 0, 0, 0, payload.AsSpan());

    SynapseConnection.PendingReliable pendingReliable = new()
    {
        Sequence = sequence,
        Payload = packetBuffer,
        PacketLength = written,
        SentTicks = DateTime.UtcNow.Ticks,
        Retries = 0
    };
    synapseConnection.PendingReliableQueue[sequence] = pendingReliable;

    await SendRawAsync(new(packetBuffer, 0, written), synapseConnection.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
}
```

`SendAckAsync` (line 198):
```csharp
public Task SendAckAsync(SynapseConnection synapseConnection, ushort sequence, CancellationToken cancellationToken)
{
    const PacketType Type = PacketType.Ack;
    int headerSize = PacketHeader.ComputeHeaderSize(Type);
    byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
    PacketHeader.Write(rentedBuffer.AsSpan(), Type, sequence, 0, 0, 0);
    return SendAndPoolBufferAsync(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint, cancellationToken);
}
```

`SendHandshakeAsync` (line 213):
```csharp
public Task SendHandshakeAsync(IPEndPoint target, CancellationToken cancellationToken)
{
    const PacketType Type = PacketType.Handshake;
    const int NonceSize = 4;
    int headerSize = PacketHeader.ComputeHeaderSize(Type);
    int totalSize = headerSize + NonceSize;
    byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
    PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
    RandomNumberGenerator.Fill(rentedBuffer.AsSpan(headerSize, NonceSize));
    return SendAndPoolBufferAsync(new(rentedBuffer, 0, totalSize), target, cancellationToken);
}
```

`SendKeepAliveAsync` (line 231):
```csharp
public Task SendKeepAliveAsync(SynapseConnection synapseConnection, CancellationToken cancellationToken)
{
    const PacketType Type = PacketType.KeepAlive;
    int headerSize = PacketHeader.ComputeHeaderSize(Type);
    byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
    PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
    return SendAndPoolBufferAsync(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint, cancellationToken);
}
```

`SendDisconnectAsync` (line 246):
```csharp
public Task SendDisconnectAsync(SynapseConnection synapseConnection, CancellationToken cancellationToken)
{
    const PacketType Type = PacketType.Disconnect;
    int headerSize = PacketHeader.ComputeHeaderSize(Type);
    byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
    PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
    return SendAndPoolBufferAsync(new(rentedBuffer, 0, headerSize), synapseConnection.RemoteEndPoint, cancellationToken);
}
```

- [ ] **Commit**
```
git add SynapseSocket/Transport/TransmissionEngine.cs
git commit -m "Update TransmissionEngine send methods to use PacketType"
```

---

### Task 5: Update `TransmissionEngine.Nat.cs`

**Files:**
- Modify: `SynapseSocket/Transport/TransmissionEngine.Nat.cs`

- [ ] **Replace the entire file**

```csharp
using System;
using System.Buffers;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Packets;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Transport;

/// <summary>
/// Transmission Engine (Sender).
/// Manages outgoing packet flow for both the unreliable and reliable channels.
/// Immediate processing (no batching) per the spec's no-batching policy.
/// </summary>
public sealed partial class TransmissionEngine
{
    /// <summary>
    /// Sends a NAT registration packet to the rendezvous server for the given session.
    /// </summary>
    public Task SendNatRegisterAsync(IPEndPoint target, string sessionId, CancellationToken cancellationToken)
    {
        return SendNatServerPacketAsync(target, PacketType.NatRegister, sessionId, cancellationToken);
    }

    /// <summary>
    /// Sends a NAT heartbeat packet to the rendezvous server to keep the session alive.
    /// </summary>
    public Task SendNatHeartbeatAsync(IPEndPoint target, string sessionId, CancellationToken cancellationToken)
    {
        return SendNatServerPacketAsync(target, PacketType.NatHeartbeat, sessionId, cancellationToken);
    }

    /// <summary>
    /// Builds and sends a NAT server packet with the session identifier encoded as a fixed-length ASCII payload.
    /// </summary>
    private Task SendNatServerPacketAsync(IPEndPoint target, PacketType packetType, string sessionId, CancellationToken cancellationToken)
    {
        const int SessionIdBytes = ServerNatConfig.SessionIdLength;
        int headerSize = PacketHeader.ComputeHeaderSize(packetType);
        int totalSize = headerSize + SessionIdBytes;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        int offset = PacketHeader.Write(rentedBuffer.AsSpan(), packetType, 0, 0, 0, 0);
        System.Text.Encoding.ASCII.GetBytes(sessionId, 0, SessionIdBytes, rentedBuffer, offset);
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, totalSize), target, cancellationToken);
    }

    /// <summary>
    /// Sends a minimal NAT probe (no payload) to open a NAT mapping on the remote side.
    /// </summary>
    public Task SendNatProbeAsync(IPEndPoint target, CancellationToken cancellationToken)
    {
        const PacketType Type = PacketType.NatProbe;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, headerSize), target, cancellationToken);
    }

    /// <summary>
    /// Sends a NAT challenge or challenge echo containing the provided token.
    /// Used both when issuing a challenge (server → initiator) and when echoing one (initiator → server).
    /// </summary>
    public Task SendNatChallengeAsync(IPEndPoint target, ReadOnlySpan<byte> token, CancellationToken cancellationToken)
    {
        const PacketType Type = PacketType.NatChallenge;
        int headerSize = PacketHeader.ComputeHeaderSize(Type);
        int totalSize = headerSize + token.Length;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        PacketHeader.Write(rentedBuffer.AsSpan(), Type, 0, 0, 0, 0);
        token.CopyTo(rentedBuffer.AsSpan(headerSize));
        return SendAndPoolBufferAsync(new(rentedBuffer, 0, totalSize), target, cancellationToken);
    }
}
```

- [ ] **Commit**
```
git add SynapseSocket/Transport/TransmissionEngine.Nat.cs
git commit -m "Update TransmissionEngine.Nat to use PacketType and add SendNatChallengeAsync"
```

---

### Task 6: Update `IngressEngine.cs`

**Files:**
- Modify: `SynapseSocket/Transport/IngressEngine.cs`

- [ ] **Add `_natChallengeSecret` field** after `_lastHandshakeEvictionTicks` (line 71):

```csharp
/// <summary>
/// Server secret used to sign NAT challenge tokens. Generated once at construction and never transmitted.
/// </summary>
private readonly byte[] _natChallengeSecret = new byte[32];
```

- [ ] **Initialize `_natChallengeSecret` in the constructor** — add after the last assignment in the constructor body:

```csharp
System.Security.Cryptography.RandomNumberGenerator.Fill(_natChallengeSecret);
```

- [ ] **Replace `ProcessPacket`** (lines 239–413) with the version below. The only structural change is replacing the chain of `if ((flags & ...) != 0)` checks with two switch blocks and renaming `flags` → `type`:

```csharp
private void ProcessPacket(byte[] buffer, int length, IPEndPoint fromEndPoint, CancellationToken cancellationToken, ref bool isPayloadCopied)
{
    PacketType type;
    ushort sequence;
    ushort segmentId;
    byte segmentIndex;
    byte segmentCount;
    int headerSize;

    try
    {
        headerSize = PacketHeader.Read(buffer.AsSpan(0, length), out type, out sequence, out segmentId, out segmentIndex, out segmentCount);
    }
    catch
    {
        _telemetry.OnDroppedIn();
        ulong signature = _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);
        ViolationOccurred?.Invoke(fromEndPoint, signature, ViolationReason.Malformed, length, "Header parse failure", ViolationAction.KickAndBlacklist);
        return;
    }

    // Connection-less packet types — handled before any connection lookup.
    switch (type)
    {
        case PacketType.Handshake:
            ProcessHandshake(fromEndPoint, buffer, headerSize, length, cancellationToken);
            return;

        case PacketType.NatProbe:
            ProcessNatProbe(fromEndPoint, cancellationToken);
            return;

        case PacketType.NatChallenge:
            ProcessNatChallengeExchange(fromEndPoint, buffer.AsSpan(headerSize, length - headerSize), cancellationToken);
            return;

        case PacketType.NatRegister:
        case PacketType.NatHeartbeat:
        case PacketType.NatHeartbeatAck:
        case PacketType.NatPeerReady:
        case PacketType.NatSessionFull:
            ProcessNatServerPacket(fromEndPoint, type, buffer.AsSpan(headerSize, length - headerSize));
            return;
    }

    if (!_connections.TryGet(fromEndPoint, out SynapseConnection? synapseConnection) || synapseConnection is null)
    {
        _telemetry.OnDroppedIn();
        return;
    }

    synapseConnection.LastReceivedTicks = DateTime.UtcNow.Ticks;

    switch (type)
    {
        case PacketType.Disconnect:
            synapseConnection.State = ConnectionState.Disconnected;
            _connections.Remove(fromEndPoint, out _);
            ConnectionClosed?.Invoke(synapseConnection);
            ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.PeerDisconnect, 0, null, ViolationAction.Ignore);
            return;

        case PacketType.KeepAlive:
            return;

        case PacketType.Ack:
            if (synapseConnection.PendingReliableQueue.TryRemove(sequence, out SynapseConnection.PendingReliable? acked))
                SynapseConnection.ReturnPendingReliableBuffers(acked);
            return;
    }

    int payloadLength = length - headerSize;

    if (payloadLength < 0)
    {
        _telemetry.OnDroppedIn();
        ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature, ViolationReason.Malformed, length, "Negative payload length", ViolationAction.KickAndBlacklist);
        return;
    }

    if ((type == PacketType.Segmented || type == PacketType.ReliableSegmented) && _config.MaximumReassembledPacketSize > 0)
    {
        if (segmentCount * _config.MaximumTransmissionUnit > _config.MaximumReassembledPacketSize)
        {
            _telemetry.OnDroppedIn();
            ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature,
                ViolationReason.Oversized, length,
                $"Declared segment assembly ({segmentCount} * {_config.MaximumTransmissionUnit} bytes) exceeds MaximumReassembledPacketSize",
                ViolationAction.KickAndBlacklist);
            return;
        }
    }

    switch (type)
    {
        case PacketType.Reliable:
        {
            byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
            Buffer.BlockCopy(buffer, headerSize, payloadBuffer, 0, payloadLength);
            ArraySegment<byte> payload = new(payloadBuffer, 0, payloadLength);
            _ = _sender.SendAckAsync(synapseConnection, sequence, cancellationToken);
            DeliverOrdered(synapseConnection, sequence, payload, isReliable: true);
            return;
        }

        case PacketType.ReliableSegmented:
        {
            if (_config.MaximumSegments is not SynapseConfig.DisabledMaximumSegments)
            {
                byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                Buffer.BlockCopy(buffer, headerSize, payloadBuffer, 0, payloadLength);
                ArraySegment<byte> payload = new(payloadBuffer, 0, payloadLength);
                PacketReassembler reassembler = GetOrRentReassembler(synapseConnection);

                if (reassembler.TryReassemble(segmentId, segmentIndex, segmentCount, payload, isReliable: true, out ArraySegment<byte> assembledPayload, out bool isProtocolViolation))
                {
                    _ = _sender.SendAckAsync(synapseConnection, sequence, cancellationToken);
                    DeliverOrdered(synapseConnection, sequence, assembledPayload, isReliable: true);
                }
                else if (isProtocolViolation)
                {
                    _telemetry.OnDroppedIn();
                    ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature,
                        ViolationReason.Malformed, length,
                        $"Segment {segmentId} resent with mismatched segmentCount/reliability",
                        ViolationAction.KickAndBlacklist);
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                    return;
                }

                ArrayPool<byte>.Shared.Return(payloadBuffer);
            }

            return;
        }

        case PacketType.Segmented:
        {
            if (_config.MaximumSegments != SynapseConfig.DisabledMaximumSegments)
            {
                byte[] segmentPayloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                Buffer.BlockCopy(buffer, headerSize, segmentPayloadBuffer, 0, payloadLength);
                PacketReassembler reassembler = GetOrRentReassembler(synapseConnection);

                if (reassembler.TryReassemble(segmentId, segmentIndex, segmentCount, new(segmentPayloadBuffer, 0, payloadLength), isReliable: false, out ArraySegment<byte> assembledPayload, out bool isProtocolViolation))
                {
                    PayloadDelivered?.Invoke(synapseConnection, assembledPayload, false);
                }
                else if (isProtocolViolation)
                {
                    _telemetry.OnDroppedIn();
                    ViolationOccurred?.Invoke(fromEndPoint, synapseConnection.Signature,
                        ViolationReason.Malformed, length,
                        $"Segment {segmentId} resent with mismatched segmentCount/reliability",
                        ViolationAction.KickAndBlacklist);
                    ArrayPool<byte>.Shared.Return(segmentPayloadBuffer);
                    return;
                }

                ArrayPool<byte>.Shared.Return(segmentPayloadBuffer);
            }

            return;
        }

        case PacketType.None:
        {
            if (!_config.CopyReceivedPayloads)
            {
                isPayloadCopied = false;
                PayloadDelivered?.Invoke(synapseConnection, new(buffer, headerSize, payloadLength), false);
            }
            else
            {
                byte[] payloadCopyBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                Buffer.BlockCopy(buffer, headerSize, payloadCopyBuffer, 0, payloadLength);
                PayloadDelivered?.Invoke(synapseConnection, new(payloadCopyBuffer, 0, payloadLength), false);
            }

            return;
        }
    }
}
```

- [ ] **Commit**
```
git add SynapseSocket/Transport/IngressEngine.cs
git commit -m "Refactor ProcessPacket to switch on PacketType; add _natChallengeSecret"
```

---

### Task 7: Update `IngressEngine.Nat.cs`

**Files:**
- Modify: `SynapseSocket/Transport/IngressEngine.Nat.cs`

- [ ] **Replace the entire file**

```csharp
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using SynapseSocket.Connections;
using SynapseSocket.Packets;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Transport;

/// <summary>
/// Ingress Engine (Receiver).
/// Manages incoming data and initial filtering.
/// Applies lowest-level mitigations BEFORE any payload copy.
/// </summary>
public sealed partial class IngressEngine
{
    /// <summary>
    /// Raised when a NAT rendezvous server reports the peer's external endpoint.
    /// </summary>
    public event NatPeerReadyDelegate? NatPeerReady;

    /// <summary>
    /// Raised when a NAT rendezvous server rejects the session because it is already full.
    /// </summary>
    public event NatSessionFullDelegate? NatSessionFull;

    /// <summary>
    /// Size of the HMAC-SHA256 token truncated to this many bytes for NAT challenge packets.
    /// </summary>
    private const int NatTokenSize = 8;

    /// <summary>
    /// Duration of a single time bucket in ticks. Tokens are valid for the current bucket and the previous one (~60 seconds total).
    /// </summary>
    private const long NatTokenTimeBucketTicks = 30 * TimeSpan.TicksPerSecond;

    /// <summary>
    /// Handles an inbound NAT probe from an unrecognised endpoint.
    /// Responds with a challenge token instead of a handshake, subject to per-IP rate limiting.
    /// </summary>
    private void ProcessNatProbe(IPEndPoint fromEndPoint, CancellationToken cancellationToken)
    {
        if (_config.NatTraversal.Mode == NatTraversalMode.Disabled)
            return;

        // Never respond to blacklisted addresses.
        ulong signature = _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);

        if (_security.IsBlacklisted(signature))
            return;

        // Only respond to unrecognised endpoints; established peers do not need probes.
        if (_connections.TryGet(fromEndPoint, out SynapseConnection? _))
            return;

        // Rate-limit outbound challenge responses per source IP.
        long nowTicks = DateTime.UtcNow.Ticks;
        long minIntervalTicks = _config.NatTraversal.IntervalMilliseconds * TimeSpan.TicksPerMillisecond;
        IpKey addressKey = IpKey.From(fromEndPoint.Address);

        long lastProbeEvict = Volatile.Read(ref _lastProbeEvictionTicks);

        if (nowTicks - lastProbeEvict > TimeSpan.TicksPerMinute)
        {
            if (Interlocked.CompareExchange(ref _lastProbeEvictionTicks, nowTicks, lastProbeEvict) == lastProbeEvict)
                RemoveExpiredProbeLimitEntries(nowTicks, staleTicks: minIntervalTicks * 10);
        }

        long lastTicks = _natProbeLastResponseTicks.GetOrAdd(addressKey, 0L);

        if (nowTicks - lastTicks < minIntervalTicks)
            return;

        _natProbeLastResponseTicks[addressKey] = nowTicks;

        Span<byte> token = stackalloc byte[NatTokenSize];
        ComputeNatToken(fromEndPoint, nowTicks / NatTokenTimeBucketTicks, token);
        _ = _sender.SendNatChallengeAsync(fromEndPoint, token, cancellationToken);
    }

    /// <summary>
    /// Handles an inbound NatChallenge packet from an unrecognised endpoint.
    /// If the payload matches a token this engine issued, sends a handshake (completing the probe exchange).
    /// Otherwise echoes the token back — this is the initiator side of a simultaneous P2P probe.
    /// </summary>
    private void ProcessNatChallengeExchange(IPEndPoint fromEndPoint, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length != NatTokenSize)
            return;

        if (_config.NatTraversal.Mode == NatTraversalMode.Disabled)
            return;

        ulong signature = _security.ComputeSignature(fromEndPoint, ReadOnlySpan<byte>.Empty);

        if (_security.IsBlacklisted(signature))
            return;

        if (_connections.TryGet(fromEndPoint, out SynapseConnection? _))
            return;

        long nowTicks = DateTime.UtcNow.Ticks;
        long minIntervalTicks = _config.NatTraversal.IntervalMilliseconds * TimeSpan.TicksPerMillisecond;
        IpKey addressKey = IpKey.From(fromEndPoint.Address);

        long lastTicks = _natProbeLastResponseTicks.GetOrAdd(addressKey, 0L);

        if (nowTicks - lastTicks < minIntervalTicks)
            return;

        _natProbeLastResponseTicks[addressKey] = nowTicks;

        if (VerifyNatToken(fromEndPoint, payload))
            _ = _sender.SendHandshakeAsync(fromEndPoint, cancellationToken);
        else
            _ = _sender.SendNatChallengeAsync(fromEndPoint, payload, cancellationToken);
    }

    /// <summary>
    /// Routes an inbound packet from the configured NAT rendezvous server to the appropriate handler.
    /// </summary>
    private void ProcessNatServerPacket(IPEndPoint fromEndPoint, PacketType packetType, ReadOnlySpan<byte> payload)
    {
        if (_config.NatTraversal.Mode != NatTraversalMode.Server)
            return;

        if (_config.NatTraversal.Server.ServerEndPoint is null)
            return;

        if (!fromEndPoint.Equals(_config.NatTraversal.Server.ServerEndPoint))
            return;

        switch (packetType)
        {
            case PacketType.NatPeerReady:
                IPEndPoint? peerEndPoint = TryParsePeerEndPoint(payload);
                if (peerEndPoint is not null)
                    NatPeerReady?.Invoke(peerEndPoint);
                break;

            case PacketType.NatSessionFull:
                NatSessionFull?.Invoke();
                break;

            case PacketType.NatHeartbeatAck:
                break;
        }
    }

    /// <summary>
    /// Computes a truncated HMAC-SHA256 token bound to <paramref name="endPoint"/> and <paramref name="timeBucket"/>.
    /// Writes exactly <see cref="NatTokenSize"/> bytes into <paramref name="destination"/>.
    /// </summary>
    private void ComputeNatToken(IPEndPoint endPoint, long timeBucket, Span<byte> destination)
    {
        Span<byte> addressBytes = stackalloc byte[16];
        endPoint.Address.TryWriteBytes(addressBytes, out int addressLength);

        int inputLength = addressLength + 2 + 8;
        Span<byte> input = stackalloc byte[inputLength];
        addressBytes[..addressLength].CopyTo(input);

        int offset = addressLength;
        input[offset++] = (byte)(endPoint.Port & 0xFF);
        input[offset++] = (byte)((endPoint.Port >> 8) & 0xFF);

        for (int i = 0; i < 8; i++)
            input[offset++] = (byte)((timeBucket >> (i * 8)) & 0xFF);

        Span<byte> hashBuffer = stackalloc byte[32];
        using HMACSHA256 hmac = new(_natChallengeSecret);
        hmac.TryComputeHash(input, hashBuffer, out _);
        hashBuffer[..NatTokenSize].CopyTo(destination);
    }

    /// <summary>
    /// Returns true if <paramref name="token"/> matches the expected token for the current or previous time bucket.
    /// </summary>
    private bool VerifyNatToken(IPEndPoint endPoint, ReadOnlySpan<byte> token)
    {
        long bucket = DateTime.UtcNow.Ticks / NatTokenTimeBucketTicks;
        Span<byte> expected = stackalloc byte[NatTokenSize];

        ComputeNatToken(endPoint, bucket, expected);
        if (token.SequenceEqual(expected))
            return true;

        ComputeNatToken(endPoint, bucket - 1, expected);
        return token.SequenceEqual(expected);
    }

    /// <summary>
    /// Evicts stale entries from the NAT probe response-time dictionary.
    /// </summary>
    private void RemoveExpiredProbeLimitEntries(long nowTicks, long staleTicks) =>
        RemoveExpiredEntries(_natProbeLastResponseTicks, nowTicks, staleTicks);

    /// <summary>
    /// Parses a NAT server packet body into an <see cref="IPEndPoint"/>.
    /// Returns null if the body is malformed or too short.
    /// </summary>
    private static IPEndPoint? TryParsePeerEndPoint(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1)
            return null;

        byte addrFamily = body[0];
        ReadOnlySpan<byte> rest = body[1..];

        if (addrFamily == 4 && rest.Length >= 6)
        {
            IPAddress ip = new(rest[..4]);
            ushort port = (ushort)(rest[4] | (rest[5] << 8));
            return new(ip, port);
        }

        if (addrFamily == 6 && rest.Length >= 18)
        {
            IPAddress ip = new(rest[..16]);
            ushort port = (ushort)(rest[16] | (rest[17] << 8));
            return new(ip, port);
        }

        return null;
    }
}
```

- [ ] **Commit**
```
git add SynapseSocket/Transport/IngressEngine.Nat.cs
git commit -m "Add NAT challenge-response; update IngressEngine.Nat to use PacketType"
```

---

### Task 8: Delete old files and verify build

**Files:**
- Delete: `SynapseSocket/Packets/PacketFlags.cs`
- Delete: `SynapseSocket/Packets/NatPacketType.cs`

- [ ] **Delete both files**
```
rm "SynapseSocket/Packets/PacketFlags.cs"
rm "SynapseSocket/Packets/NatPacketType.cs"
```

- [ ] **Verify the build is clean**
```
dotnet build SynapseSocket/SynapseSocket.csproj
```
Expected: build succeeds with 0 errors and 0 warnings.

- [ ] **Update the security vulnerabilities report** — open `SECURITY_VULNERABILITIES.md` and update V6 to mark it resolved:

In the `## High Severity Vulnerabilities` section, replace the V6 entry body with:

```
**Severity:** RESOLVED  
**Resolution:** Replaced with stateless HMAC challenge-response. The server now issues a signed token on probe receipt and only sends a handshake after the peer echoes the token back, proving bidirectional reachability. A spoofed-source attacker never receives the token and cannot complete the exchange.
```

- [ ] **Commit**
```
git add -A
git commit -m "Remove PacketFlags and NatPacketType; mark V6 resolved in security report"
```