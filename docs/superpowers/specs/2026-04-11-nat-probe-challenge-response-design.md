# NAT Probe Challenge-Response Design

**Date:** 2026-04-11  
**Status:** Approved

---

## Problem

`ProcessNatProbe` currently responds to every incoming NAT probe with two outbound packets: a probe and a handshake. If an attacker spoofs the victim's IP as the probe source, the server sends both packets to the victim. The existing per-source-IP rate limit bounds this to one pair per interval, but the structural flaw remains: the server sends the handshake to an address that has not proven it can receive packets.

A secondary design issue: `PacketFlags` is a `[Flags]` enum but only one real flag combination exists (`Reliable | Segmented`). `Extended` consumes a bit to mean "NAT probe", limiting extensibility.

---

## Solution

Two changes together:

1. **`PacketFlags` → `PacketType`** — Replace the flags enum with a plain discrete-value enum. Every packet type gets its own value. All NAT sub-types are absorbed directly. `Extended` and `NatPacketType` are eliminated. `PacketHeader` logic simplifies to switch statements.

2. **Challenge-response for NAT probes** — The server sends a signed HMAC token instead of a handshake. The handshake is only sent after the remote endpoint proves bidirectional reachability by echoing the token.

---

## `PacketType` Enum

Replaces both `PacketFlags` and `NatPacketType`. No `[Flags]` attribute.

```
None = 0             unreliable unsegmented data
Reliable = 1         reliable unsegmented data          (+ sequence number)
Ack = 2              acknowledgment                     (+ sequence number)
Handshake = 3        handshake / handshake-ack
KeepAlive = 4        keep-alive heartbeat
Disconnect = 5       graceful disconnect
Segmented = 6        unreliable segmented data          (+ segment fields)
ReliableSegmented = 7  reliable segmented data          (+ sequence + segment fields)
NatProbe = 8         NAT punch probe, no payload
NatChallenge = 9     challenge or echo, 8-byte token payload
NatRegister = 10     register with rendezvous server
NatHeartbeat = 11    keep rendezvous session alive
NatHeartbeatAck = 12 rendezvous server heartbeat ack
NatPeerReady = 13    rendezvous server reports peer endpoint
NatSessionFull = 14  rendezvous server rejects full session
```

---

## Wire Format

The first byte of every packet is now a `PacketType` value. Optional header fields follow based on type — same positions as before, just selected by type rather than flag bits:

```
[PacketType byte] [sequence 2 bytes if Reliable|Ack|ReliableSegmented]
                  [segmentId 2 + segmentIndex 1 + segmentCount 1 if Segmented|ReliableSegmented]
                  [payload]
```

`PacketHeader.ComputeHeaderSize` becomes a switch expression. `Read` and `Write` use equality checks instead of bitwise masks.

---

## Packet Flow (Challenge-Response)

```
Initiator                        Receiver
   |                                |
   |-- NatProbe -----------------> |  no payload
   |                                |
   |<-- NatChallenge ---------------|  8-byte HMAC token
   |                                |
   |-- NatChallenge --------------> |  echo (same 8 bytes)
   |                                |
   |<-- Handshake ------------------|  connection established
```

`NatChallenge` is used for both the outbound challenge and the echo. The receiver distinguishes them by verifying the token — if it matches, it is a valid echo; if not, it is a challenge from the other side and is echoed back. This handles simultaneous P2P probing without special casing.

---

## Token

```
token = HMAC-SHA256(secret, addressBytes || port(2 LE) || timeBucket(8 LE))[0..7]
```

- **secret** — 32 bytes, `RandomNumberGenerator.Fill` at `IngressEngine` construction. Never transmitted.
- **timeBucket** — `DateTime.UtcNow.Ticks / (30 * TimeSpan.TicksPerSecond)` as `long`. 30-second validity windows.
- **Verification** — checks bucket `N` and `N-1` to handle boundary crossings.
- **Size** — 8 bytes (2^64 collision resistance).

Fully stateless. No pending-challenge dictionary required.

---

## `ProcessPacket` Routing

Replaces the current chain of bitwise `if` checks with two switch blocks:

**Pre-connection switch** (no `SynapseConnection` lookup needed):
```
NatProbe       → ProcessNatProbe
NatChallenge   → ProcessNatChallengeExchange
NatRegister /
NatHeartbeat /
NatHeartbeatAck /
NatPeerReady /
NatSessionFull → ProcessNatServerPacket(type, payload)
Handshake      → ProcessHandshake
```

**Post-connection switch** (requires established `SynapseConnection`):
```
Disconnect         → disconnect handling
KeepAlive          → return
Ack                → remove from PendingReliableQueue
Reliable           → copy payload, send ACK, DeliverOrdered
ReliableSegmented  → copy payload, reassemble, send ACK on complete, DeliverOrdered
Segmented          → copy payload, reassemble, deliver
None               → deliver (zero-copy or copy path)
```

---

## `ProcessNatServerPacket` Change

Signature changes from reading `NatPacketType` as the first payload byte to accepting `PacketType` as a parameter. The payload byte is removed from all NAT server send helpers.

---

## Challenge-Response Handlers (`IngressEngine.Nat.cs`)

**`ProcessNatProbe`** (existing, modified):  
All existing guards preserved. Replaces the two send calls with: compute token, call `SendNatChallengeAsync`.

**`ProcessNatChallengeExchange`** (new):
```
1. payload.Length != 8  → drop
2. NAT disabled         → drop
3. Blacklisted          → drop
4. Already connected    → drop
5. Rate limited         → drop  (reuses _natProbeLastResponseTicks)
6. VerifyNatToken true  → SendHandshakeAsync
   VerifyNatToken false → SendNatChallengeAsync (echo)
```

**Token helpers:**
```
ComputeNatToken(IPEndPoint, long timeBucket, Span<byte> destination)
VerifyNatToken(IPEndPoint, ReadOnlySpan<byte> token) → bool
```

Uses `HMACSHA256.TryComputeHash` into a stackalloc buffer — no heap allocation on the hot side of the check.

---

## Transmission Changes (`TransmissionEngine.Nat.cs`)

- All send helpers updated to write `PacketType` byte; `NatPacketType` payload byte removed from server packets.
- `SendNatProbeAsync` — writes `PacketType.NatProbe` header.
- `SendNatChallengeAsync` (new) — writes `PacketType.NatChallenge` header + 8-byte token payload.

---

## Files Touched

| File | Change |
|---|---|
| `SynapseSocket/Packets/PacketType.cs` | **New** — replaces `PacketFlags` and absorbs `NatPacketType` |
| `SynapseSocket/Packets/PacketFlags.cs` | **Deleted** |
| `SynapseSocket/Packets/NatPacketType.cs` | **Deleted** |
| `SynapseSocket/Packets/PacketHeader.cs` | All methods updated to use `PacketType`; logic simplified to switch |
| `SynapseSocket/Packets/PacketSplitter.cs` | Flag combination replaced with `PacketType.ReliableSegmented` |
| `SynapseSocket/Transport/TransmissionEngine.cs` | All `PacketFlags` references updated to `PacketType` |
| `SynapseSocket/Transport/TransmissionEngine.Nat.cs` | Updated for `PacketType`; payload type byte removed; `SendNatChallengeAsync` added |
| `SynapseSocket/Transport/IngressEngine.cs` | `ProcessPacket` restructured to switch; `_natChallengeSecret` added |
| `SynapseSocket/Transport/IngressEngine.Nat.cs` | `ProcessNatServerPacket` signature updated; `ProcessNatProbe` modified; challenge-response handlers added |