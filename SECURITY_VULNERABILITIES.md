# SynapseSocket — Security Vulnerability Report

**Date:** 2026-04-11  
**Scope:** Full codebase scan — DDoS, replay, amplification, flooding, resource exhaustion, state machine attacks  
**Status:** For review only — no fixes applied

---

## Summary Table

| # | Vulnerability | Severity | File | Category |
|---|---|---|---|---|
| 6 | NAT probe sends two responses — amplification vector | HIGH | `IngressEngine.Nat.cs` | Amplification |
| 7 | Rate limiting counts packets only, not bytes | HIGH | `SecurityProvider.cs` | Resource Exhaustion |
| 8 | Segment count not validated against actual payload bytes | HIGH | `IngressEngine.cs` | Resource Exhaustion |
| 9 | Handshake nonce generated but never echoed or validated | MEDIUM | `TransmissionEngine.cs` | Replay |
| 10 | Default signature ignores port — same-IP collision | MEDIUM | `DefaultSignatureProvider.cs` | Spoofing |
| 11 | NAT session ID too short (6 chars ≈ 2.1 billion) | MEDIUM | `ServerNatConfig.cs` | Brute-force |
| 13 | No ACK batching — ACK flood possible | LOW | `IngressEngine.cs` | Flooding |
| 14 | Keep-alive rate not adaptive | LOW | `SynapseManager.Maintenance.cs` | Efficiency |

---

## Critical Vulnerabilities

### V1 — Sequence Number Wrapping *(Not a valid vulnerability)*

**Severity:** REMOVED  
**Reason:** Sequence numbers and the reorder buffer are per-`SynapseConnection`. Any "attacker" exploiting the wrap can only affect their own session — they are the peer on that connection and can already send arbitrary bytes. Affecting a *different* peer's session would require spoofing that peer's source `IPEndPoint`, which is the V12 (no HMAC) problem, not a sequence-number problem. Removed from the active list.

---

### V2 — Segment ID Reuse After Wrap *(Not a valid vulnerability)*

**Severity:** REMOVED  
**Reason:** The original analysis was incorrect. The `PacketReassembler` removes completed assemblies from `_currentSegments` immediately on success (`_currentSegments.Remove(segmentId)` at line 101). Multiple additional safeguards make an exploitable collision effectively impossible:

- `_maximumConcurrentAssemblies` caps concurrent in-progress assemblies; an old incomplete assembly would have been evicted by this cap long before 65 536 new segmented sends complete.
- `RemoveExpiredSegments` timeout eviction clears any assemblies that do linger.
- Protocol violation detection (lines 85–95) catches a reused ID that arrives with a different `segmentCount` or `isReliable` flag, raising a `ViolationReason.Malformed` event.

This entry has been removed from the active vulnerability list.

---

### V3 — Reorder Buffer Unbounded Growth *(Not a valid vulnerability)*

**Severity:** REMOVED  
**Reason:** The reorder buffer does not overflow — it simply holds packets waiting for gaps to be filled. `MaximumOutOfOrderReliablePackets = 64` (default) caps it: once the buffer reaches that count the peer is kicked and blacklisted regardless of how wide the sequence gaps are. A window-gap check would only be a minor early-out optimization, not a missing safeguard. On disconnect or timeout, `ReturnReorderBufferToPool` clears all buffered memory. No unbounded growth is possible.

---

## High Severity Vulnerabilities

### V4 — ACK Forgery *(Not a valid vulnerability)*

**Severity:** REMOVED  
**Reason:** `PendingReliableQueue` is scoped to the individual `SynapseConnection`. A peer sending forged ACKs is only suppressing retransmission of their own packets — something they could achieve simply by not sending them. Affecting another peer's queue requires IP spoofing, which is out of scope.

---

### V5 — Handshake Replay Cache TTL Too Short *(Not a valid vulnerability)*

**Severity:** REMOVED  
**Reason:** The nonce in the handshake payload makes every legitimate handshake's signature unique — a replay of the same bytes produces the same signature and is rejected. The TTL is just cache housekeeping to prevent unbounded growth, not the primary replay defense. Furthermore, line 590 (`isNewConnection || State != Connected`) means a replay against an already-established connection is silently ignored even if a cache entry did expire. The only remaining scenario requires the attacker to receive the server's response, which means controlling the victim's IP — out of scope.

---

### V6 — NAT Probe Sends Two Response Packets (Amplification)

**Severity:** HIGH  
**Files:** `SynapseSocket/Transport/IngressEngine.Nat.cs` (lines ~33–72)  
**Category:** Reflection / amplification DDoS

**Description:**  
On receiving a NAT probe, the engine calls both `SendNatProbeAsync` **and** `SendHandshakeAsync` to the source address. An attacker who spoofs the victim's IP as the source will cause the server to send two UDP packets to the victim for every one probe sent by the attacker.

**Exploit:**
1. Attacker forges UDP NAT probe packets with victim's IP as the source.
2. Server responds with probe + handshake to the victim.
3. The rate limiter (200 ms cooldown per address key) limits this to ~5 response-pairs per second per source IP — but across many source IPs or many servers the victim receives a significant flood.

**Suggested fix:**  
Send only **one** response packet (the probe response). Do not send an unsolicited handshake until the remote peer has demonstrated bidirectional reachability (i.e., completed a full round-trip).

---

### V7 — Rate Limiting Counts Packets, Not Bytes

**Severity:** HIGH  
**Files:** `SynapseSocket/Security/SecurityProvider.cs` (lines ~98–108) or equivalent  
**Category:** Resource exhaustion / bandwidth amplification

**Description:**  
`InspectEstablished` counts incoming packets per second against `_maximumPacketsPerSecond` but does not measure bytes per second. An attacker stays within the packet-rate cap while sending maximum-size payloads, potentially saturating the server's bandwidth or processing pipeline.

**Exploit:**
- `MaximumPacketsPerSecond = 2 000`, `MaximumTransmissionUnit = 1 200` bytes.
- Attacker sends 2 000 packets × 1 200 bytes = 2.4 MB/s per connection, unlimited in aggregate.
- With 100 connections from different IPs: 240 MB/s, all within the per-connection packet-rate limit.

**Suggested fix:**  
Add a parallel `bytesPerSecond` token-bucket alongside the packet-count bucket. Reject packets that would exceed a configured `MaximumBytesPerSecond` threshold.

---

### V8 — Segment Count Not Validated Against Actual Payload Bytes

**Severity:** HIGH  
**Files:** `SynapseSocket/Transport/IngressEngine.cs` (lines ~315–328)  
**Category:** Resource exhaustion

**Description:**  
When the first segment of a fragmented message arrives, the engine validates that `segmentCount × MTU ≤ MaximumReassembledPacketSize`. However, it does not validate that the stated `segmentCount` is consistent with the actual payload present in the first segment. A malicious sender can claim 1 000 segments but send only 1, keeping an incomplete `SegmentAssembly` object alive in memory until the timeout.

**Exploit:**
1. Send a segment with `segmentCount = 1 000` but a tiny payload.
2. Never send the remaining 999 segments.
3. The server allocates a `SegmentAssembly` and holds it for the full timeout period.
4. Repeat rapidly across many connections to exhaust memory.

**Suggested fix:**  
Enforce an aggressive maximum number of incomplete assemblies per connection (e.g., 4). If exceeded, drop the oldest or reject the new one. Already having a timeout helps, but should be combined with a cap on concurrent assemblies.

---

## Medium Severity Vulnerabilities

### V9 — Handshake Nonce Generated But Never Validated

**Severity:** MEDIUM  
**Files:** `SynapseSocket/Transport/TransmissionEngine.cs` (lines ~208–223)  
**Category:** Replay / protocol weakness

**Description:**  
The handshake payload includes 4 bytes filled with `RandomNumberGenerator.Fill(...)`. However, the receiver never echoes those bytes back and never validates them. The nonce exists in the packet but provides no cryptographic guarantee — it is security theater that gives false confidence.

**Suggested fix:**  
Require the responder to echo the initiator's nonce (or a keyed derivation of it) in the handshake acknowledgment. Only complete the handshake if the echoed value matches.

---

### V10 — Default Signature Provider Excludes Port (Same-IP Collision)

**Severity:** MEDIUM  
**Files:** `SynapseSocket/Security/DefaultSignatureProvider.cs` (lines ~22–48)  
**Category:** Spoofing / misrouting

**Description:**  
The default `ISignatureProvider` hashes only the IP address bytes, ignoring the source port. Two distinct UDP endpoints from the same IP address (e.g., clients behind NAT, or two processes on the same machine) produce an identical signature. The `ConnectionManager`'s signature → connection lookup can be confused, and the "signature collision" event is an unreliable defense.

**Suggested fix:**  
Include the port in the hash: after hashing address bytes, `hash ^= (ulong)endPoint.Port; hash *= FnvPrime;`

---

### V11 — NAT Session ID Space Too Small (6 Alphanumeric Characters)

**Severity:** MEDIUM  
**Files:** `SynapseSocket/Core/Configuration/ServerNatConfig.cs` (line ~40)  
**Category:** Brute-force

**Description:**  
NAT server session IDs are 6 characters from `[A-Z0-9]` = 36^6 ≈ 2.18 billion combinations. Session IDs are transmitted in plaintext in NAT packets. An attacker making 1 000 requests/second exhausts the space in about 25 days. With multiple attacking IPs or lucky collisions the window is much smaller.

**Suggested fix:**  
Use at least 12–16 characters (36^12 ≈ 4.7 × 10^18) or switch to UUID v4. Additionally, implement per-IP rate limiting on session registration.

---

## Low Severity / Design Issues

### V13 — No ACK Batching: ACK Flood Possible

**Severity:** LOW  
**Files:** `SynapseSocket/Transport/IngressEngine.cs` (lines ~299–304)  
**Category:** Response flooding

**Description:**  
Every reliable packet triggers an immediate, individual ACK response. A peer (or attacker) sending a rapid burst of reliable packets receives an equal burst of ACK responses. This is not dangerous on its own but can amplify traffic in congested networks and cause the server to spend disproportionate time sending ACKs.

**Suggested fix:**  
Implement delayed ACKs: buffer ACKs and flush after N packets or T milliseconds (whichever comes first), a standard TCP optimization easily adapted to UDP.

---

### V14 — Keep-Alive Rate Not Adaptive

**Severity:** LOW  
**Files:** `SynapseSocket/Core/SynapseManager.Maintenance.cs` (lines ~86–92)  
**Category:** Efficiency / minor flooding

**Description:**  
Keep-alive packets are sent at a fixed 5-second interval regardless of connection health or peer responsiveness. If a peer is already sending traffic, keep-alives are redundant. If a peer is unresponsive, fixed-interval keep-alives generate unnecessary traffic.

**Suggested fix:**  
Reset the keep-alive timer on any received packet; only send keep-alives during genuine silence periods. Apply exponential backoff when keep-alives go unacknowledged.

---

## Recommended Fix Priority

| Priority | Item | Reason |
|---|---|---|
| 1 | V6 — NAT probe amplification | Easy reflection vector, small code change to fix |
| 3 | V7 — Byte-rate limiting | Packet-only rate limiting is trivially bypassed |
| 5 | V8 — Max concurrent assemblies per connection | Prevents low-cost memory exhaustion via fake segment counts |
| 6 | V10 — Port in signature hash | One-line fix, eliminates same-IP collision |
| 8 | V11 — NAT session ID length | Increase to 12+ characters |