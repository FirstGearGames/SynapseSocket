# SynapseSocket — Security Vulnerability Report

**Date:** 2026-04-11  
**Scope:** Full codebase scan — DDoS, replay, amplification, flooding, resource exhaustion, state machine attacks  
**Status:** For review only — no fixes applied

---

## Summary Table

| # | Vulnerability | Severity | File | Category |
|---|---|---|---|---|
| 6 | NAT probe sends two responses — amplification vector | RESOLVED | `IngressEngine.Nat.cs` | Amplification |
| 7 | Rate limiting counts packets only, not bytes | RESOLVED | `SecurityProvider.cs` | Resource Exhaustion |
| 8 | Segment count not validated against actual payload bytes | *(Not a valid vulnerability)* | `IngressEngine.cs` | Resource Exhaustion |
| 9 | Handshake nonce generated but never echoed or validated | RESOLVED | `TransmissionEngine.cs` | Replay |
| 10 | Default signature ignores port — same-IP collision | RESOLVED | `DefaultSignatureProvider.cs` | Spoofing |
| 11 | NAT session ID too short (6 chars ≈ 2.1 billion) | RESOLVED | `ServerNatConfig.cs` | Brute-force |
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

**Severity:** RESOLVED  
**Resolution:** Replaced with stateless HMAC challenge-response. The server now issues a signed token on probe receipt and only sends a handshake after the peer echoes the token back, proving bidirectional reachability. A spoofed-source attacker never receives the token and cannot complete the exchange.

---

### V7 — Rate Limiting Counts Packets, Not Bytes

**Severity:** RESOLVED  
**Resolution:** Added `MaximumBytesPerSecond` to `SynapseConfig` (default 0 = disabled) and a parallel `_byteCount` accumulator to `RateBucket`. Both limits are checked independently in a single window pass — exceeding either rejects the packet. Disabled by default to avoid breaking existing configs; set `MaximumBytesPerSecond` to enforce a byte cap alongside the packet cap.

---

---

## Medium Severity Vulnerabilities

### V9 — Handshake Nonce Generated But Never Validated

**Severity:** RESOLVED  
**Resolution:** The replay cache (`_seenHandshakes`) was keyed by IP-only signature, so the nonce had no effect — two handshakes from the same IP produced the same key, meaning reconnections were incorrectly blocked as replays and the nonce was unused. Fixed by introducing `MixHandshakeNonce` in `IngressEngine`, which FNV-1a mixes the nonce bytes into the replay cache key independently of the connection signature. The connection signature remains IP-only (so blacklisting survives reconnects); the replay key now incorporates the nonce (so each handshake is unique). `ISignatureProvider` documentation updated to reflect that providers no longer need to incorporate the payload for replay protection.

---

### V10 — Default Signature Provider Excludes Port (Same-IP Collision)

**Severity:** RESOLVED  
**Resolution:** `DefaultSignatureProvider` now mixes the port into the FNV-1a hash after the address bytes (`hash ^= (ulong)endPoint.Port; hash *= FnvPrime`). Two endpoints from the same IP but different ports now produce distinct signatures.

---

### V11 — NAT Session ID Space Too Small (6 Alphanumeric Characters)

**Severity:** RESOLVED  
**Resolution:** Session IDs are now generated exclusively by the server (`NatRequestSession` / `NatSessionCreated` flow). Clients no longer generate IDs, so a brute-force attacker cannot pre-compute or enumerate IDs before they exist. Each ID is live only until the peer joins or the 5-minute session timeout elapses. `ServerNatConfig.SessionId` and `GenerateSessionId()` have been removed from the client API. The 6-character length is unchanged but the server-generated model means the effective attack window per session is narrow — an attacker must guess the correct ID within the 5-minute window while also knowing the target session is active.

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

## Remaining Open Items

| # | Item | Severity |
|---|---|---|
| 13 | V13 — No ACK batching | LOW |
| 14 | V14 — Keep-alive rate not adaptive | LOW |