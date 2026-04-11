# SynapseSocket — Allocation Audit

**Date:** 2026-04-11
**Scope:** Full solution scan for heap allocations on hot/repeat paths (per-packet, per-connection, per-send, per-NAT-message, maintenance sweeps)
**Status:** For review only — no fixes applied

---

## Summary

| # | Location | Severity | Hotness | Type | Status |
|---|---|---|---|---|---|
| 1 | `TransmissionEngine.SendReliableUnsegmentedAsync` — raw byte buffer | CRITICAL | Per reliable send | `byte[]` | RESOLVED |
| 2 | `TransmissionEngine.SendReliableUnsegmentedAsync` — PendingReliable object | CRITICAL | Per reliable send | `PendingReliable` | RESOLVED |
| 3 | `NatServer` send helpers — packet buffers | HIGH | Per NAT message | `byte[]` | RESOLVED |
| 4 | `NatServer.SendPeerReady` — IPAddress.GetAddressBytes | HIGH | Per NAT peer match | `byte[]` | RESOLVED |
| 5 | `NatSessionRegistry` — session ID strings | MEDIUM | Per session / per packet | `string` | RESOLVED |
| 6 | `IngressEngine.Nat.TryParsePeerEndPoint` — IPAddress + IPEndPoint | MEDIUM | Per peer-ready | `IPAddress`, `IPEndPoint` | skipped — unavoidable on .NET Standard 2.1 |
| 7 | `IngressEngine` — violation string interpolation | LOW | Per violation | `string` | RESOLVED |
| 8 | `SynapseManager.Maintenance` — retransmit exhaustion message | LOW | Per exhausted connection | `string` | RESOLVED |

---

## CRITICAL

### 1. `TransmissionEngine.SendReliableUnsegmentedAsync` — raw buffer allocation

**File:** `SynapseSocket/Transport/TransmissionEngine.cs` (line ~115)
**Severity:** CRITICAL
**Hotness:** Every reliable unsegmented send

```csharp
byte[] packetBuffer = new byte[totalLength];
int written = PacketHeader.BuildPacket(packetBuffer.AsSpan(), Type, sequence, 0, 0, 0, payload.AsSpan());
```

**Issue:**
Every reliable unsegmented send allocates a fresh `byte[]`. This is the only send path in `TransmissionEngine` that does not use `ArrayPool<byte>.Shared`. The buffer must be retained until the ACK is received (it lives in `PendingReliable.Payload` for retransmission), but that does not preclude renting from a pool — pooling would just require returning the buffer in `ReturnPendingReliableBuffers` when the packet is ACK'd or the connection is kicked.

**Suggested fix:**
- Rent via `ArrayPool<byte>.Shared.Rent(totalLength)`
- Store the rented array in `PendingReliable.Payload`
- Track the logical length separately via the existing `PacketLength` field
- Extend `SynapseConnection.ReturnPendingReliableBuffers` to handle the non-segmented case — currently it only returns segment buffers

---

### 2. `TransmissionEngine.SendReliableUnsegmentedAsync` — `PendingReliable` allocation

**File:** `SynapseSocket/Transport/TransmissionEngine.cs` (line ~118)
**Severity:** CRITICAL
**Hotness:** Every reliable send (unsegmented and segmented)

```csharp
SynapseConnection.PendingReliable pendingReliable = new()
{
    Sequence = sequence,
    Payload = packetBuffer,
    PacketLength = written,
    SentTicks = DateTime.UtcNow.Ticks,
    Retries = 0
};
```

**Issue:**
A fresh `PendingReliable` is allocated per reliable send. This object has clear ownership transitions (created on send → held in `PendingReliableQueue` → released on ACK or eviction), making it a textbook pool candidate.

**Suggested fix:**
- Pool via `ResettableObjectPool<PendingReliable>`
- Rent in `SendReliableUnsegmentedAsync` and `SendSegmentedAsync` (reliable branch)
- Return in `PacketType.Ack` handling in `IngressEngine`, in retransmit exhaustion eviction, and on connection kick
- Ensure the reset logic clears `Segments`, `Payload`, and other fields to avoid leaked references

---

## HIGH

### 3. `NatServer` send helpers — per-packet buffer allocation

**File:** `SynapseSocket.NatServer/NatServer.cs` (lines ~166, 174, 187, 200, 208)
**Severity:** HIGH
**Hotness:** Every outgoing NAT server packet

```csharp
byte[] packet = new byte[1 + SessionIdBytes];            // SendNatSessionCreated
byte[] packet = new byte[1 + 1 + peerBytes.Length + 2];  // SendPeerReady
byte[] packet = [(byte)PacketType.NatHeartbeatAck];       // SendHeartbeatAck
byte[] packet = [(byte)PacketType.NatSessionFull];        // SendSessionFull
byte[] packet = [(byte)PacketType.NatSessionUnavailable]; // SendSessionUnavailable
```

**Issue:**
Every NAT server response allocates a fresh array. During active matchmaking (many hosts creating sessions, many joiners registering), this fires frequently.

**Suggested fix:**
- Rent via `ArrayPool<byte>.Shared.Rent(size)` and return after `SendAsync` completes
- Alternatively, use a per-thread scratch buffer (`[ThreadStatic]`) since the `NatServer` receive loop is single-threaded — no contention
- For the 1-byte single-type packets, a static readonly `byte[]` literal per type is allocation-free (they never change)

---

### 4. `NatServer.SendPeerReady` — `GetAddressBytes` allocation

**File:** `SynapseSocket.NatServer/NatServer.cs` (line ~183)
**Severity:** HIGH
**Hotness:** Every matched peer pair (sends twice per match)

```csharp
byte[] peerBytes = peer.Address.GetAddressBytes();
```

**Issue:**
`IPAddress.GetAddressBytes()` allocates a fresh 4- or 16-byte array per call. Called twice per successful NAT match (once for each side of the peer-ready notification).

**Suggested fix:**
Use `IPAddress.TryWriteBytes(Span<byte>, out int)` to write directly into the already-rented packet buffer. This pattern is already used in `IngressEngine.Nat.ComputeNatToken` and `Security/DefaultSignatureProvider.TryCompute`:

```csharp
Span<byte> addressBytes = stackalloc byte[16];
peer.Address.TryWriteBytes(addressBytes, out int writtenCount);
```

---

## MEDIUM

### 5. `NatSessionRegistry` — session ID string allocations

**File:** `SynapseSocket.NatServer/NatSessionRegistry.cs` (lines ~129, ~142)
**Severity:** MEDIUM
**Hotness:** Per session creation, per incoming NAT server packet

```csharp
// ParseSessionId — once per NAT register/heartbeat/close packet
return Encoding.ASCII.GetString(data, offset, length);

// GenerateId — once per new session
StringBuilder stringBuilder = new(SessionIdLength);
foreach (byte b in bytes)
    stringBuilder.Append(AlphaNumericChars[b % AlphaNumericChars.Length]);
return stringBuilder.ToString();
```

Also mirrored on the client in `IngressEngine.Nat.ParseNatSessionId`:
```csharp
return System.Text.Encoding.ASCII.GetString(payload[..ServerNatConfig.SessionIdLength]);
```

**Issue:**
Every NAT server packet parse allocates a new 6-character `string`. The server-side session ID is then used as a dictionary key, pinning it in memory for the session lifetime. `GenerateId` additionally allocates a `StringBuilder` and its backing array.

**Suggested fix (optional — larger refactor):**
- Switch sessions to a 6-byte struct key (`SessionIdKey` fixed buffer) and use it as the dictionary key via `IEquatable<SessionIdKey>`
- Replace `StringBuilder` in `GenerateId` with a `Span<char>` + `new string(Span<char>)`, saving the `StringBuilder` allocation (final `string` still unavoidable)
- If session IDs must remain `string`, this is a bounded cost (one short string per session event) and can be deferred

---

### 6. `IngressEngine.Nat.TryParsePeerEndPoint` — `IPAddress` / `IPEndPoint` allocation

**File:** `SynapseSocket/Transport/IngressEngine.Nat.cs` (lines ~248, 255)
**Severity:** MEDIUM
**Hotness:** Every `NatPeerReady` parse

```csharp
IPAddress ip = new(rest[..4]);
ushort port = (ushort)(rest[4] | (rest[5] << 8));
return new(ip, port);
```

**Issue:**
`IPAddress` and `IPEndPoint` are both reference types and must be allocated on the heap. Both are needed downstream to key into the connection manager and initiate the hole-punch.

**Decision:**
Skipped. `IPAddress` has no allocation-free construction path on .NET Standard 2.1 — the `IPAddress(ReadOnlySpan<byte>)` constructor is not available. Acceptable as-is; only fires once per matched peer pair.

---

## LOW

### 7. `IngressEngine` — violation string interpolations

**File:** `SynapseSocket/Transport/IngressEngine.cs` (lines ~336, ~373, ~402, ~497)
**Severity:** LOW
**Hotness:** Per detected violation (peer is being kicked/blacklisted anyway)

```csharp
$"Declared segment assembly ({segmentCount} * {_config.MaximumTransmissionUnit} bytes) exceeds MaximumReassembledPacketSize"
$"Segment {segmentId} resent with mismatched segmentCount/reliability"
$"Reorder buffer cap ({_config.MaximumOutOfOrderReliablePackets}) exceeded"
```

**Issue:**
Each interpolation allocates a new `string` at a violation site.

**Suggested fix:**
Acceptable as-is. Violations are, by definition, abnormal events and the allocation is dwarfed by the subsequent kick/blacklist work. Only worth optimising if a specific attacker pattern demonstrates sustained violation throughput.

---

### 8. `SynapseManager.Maintenance` — retransmit exhaustion message

**File:** `SynapseSocket/Core/SynapseManager.Maintenance.cs` (line ~165)
**Severity:** LOW
**Hotness:** Once per connection that exhausts retries

```csharp
HandleViolation(..., $"seq={pendingReliable.Sequence}", ...);
```

**Issue:**
String interpolation allocates a short `string`.

**Suggested fix:**
Acceptable as-is — fires exactly once per connection-terminating event.

---

## Verified Clean (No Issues)

These subsystems were audited and confirmed to follow proper pooling discipline:

- **Event argument pooling** — `PacketReceivedEventArgs`, `ConnectionEventArgs`, `PacketSentEventArgs`, `ConnectionFailedEventArgs`, `ViolationEventArgs` all rented from `ResettableObjectPool<T>` and returned after dispatch
- **Packet segmentation** — `PacketSplitter`, `PacketReassembler`, `SegmentAssembly` all rent from `ArrayPool<byte>` / `ArrayPool<ArraySegment<byte>>` and return on ACK or timeout
- **Rate limiting** — `RateBucket` instances held in `_rateBuckets` dict for the connection lifetime, evicted via TTL sweep every 5 minutes
- **NAT challenge tokens** — `stackalloc` + `HMACSHA256.TryComputeHash`, fully stack-based
- **Signature computation** — `DefaultSignatureProvider.TryCompute` uses `stackalloc` and `TryWriteBytes`, no heap allocation
- **Engine construction** — sockets, dictionaries, config objects all allocated once at `StartAsync` and reused for the engine lifetime

---

## Recommended Priority

| Priority | Item | Reason |
|---|---|---|
| 1 | #1 — `SendReliableUnsegmentedAsync` buffer pooling | Fires every reliable send; simple fix (pattern already exists in segmented path) |
| 2 | #2 — `PendingReliable` object pooling | Same hotness; requires `ResettableObjectPool<PendingReliable>` and reset logic |
| 3 | #3 — `NatServer` response buffer pooling | Fires on every NAT message during matchmaking |
| 4 | #4 — `GetAddressBytes` → `TryWriteBytes` | Trivial fix, pattern already used elsewhere |
| 5 | #5 — Session ID struct key | Larger refactor, may not be worth it |
| - | #6, #7, #8 | Leave as-is unless profiling shows they matter |
