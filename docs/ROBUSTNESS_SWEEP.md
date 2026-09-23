# SynapseSocket — Robustness & Security Sweep

**Date:** 2026-08-17
**Scope:** Full sweep of `SynapseSocket`, `SynapseBeacon`, and the CodeBoost pooling primitives they depend on, across six requested axes: incorrect socket timeouts, memory-flooding vulnerabilities, socket identity spoofing, CPU-loop vulnerabilities, memory leaks, and general memory/GC/CPU performance.
**Status:** **Fixes applied and landed on `main`** (commit `3b5ffcc`, 2026-09-22). This document is the record of the sweep that produced them, not an open work list. It is written throughout in the present tense of 2026-08-17, when nothing had been fixed yet; read a finding's description as the state *before* that commit. Every finding's current state is in the status table immediately below.
**Method:** Manual trace of every hot path, plus a seven-dimension parallel audit with one adversarial verifier per top finding and a completeness critic. 67 raw findings → deduplicated and consolidated to the 54 below.

---

## Status of every finding

Added 2026-09-22, when the remaining findings were triaged one by one. Every ID below has a state; nothing is
left implicit. "Fixed, no test" means the fix was read in the current source and named here, not that it was
merely intended. Where a finding is genuinely not worth a test, or cannot be tested from this harness, the reason
is given rather than left blank.

| Status | Findings | Evidence |
|---|---|---|
| **Fixed, with a test** | C1, C3, C4, H1, H2, H3, H4, H5, H6, H7, H9, H10, H11, H12, H13, M1, M2, M3, M4, M5, M9, M12, M13, M14, M15, M16, M17, M18 | `SweepFindingTests`, plus `NatTraversalTests` for the NAT exchange (M9) |
| **Refuted, with a test** | C2 | Windows reports `Available == 1` for a queued zero-length datagram; see "C2 was wrong" below. Still unverified on Linux |
| **Fixed, no test** | M10, M11, M19, M20, M22, M23, M24 | M10 `RemoveExpiredProbeLimitEntries` from maintenance; M11 `Core/Clock.cs`; M19 the two HMAC instances are built once in the ingress constructor; M20 `ParkPendingReliable`; M22 `MixHandshakeNonce` folds at most `HandshakeNonceSize` bytes; M23 the sweep moved off the receive path into `RunMaintenance`; M24 the sweep is bounded by `MaximumConcurrentConnections` |
| **Accepted risk, by design** | H8, M6, M7, M8 | Datagram attribution is source-IP only and per-packet authentication is deliberately not paid for. Stated in `IngressEngine.ProcessPacket` at the point where it bites: damage is bounded to a held connection slot, and anyone able to forge there can already inject payloads as that peer |
| **Fixed, with a test** (second pass) | H11b/M21 (partly), L1 | `BeaconSecurityTests`. See the note below for what remains of H11b |
| **Open, not triaged** | L2, L3, L4, L5, L6, L7, L8, L9, L12, L13 | Low/info-severity performance and tidiness items: a socket handle held until finalization when `Bind` throws, two dictionary lookups per datagram, a double payload copy on segmented receive, a slower comparer on the send table, pooled-list rentals for the empty case, an extra `ioctl` per receive, an unreachable keep-alive backoff range, and `Interlocked` on telemetry counters in a single-threaded engine. None is a security property |

**H11b/M21, what is fixed and what is not.** The reflection vector is closed: a `JoinSession` is answered
with an address-bound cookie and nothing else until that cookie comes back, so a forged join can no longer
make a host aim a hole-punch burst at a third party. The heartbeat reflector is closed, and the server now
rate-limits per source address. What remains is that a joiner who *does* learn a valid session ID still
receives the host's endpoint: the ID is the only credential. Closing that needs a host-issued join secret
carried alongside the ID, which lengthens the shared room code and is therefore a product decision rather
than a fix.

**L1** was closed as a side effect of H5's per-poll receive budget, which is what its own suggested fix called
for. The secondary half, disabling `SIO_UDP_CONNRESET` on Windows so ICMP-induced errors never reach the
receive path, is not done.

Why some fixes carry no test: M11 needs the system clock stepped underneath a running engine; M20 needs the 16-bit
sequence space to wrap with an entry still unacknowledged; M19, M23 and M24 are allocation and cost properties
whose only honest assertion is a timing or allocation measurement that would be flaky in CI. Each was verified by
reading the current source, which is weaker evidence than a test, and is labelled as such rather than counted as
covered.

---

## Verification Status — READ THIS FIRST

This document originally shipped as **static analysis only**: code reading and hand-tracing, with nothing compiled or executed. The words "confirmed" and "verified" meant *confirmed by re-reading the source*, which overstated their weight.

Findings have since been **empirically tested** against a live engine over real loopback UDP sockets
(`SynapseSocket.Tests/Security/SweepFindingTests.cs`). Each test asserts the *correct* post-fix behaviour. The
results below are from before the fixes landed, which is why they read as failures: a failing test there was the
proof that the finding was real. All of them pass on `main` as of `3b5ffcc`.

| Finding | Test | Result |
|---|---|---|
| C1 (table/list divergence) | `C1_TwoRemovals_LeaveEndpointTableAndListConsistent` | **FAILS — confirmed.** `127.0.0.1:40004` resolved by endpoint but was absent from the maintenance list. |
| C1 (index desync) | `C1_ConnectionsIndex_MatchesListPositionAfterRemoval` | **FAILS — confirmed.** `41003` sat at list position 1 but reported `ConnectionsIndex` 2. |
| C3 (handshake reset loop) | `C3_HandshakeFromConnectedEndpoint_DoesNotResetAndEchoBack` | **FAILS — confirmed.** A second handshake from a Connected endpoint both reset the session and echoed a handshake back. |
| C4 (unbounded connections) | `C4_UnauthenticatedHandshakes_DoNotCreateUnboundedConnections` | **FAILS — confirmed.** 40 of 40 unauthenticated handshakes each created a live connection. |
| H1 (Pending wedge) | `H1_PendingConnection_TimesOutEvenWhileInboundDataArrives` | **FAILS — confirmed.** Timeout was 1000 ms; the connection survived 5000 ms of inbound data. |
| H2 (one-packet blacklist) | `H2_SingleOversizedDatagram_DoesNotPermanentlyBlacklistTheSender` | **FAILS — confirmed.** One 1401-byte datagram permanently barred the sender. |
| H4 (rate-limit false positive) | `H4_TwoLargeSendsInOneSecond_DoNotBlacklistALegitimatePeer` | **FAILS — confirmed.** Two 300 KB sends in one second tripped `RateLimitExceeded`. |
| **C2 (zero-length wedge)** | `C2_ZeroLengthDatagram_DoesNotWedgeTheReceiveDrain` | **PASSES — REFUTED on Windows.** See below. |

The pre-existing suite still passes in full (59/59), so none of this is a test-harness artefact.

**Everything not in that table remains static analysis only and has not been executed.** Treat those findings as
well-traced hypotheses, not established facts — particularly the pooled-buffer leak claims (H7, M1–M4), which
assert things about `ArrayPool` accounting that no test here observes.

### C2 was wrong

A direct probe of `FIONREAD` semantics settles it:

```
Available on empty socket:                          0
Available with ONE zero-length datagram queued:     1     ← not 0
Available with zero-length + 10-byte queued:        10
First ReceiveFrom returned:                         0 bytes (zero-length datagram dequeued normally)
```

Windows reports `Available == 1` for a queued zero-length datagram — an explicit guard against exactly the deadlock
I described. The drain loop proceeds, the datagram is dequeued, and the engine keeps receiving. **C2 does not
reproduce on Windows at all**, not even as a transient stall.

My Linux claim (that `SIOCINQ` returns the first datagram's length, hence 0, hence a permanent wedge) was reasoned
from documentation and **remains untested**. Given that Windows deliberately special-cases this hazard, I would not
assume Linux does not. C2 is therefore demoted from CRITICAL to **unverified**, and should not be actioned until
the test above is run on Linux. It is kept in §1 for that purpose only.

Corrected severity counts: **3 critical** (C1, C3, C4), 13 high, 23 medium, 15 low/info/unverified.

---

## Severity Legend

- **CRITICAL** — remotely triggerable with no authentication, or silently corrupts engine state. Fix before shipping.
- **HIGH** — remotely triggerable denial of service, permanent lockout of legitimate peers, or unbounded growth.
- **MEDIUM** — real defect with a narrower trigger, a non-default config requirement, or bounded blast radius.
- **LOW** — correctness/efficiency issue with minor practical impact.
- **INFO** — observation, stale documentation, or dead code.

Throughout: **"default config"** means stock `SynapseConfig` — MTU 1200, `MaximumPacketSize` 1400, `Security.Enabled = true`, `MaximumPacketsPerSecond` 500, `MaximumBytesPerSecond` 2 MiB, `MaximumConcurrentConnections` 0 (**unlimited**), `MaximumReassembledPacketSize` 0 (**disabled**), `MaximumSegments` 0 (**→ 255**), timeout 15 s, keep-alive 5 s.

---

## Summary

### Critical

| # | Finding | File | Axis |
|---|---------|------|------|
| C1 | Swap-remove deletes the element it just swapped in, desyncing every `ConnectionsIndex` | `Connections/ConnectionManager.cs:171` | timeout / leak / cpu | **tested — confirmed** |
| C3 | One forged handshake starts an endless mutual reset loop between two live peers | `Transport/IngressEngine.cs:778` | spoofing | **tested — confirmed** |
| C4 | Connection cap disabled by default + zero pre-connection rate limiting | `Transport/IngressEngine.cs:739`, `Security/SecurityProvider.cs:129` | memory flood | **tested — confirmed** |
| ~~C2~~ | ~~One zero-length UDP datagram wedges the receive drain~~ | `Transport/IngressEngine.cs:271` | — | **REFUTED on Windows; untested on Linux** |

### High

| # | Finding | File | Axis |
|---|---------|------|------|
| H1 | Lost handshake reply wedges a connection in `Pending` forever; neither side ever times out | `Transport/IngressEngine.cs:515` | timeout |
| H2 | One spoofed datagram permanently blacklists any endpoint | `Transport/IngressEngine.cs:402` | spoofing |
| H3 | Blacklist is unbounded and has no TTL | `Security/SecurityProvider.cs:22` | memory flood |
| H4 | A single large send costs 255 packets, so normal traffic trips the rate limit and blacklists a legitimate peer | `Connections/SynapseConnection.Security.cs:38` | timeout |
| H5 | `Drain()` has no per-poll datagram budget — a flood livelocks `Poll()` | `Transport/IngressEngine.cs:259` | cpu |
| H6 | Handshake replay cache accepts unlimited attacker-minted keys; O(n) sweep runs inline on the flooded path | `Transport/IngressEngine.cs:752` | memory flood / cpu |
| H7 | Remote `Disconnect` packet tears down a connection without running any cleanup helper | `Transport/IngressEngine.cs:519` | leak / spoofing |
| H8 | Datagram attribution is source-IP only — no per-connection secret exists | `Transport/IngressEngine.cs:369` | spoofing |
| H9 | Beacon heartbeat task drives the single-threaded engine from a threadpool thread | `SynapseBeacon/Client/BeaconHostSession.cs:61` | cpu / corruption |
| H10 | `BeaconClient` trusts any datagram whose source claims to be the beacon server | `SynapseBeacon/Client/BeaconClient.cs:179` | spoofing |
| H11 | Beacon `JoinSession` is unauthenticated over a 900,000-value ID space; ID generator can spin forever | `SynapseBeacon/Server/BeaconSessionRegistry.cs:100` | spoofing / memory flood / cpu |
| H12 | Malformed header forces a managed exception throw/catch per packet on an unrate-limited path | `Transport/IngressEngine.cs:483` | cpu / gc |
| H13 | Unity/Mono build allocates 4–6 objects per received datagram | `Transport/IngressEngine.cs:292` | gc-perf |

### Medium

| # | Finding | File | Axis |
|---|---------|------|------|
| M1 | Timeout teardown omits `ReturnConnectionSegmenters` | `Core/SynapseManager.Maintenance.cs:141` | leak |
| M2 | `CreateNew` discards the replaced connection without reclaiming its buffers | `Connections/ConnectionManager.cs:97` | leak |
| M3 | `SynapseConnection` is rented from the pool but **never returned** — `OnReturn` is dead code | `Connections/ConnectionManager.cs:64` | leak / gc |
| M4 | Unreliable segmented send never returns the `ListPool` list | `Transport/TransmissionEngine.cs:245` | leak |
| M5 | Reorder-buffer entries never age out; the cap vanishes entirely when security is off | `Transport/IngressEngine.cs:695` | memory flood |
| M6 | Forged ACK silently dequeues a pending reliable packet | `Transport/IngressEngine.cs:529` | spoofing |
| M7 | Forged reliable sequence desyncs `NextExpectedSequence` | `Transport/IngressEngine.cs:665` | spoofing |
| M8 | Forged 1-byte KeepAlive prevents a connection from ever timing out | `Transport/IngressEngine.cs:526` | spoofing / timeout |
| M9 | NAT challenge echo has no origin binding or hop limit | `Transport/IngressEngine.Nat.cs:108` | spoofing |
| M10 | NAT probe table grows per spoofed IP; the challenge path never runs eviction | `Transport/IngressEngine.Nat.cs:98` | memory flood |
| M11 | All timeout scheduling reads the wall clock — an NTP step causes mass disconnects | `Core/SynapseManager.Maintenance.cs:137` | timeout |
| M12 | Two bind endpoints of the same family send every reply out the wrong socket | `Core/SynapseManager.cs:220` | timeout |
| M13 | Reassembly size precheck is disabled by default and its bound is unsound | `Transport/IngressEngine.cs:548` | memory flood |
| M14 | A throwing `PacketReceived` subscriber leaks the pooled list and every undelivered payload | `Transport/IngressEngine.cs:712` | leak |
| M15 | `LatencySimulator.Flush` double-returns buffers if a send throws mid-drain | `Diagnostics/LatencySimulator.cs:130` | leak / corruption |
| M16 | ACK "batching" never coalesces — one datagram and one syscall per ack | `Connections/SynapseConnection.cs:158` | cpu |
| M17 | `Connect()` sends one un-retried handshake | `Core/SynapseManager.cs:313` | timeout |
| M18 | One lost segment retransmits all 255 segments, up to 10 times | `Core/SynapseManager.Maintenance.cs:241` | cpu / bandwidth |
| M19 | `HMACSHA256` is allocated per NAT token computation | `Transport/IngressEngine.Nat.cs:132` | gc-perf |
| M20 | `PendingReliableQueue` indexer overwrite orphans buffers on sequence wrap | `Transport/TransmissionEngine.cs:190` | leak |

### Low / Info

| # | Finding | File |
|---|---------|------|
| L1 | Catch-all `continue` in the drain loop has no forward-progress guarantee | `Transport/IngressEngine.cs:311` |
| L2 | Sockets whose `Bind` throws are never disposed | `Core/SynapseManager.cs:209` |
| L3 | Every datagram resolves its connection twice on the NET8 drain | `Transport/IngressEngine.cs:369` |
| L4 | Segmented receive rents and memcpys the payload twice | `Transport/IngressEngine.cs:575` |
| L5 | The one endpoint-keyed dictionary on the send path skips `IPEndPointComparer` | `Transport/TransmissionEngine.cs:67` |
| L6 | `DeliverOrdered` rents a pooled list for the 1-element common case | `Transport/IngressEngine.cs:673` |
| L7 | `RemoveExpiredSegments` locks and rents per connection per `Poll` to usually do nothing | `Packets/PacketReassembler.cs:122` |
| L8 | `Socket.Available` before every receive doubles the syscall count | `Transport/IngressEngine.cs:271` |
| L9 | Keep-alive backoff can suppress the last heartbeat inside the timeout window | `Core/SynapseManager.Maintenance.cs:162` |
| L10 | NAT token comparison is not constant-time | `Transport/IngressEngine.Nat.cs:146` |
| L11 | Dead code: unused `EndPointKey` struct | `Connections/ConnectionManager.cs:205` |
| L12 | `Telemetry` uses `Interlocked` on a documented single-threaded engine | `Diagnostics/Telemetry.cs:81` |
| L13 | `Dictionary<ushort,…>` where a bounded ring buffer fits the sequence space exactly | `Connections/SynapseConnection.cs:101` |
| L14 | Four stale XML/comment claims contradicted by the code | various — see §7 |

---

## 1. Sockets Incorrectly Timing Out

### C1 — Swap-remove deletes the element it just swapped in

**File:** `SynapseSocket/Connections/ConnectionManager.cs:171` (and the same defect at `:113` in `CreateNew`)
**Severity:** CRITICAL

```csharp
if (connectionsIndex < lastConnectionsIndex)
{
    SynapseConnection otherConnection = _connections[lastConnectionsIndex];
    otherConnection.ConnectionsIndex = connectionsIndex;

    _connections[connectionsIndex] = otherConnection;      // tail written into the freed slot
    removedSynapseConnection.ConnectionsIndex = SynapseConnection.UnsetConnectionsIndex;
}

_connections.RemoveAt(connectionsIndex);                   // ← removes the tail we just wrote
```

**Issue:**
The swap moves the tail entry into the freed slot, and then `RemoveAt(connectionsIndex)` removes that same slot — undoing the swap. List *membership* still comes out correct (it degenerates to a shifting `RemoveAt`), but every `ConnectionsIndex` field from the removal point onward is now wrong, because the code already rewrote them as though a swap had happened.

Traced with `[C0, C1, C2, C3]`:

1. `Remove(C1)` → writes C3 into slot 1 (`C3.ConnectionsIndex = 1`), then `RemoveAt(1)` → list is `[C0, C2, C3]`.
   C2 sits at position 1 but reports index 2. C3 sits at position 2 but reports index 1.
2. `Remove(C2)` → reads the stale index 2; `lastConnectionsIndex` is also 2, so the swap branch is skipped; `RemoveAt(2)` **removes C3**.

Net result: **C2 is gone from the lookup dictionaries but still sits in `_connections`; C3 is gone from `_connections` but still resolves in both dictionaries.**

**Impact:**
`RunMaintenance` and `FlushPendingAcks` iterate `Connections.Connections` — the list — so the orphaned C3 is never visited again:

- no timeout check (`Maintenance.cs:137`) — it never times out locally, and lives until `Stop()`/`Dispose()`;
- no keep-alive emission (`:175`) — the **remote** peer times *it* out after 15 s, producing a one-way disconnect the local host never observes;
- no `RetransmitReliable`, no `TimeoutAssembledSegments`;
- no `ResetInboundRateCounters` — so `_receivedByPacketCount` accumulates without bound and crosses the 500 pps default after ~500 total packets (≈17 s at 30 pps), producing a bogus `FilterResult.RateLimited` whose default action is `KickAndBlacklist`. **The peer's IP is blacklisted for a rate it never exceeded.**

Meanwhile `IngressEngine.HandleDatagram` still resolves C3 by endpoint, so it keeps receiving. The C2 remnant stays in `_connections` forever, and its next `Remove` uses a stale index that can cascade into another wrong removal or an `ArgumentOutOfRangeException` swallowed by `HandleViolation`'s bare `catch` (`SynapseManager.cs:450`).

This one defect is simultaneously the top finding on three of the six requested axes.

**Suggested fix:**

```csharp
_connections.RemoveAt(lastConnectionsIndex);   // correct in both the swap and the tail case
```

and move `removedSynapseConnection.ConnectionsIndex = UnsetConnectionsIndex;` out of the `if` so it always runs. Apply the same change at `ConnectionManager.cs:113`. Note that a correct swap-remove-last pairs naturally with *forward* iteration; `RunMaintenance`'s backward loop will then skip the swapped-in entry for one sweep — harmless, but worth acknowledging in a comment or flipping the loop.

---

### C2 — One zero-length UDP datagram wedges the receive drain

**File:** `SynapseSocket/Transport/IngressEngine.cs:271`
**Severity:** CRITICAL

```csharp
if (_socket.Available == 0)
    break;
```

**Issue:**
`Socket.Available` is `ioctl(FIONREAD)`. A zero-payload UDP datagram is perfectly legal on the wire and contributes **zero bytes** to that count.

- **On Linux/BSD**, `FIONREAD` on a datagram socket reports the length of the *first queued datagram*. With a zero-length datagram at the head of the queue, `Available` returns 0 **forever**: the `break` fires on every subsequent `Poll()`, the receive call is never reached, the datagram is never dequeued, and every legitimate datagram queued behind it is unreachable. The transport goes permanently deaf.
- **On Windows**, `FIONREAD` reports total queued payload bytes, so the stall lasts only until the next non-empty datagram arrives and unblocks the loop. A *sustained* flood of zero-length datagrams keeps `Available` pinned at 0 while still consuming `SO_RCVBUF` accounting (raised to 1 MiB at `SynapseManager.cs:203`), so the kernel queue fills and drops real traffic.

Dedicated servers for this engine typically run Linux, so the permanent-wedge variant is the realistic production case.

The `packetLength <= 0` guard in `SecurityProvider` (`:105`, `:142`) does not help — it is downstream of the drain and is never reached, because the datagram is never read at all.

**Trigger:** default config, one unauthenticated UDP datagram with a zero-byte payload sent to the bound port from any (spoofable) source. No handshake or connection required.

**Impact:** total receive denial of service from a single packet. `Poll()` keeps running, so `PerformKeepAlive` sees `LastReceivedTicks` go stale and times out every connected peer after 15 s. The process stays alive but can never receive again; only rebinding the socket recovers it.

**Suggested fix:** stop using `Available` as the loop condition. Put the socket in non-blocking mode and loop on the receive itself, treating `SocketError.WouldBlock` as the exit condition — that dequeues zero-length datagrams normally and removes a syscall per datagram (see L8). Combine with the per-poll budget from H5.

---

### H1 — A lost handshake reply wedges a connection in `Pending` forever

**File:** `SynapseSocket/Transport/IngressEngine.cs:515` (and `:426` on the fast path)
**Severity:** HIGH

**Issue:**
`Connect()` sends exactly one handshake (`SynapseManager.cs:313`) and leaves the connection `Pending`. If the peer's handshake *reply* is lost, side A stays `Pending` forever — nothing re-sends: `Connect` is one-shot, `AdvanceNatPunches` only re-sends in FullCone mode, and `ProcessHandshake` only replies when `!isExistingConnection || wasConnected`.

Side B already considers itself `Connected` and starts sending data. Every one of B's datagrams stamps `LastReceivedTicks` with **no `ConnectionState` guard**, so A's timeout check never fires. `PerformKeepAlive` only short-circuits on `State == Disconnected`, so A keeps emitting keep-alives from a `Pending` connection — which refreshes B's `LastReceivedTicks` and stops B timing out too. **Both sides are pinned indefinitely.**

**Impact:**
A permanently split-brain session that never self-heals and never times out on either side. A's application never receives `ConnectionEstablished`, yet its `PacketReceived` handler *is* invoked with a connection whose `State` is `Pending`, so any host logic keyed on the established event never runs. A's reliable channel is dead: `RetransmitReliable` requires `State == Connected`, so reliable sends accumulate un-retransmitted until `MaximumPending` (256), after which `Send` throws `InvalidOperationException` on every call forever, with 256 `ArrayPool` rentals pinned.

Also reachable deliberately: an off-path spoofer sending one 1-byte `0x00` datagram from B's address every <15 s.

**Suggested fix:** only stamp `LastReceivedTicks` for `Connected` connections, or add a separate, shorter handshake timeout for `Pending` connections. Retry the initial handshake (see M17).

---

### H4 — The pps and byte caps contradict each other, and the response is permanent

**File:** `SynapseSocket/Connections/SynapseConnection.Security.cs:38`, `SynapseSocket/Security/SecurityProvider.cs:112`
**Severity:** MEDIUM (downgraded from HIGH after testing — see below)

**Correction.** This finding originally claimed that "two large sends in one second is legitimate traffic" and was
therefore a false positive. That framing was wrong: ~600 KB/s to a single peer is genuinely heavy for a realtime
game transport, and rate-limiting it is a defensible thing for the engine to do. What testing did establish is a
narrower and more objective problem.

**The caps are not calibrated to each other.** `SecurityConfig` documents `MaximumBytesPerSecond = 2 MiB` as
allowing "comfortable legitimate headroom". That allowance is unreachable:

| | |
|---|---|
| Documented byte allowance | 2,097,152 B/s |
| Packets needed at MTU 1200 | 1,747 pps |
| Actual pps cap | **500** |
| Real bandwidth ceiling | 500 × 1200 = **600,000 B/s ≈ 0.57 MiB/s** |

The pps cap binds **~3.5× earlier** than the byte cap, so the byte cap is effectively dead configuration for
MTU-sized traffic and the real ceiling is a third of what the config says. Verified:
`H4a_TrafficWellUnderTheDocumentedByteAllowance_IsNotRateLimited` **fails** — traffic at ~1 MiB/s, half the
documented allowance, is rate limited.

**The response is disproportionate regardless of what the right rate is.** Exceeding either cap returns
`FilterResult.RateLimited`, whose default action is `KickAndBlacklist` — the peer is disconnected *and* its
IP-derived signature is added to a blacklist with **no TTL** (H3). A transient excursion is a permanent ban for
the process lifetime. Rate limiting normally sheds load; it should not be a lockout.

**What did NOT reproduce.** I hypothesised that M18's whole-message retransmit storm would trip the cap by itself,
self-inflicting a ban during ordinary operation.
`H4b_OneLargeReliableSendUnderPacketLoss_DoesNotSelfInflictABlacklist` **passes** at 2 % loss on a 200 KB reliable
send — one retransmit round appears to fill the gaps before the counter crosses 500. That chain is unproven and
should not be cited.

**Suggested fix:** calibrate the two caps against each other (or derive the pps cap from the byte cap and the MTU)
and correct the `MaximumBytesPerSecond` doc comment, which currently describes headroom the engine will not give.
Separately, default rate-limit violations to `ViolationAction.Drop` and reserve blacklisting for repeated
violations across multiple windows.

---

### M11 — Wall-clock time drives all timeout scheduling

**File:** `SynapseSocket/Core/SynapseManager.Maintenance.cs:137`
**Severity:** MEDIUM

Every timestamp in the engine is `DateTime.UtcNow.Ticks`. A backward NTP step (or a user changing the system clock) makes `nowTicks - LastReceivedTicks` negative, which blocks timeout detection until the clock catches up; a forward step larger than `TimeoutMilliseconds` **disconnects every connection at once**. On mobile and console targets, clock steps after sleep/resume are routine.

**Suggested fix:** use a monotonic source — `Stopwatch.GetTimestamp()` — for all interval arithmetic (timeout, keep-alive, resend, assembly timeout, rate-counter windows), keeping wall-clock only for anything user-facing.

**Related:** the same lack of a clamp means a long frame hitch (Unity editor pause, alt-tab, level load) produces a `Poll` whose `nowTicks` delta exceeds the timeout and disconnects every peer simultaneously. Consider clamping the observed delta per `Poll` to a maximum plausible frame time.

---

### M12 — Two bind endpoints of the same family send every reply out the wrong socket

**File:** `SynapseSocket/Core/SynapseManager.cs:217-220`
**Severity:** MEDIUM

```csharp
if (bindEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
    ipv6Socket = socket;
else
    ipv4Socket = socket;
```

The loop overwrites on each iteration, so binding two IPv4 endpoints leaves `ipv4Socket` pointing at the **last** one. The single shared `TransmissionEngine` then sends every reply — handshake acks, ACKs, keep-alives, payloads — out of that socket regardless of which socket received the datagram. Peers that connected via the first endpoint see replies from an unexpected source address and, behind NAT, will not receive them at all, so the connection times out.

**Suggested fix:** either reject multi-endpoint binds of the same address family with a clear exception, or give each ingress engine its own transmission engine bound to its own socket.

---

### M17 — `Connect()` sends one un-retried handshake

**File:** `SynapseSocket/Core/SynapseManager.cs:313`
**Severity:** MEDIUM

A single dropped handshake datagram means the connect attempt silently never completes (see H1 for the full wedge). There is no retry and no connect timeout outside FullCone NAT mode. A failed attempt that is later torn down also surfaces as `ConnectionClosed` rather than `ConnectionFailed`.

**Suggested fix:** retry the handshake on the maintenance sweep while `State == Pending`, with a bounded attempt count that raises `ConnectionFailed` on exhaustion.

---

### L9 — Keep-alive backoff can suppress the last heartbeat inside the timeout window

**File:** `SynapseSocket/Core/SynapseManager.Maintenance.cs:162`

`_connectionKeepAliveTicks << Math.Min(UnansweredKeepAlives, 3)` reaches 8 × 5 s = 40 s, well past the 15 s timeout. In practice the connection times out before the backoff matters, so the backoff's upper range is unreachable dead behaviour rather than a live defect — but the two values should be validated against each other at construction.

---

## 2. Memory Flooding Vulnerabilities

The single most important structural point: **`SecurityProvider.InspectNew` performs no rate limiting at all.**

```csharp
public FilterResult InspectNew(IPEndPoint endPoint, int packetLength, out ulong signature)
{
    if (!SignatureProvider.TryCompute(...)) { ... return FilterResult.SignatureFailure; }
    if (_blacklist.ContainsKey(signature)) return FilterResult.Blacklisted;
    if (packetLength <= 0 || packetLength > _maximumPacketSize) return FilterResult.Oversized;
    return FilterResult.Allowed;
}
```

Its own XML doc claims it "delegates to `InspectEstablished` for size and rate-limit enforcement" — it does not. Rate limiting exists **only** for already-established connections, and the counters live on the `SynapseConnection` object, so by construction there is nowhere to charge an unknown sender. Every pre-connection path — handshake, NAT probe, NAT challenge, unknown-packet — is therefore completely unthrottled, and that is the common precondition for C4, H3, H6, and M10 below.

### C4 — Unlimited connections by default, created by unauthenticated spoofed handshakes

**File:** `SynapseSocket/Transport/IngressEngine.cs:739`; `SynapseConfig.MaximumConcurrentConnections = 0`
**Severity:** CRITICAL

`MaximumConcurrentConnections` defaults to `0`, which the engine converts to `uint.MaxValue` — **no cap**. Combined with the absence of any pre-connection rate limit, a single 1-byte handshake from a forged source address:

1. computes a signature and passes the (empty) blacklist check,
2. passes the non-existent connection cap,
3. inserts an entry into `_seenHandshakes`,
4. calls `ConnectionManager.GetOrAdd`, which **rents a `SynapseConnection`** and inserts it into three lookup tables plus the list,
5. causes the engine to **send a 9-byte handshake-ack to the forged source** (a ~9× reflection amplifier).

Each connection object carries a `Dictionary`, a `Queue`, and a second `Dictionary`, and — because connections are never returned to the pool (M3) — none of it is recycled. Every one of them is then walked by `RunMaintenance` and `FlushPendingAcks` on **every** `Poll` (see H5/§4), so the flood converts directly into per-frame CPU cost that never subsides until the entries time out 15 s later.

**Suggested fix:** ship a non-zero default `MaximumConcurrentConnections`. Add a pre-connection rate limiter keyed on source IP (not IP:port — see H3) with a bounded, LRU-evicted table. Ideally, do not allocate connection state at all until the peer has proven return-routability, e.g. with a stateless SYN-cookie-style handshake: respond to the first handshake with an HMAC token bound to the source and a time bucket (the NAT path at `IngressEngine.Nat.cs:115` already implements exactly this pattern) and only create a connection when the token comes back.

### H3 — Blacklist is unbounded and has no TTL

**File:** `SynapseSocket/Security/SecurityProvider.cs:22`
**Severity:** HIGH

`_blacklist` is a `ConcurrentDictionary<ulong, byte>` that is only ever added to. There is no eviction, no expiry, and no size cap; `RemoveFromBlacklist` exists but nothing calls it automatically.

Because the default signature is `FNV-1a(address bytes ‖ port)`, the key includes the **port**, so a single attacker IP mints a fresh blacklist entry for each of 65,535 source ports — and with spoofing, for the entire address space. Every entry is 16+ bytes of permanent growth, and `IsBlacklisted` is on the per-datagram path for unknown senders.

The permanence is also what makes H2 and H4 so damaging: once blacklisted for any reason, a peer is locked out for the process lifetime.

**Suggested fix:** bound the blacklist with a TTL (evicted on the maintenance sweep) and a hard size cap with LRU eviction. Consider keying the blacklist on address only, while keeping the connection signature on address+port, so that blacklisting is not trivially diluted by port rotation.

### H6 — Handshake replay cache accepts unlimited attacker-minted keys

**File:** `SynapseSocket/Transport/IngressEngine.cs:752`, sweep at `:858`
**Severity:** HIGH

The replay key is `MixHandshakeNonce(signature, handshakePayload)` — an FNV-1a fold of the entire attacker-controlled handshake payload into the signature. Every distinct payload produces a distinct key, so an attacker mints unlimited entries at line rate against an unrate-limited path.

Eviction is worse than it looks: it runs **at most once per minute**, is triggered **only from inside `ProcessHandshake` itself** (the very path being flooded), and is a full O(n) scan of the dictionary performed inline on the receive path inside `Poll()` (see §4). Entries live for `2 × TimeoutMilliseconds` (30 s) but can only be removed on a sweep that fires once a minute, so the steady-state footprint is at least a minute of flood.

**Suggested fix:** cap the replay cache with LRU eviction, drive the sweep from `RunMaintenance` rather than from the flooded path, and hash a bounded prefix of the payload rather than all of it (also fixes the CPU cost noted at M22/§4).

### M5 — Reorder-buffer entries never age out; the cap vanishes when security is off

**File:** `SynapseSocket/Transport/IngressEngine.cs:695`, `:704`
**Severity:** MEDIUM

```csharp
if (_isSecurityEnabled && synapseConnection.ReorderBuffer.Count >= _effectiveMaximumOutOfOrderReliablePackets)
```

Two problems:

1. The cap is gated on `_isSecurityEnabled`. With `Security.Enabled = false` the reorder buffer is **completely unbounded** — the half-space sequence check admits sequences up to 32,767 ahead, so a peer can pin 32,767 pooled payload buffers on one connection (≈39 MB at MTU) simply by never sending the missing sequence.
2. Even with the default cap of 64, there is **no time-based eviction at all**. A peer that opens a permanent gap holds 64 pooled buffers out of `ArrayPool` for the entire connection lifetime. The same applies to a reassembled reliable-segmented payload filed under an attacker-chosen sequence (`:583`) — up to 304 KB parked per entry.

**Suggested fix:** apply the cap unconditionally (a bound on remote-controlled memory is not a "security feature" that should be switchable), and add an age-based eviction of reorder entries on the maintenance sweep, mirroring `RemoveExpiredSegments`.

### M13 — Reassembly size precheck is disabled by default and its bound is unsound

**File:** `SynapseSocket/Transport/IngressEngine.cs:548`
**Severity:** MEDIUM

```csharp
if (_isSecurityEnabled && segmentCount * _effectiveMaximumTransmissionUnit > _effectiveMaximumReassembledPacketSize)
```

`MaximumReassembledPacketSize` defaults to `0` → converted to `uint.MaxValue`, so **this check never fires under stock configuration**. When it *is* enabled, the bound is computed from `MaximumTransmissionUnit` (1200) but the per-segment length actually accepted is bounded by `MaximumPacketSize` (1400) — and by nothing at all when security is disabled. A peer can therefore declare a segment count that passes the precheck while sending segments larger than the MTU, so the assembled payload exceeds the configured cap. `SegmentAssembly.Add` copies `segmentData.Length` with no MTU check of its own.

`segmentCount * _effectiveMaximumTransmissionUnit` is also `byte * uint` in unchecked `uint` arithmetic; a misconfigured MTU above ~16.8 M would wrap.

**Suggested fix:** compute the bound from `MaximumPacketSize`, not the MTU; validate each arriving segment's length against the MTU in `TryReassemble`; ship a sane non-zero default; and widen the multiplication to `ulong`.

### M10 — NAT probe table grows per spoofed IP and the challenge path never evicts

**File:** `SynapseSocket/Transport/IngressEngine.Nat.cs:98` (insert), `:55` (sweep, probe path only)
**Severity:** MEDIUM (NAT traversal is disabled by default)

`_natProbeLastResponseTicks` gets one entry per distinct source IP. `ProcessNatProbe` runs the once-a-minute eviction sweep; `ProcessNatChallengeExchange` **inserts into the same table but never runs the sweep**. An attacker sending only `NatChallenge` packets from spoofed sources therefore grows the table with no eviction whatsoever. Note also that `GetOrAdd(addressKey, 0L)` inserts an entry for *every* probing address before the rate check, so even rate-limited sources are recorded.

**Suggested fix:** drive this eviction from `RunMaintenance`, cap the table size, and use `TryGetValue` rather than `GetOrAdd` so a rate-limited source does not create an entry.

### H11 — Beacon session registry: unbounded by default, and the ID generator can spin forever

**File:** `SynapseBeacon/Server/BeaconSessionRegistry.cs:100`
**Severity:** HIGH

```csharp
while (true)
{
    uint candidate = GenerateId();          // ThreadRandom.Next(100000, 1000000)
    if (_sessions.TryAdd(candidate, entry)) { sessionId = candidate; return true; }
}
```

`HandleRequestSession` creates a session for **any** datagram whose first byte is `RequestSession`, with no authentication and no rate limit. `BeaconServer.UnlimitedConcurrentSessions` (`0`) is an exposed, documented option, and under it there is no cap.

The ID space is only **900,000 values** (`[100000, 1000000)`), and sessions live for `DefaultSessionTimeoutMilliseconds` = **300 s** without a heartbeat. An attacker can create 900,000 sessions in far less than 300 s (≈900k small datagrams), at which point `TryCreateSession` **never terminates** — the receive loop hangs forever. Long before full saturation the loop degrades catastrophically: expected iterations are `1/(1-load)`, so ≈10 at 90 % occupancy and ≈1000 at 99.9 %.

**Suggested fix:** bound the retry loop (fail after N attempts and return `ServerAtCapacity`), widen the ID space to the full `uint` range, refuse to run with an unlimited cap, and rate-limit `RequestSession` per source address.

---

## 3. Socket Identity Spoofing

### H8 — Datagram attribution is source-IP only (umbrella finding)

**File:** `SynapseSocket/Transport/IngressEngine.cs:369`
**Severity:** HIGH

```csharp
bool isEstablished = _connections.ConnectionsByEndPoint.TryGetValue(fromEndPoint, out SynapseConnection? synapseConnection);
```

There is **no per-connection secret anywhere in the wire format**. The `Signature` is `FNV-1a(address ‖ port)` — a pure function of the endpoint, computed identically by anyone, never transmitted, and never verified. It is an identifier, not a credential. Once a connection exists, the sole requirement for a datagram to be accepted as coming from that peer is that its source address matches, which an off-path attacker forges freely.

Everything in this section follows from that. The individually-actionable consequences:

| Forged packet | Effect |
|---|---|
| any payload | delivered to the application as trusted peer data |
| `Handshake` | **C3** — endless mutual reset loop |
| `Disconnect` (1 byte) | **H7** — terminates a live session instantly |
| oversized / malformed / unknown-type | **H2** — victim kicked and permanently blacklisted |
| `Ack` | **M6** — silent reliable data loss |
| `Reliable` with chosen sequence | **M7** — sequence desync, genuine packets discarded |
| `KeepAlive` (1 byte) | **M8** — connection never times out |

**Suggested fix:** this is the one architectural change on the list. Establish a shared secret during the handshake and carry a short per-packet MAC (or at minimum a random 32/64-bit connection token) on every datagram, validating it before the packet touches connection state. Without this, the mitigations below are individually worthwhile but none of them close the class.

### C3 — One forged handshake starts an endless mutual reset loop

**File:** `SynapseSocket/Transport/IngressEngine.cs:778-806`
**Severity:** CRITICAL

`ProcessHandshake` handles a handshake arriving from an endpoint that is **already `Connected`** by treating it as a forced reconnect:

```csharp
bool wasConnected = isExistingConnection && synapseConnection.State == ConnectionState.Connected;

if (wasConnected)
{
    synapseConnection.ResetForReconnect();     // wipes sequences, reorder buffer, pending reliables
    ConnectionClosed?.Invoke(synapseConnection);
}
...
if (!isExistingConnection || wasConnected)
    _sender.SendHandshake(fromEndPoint);       // ← and replies with a handshake
```

Trace one forged handshake sent to peer A with peer B's source address, while A and B are connected:

1. A sees a handshake from B while `Connected` → `wasConnected` → resets its session state → **sends a real handshake to B**.
2. B receives a genuine handshake from A while B is `Connected` → `wasConnected` → resets → **sends a handshake back to A**.
3. A is `Connected` again (set at `:793`) → `wasConnected` → resets → replies. Go to 2.

The replay cache does not break the cycle: `SendHandshake` writes a fresh 8-byte cryptographic nonce into every handshake, so each one is a distinct replay key.

**Impact:** a single forged 1-byte-header datagram puts two legitimate peers into a permanent handshake ping-pong. Each iteration wipes both sequence spaces, drops the entire reorder buffer, and drains every pending reliable packet, so the session never carries application data again. It is self-sustaining — the attacker sends one packet and walks away.

**Suggested fix:** never let an inbound handshake reset an already-`Connected` session without proof of origin. At minimum, require the reconnect handshake to echo a token issued to that connection at establishment; and unconditionally suppress the handshake *reply* when the connection was already `Connected`, which alone breaks the loop.

### H2 — One spoofed datagram permanently blacklists any endpoint

**File:** `SynapseSocket/Transport/IngressEngine.cs:402` (and `:458`, `:489`, `:542`, `:551`, `:588`, `:700`)
**Severity:** HIGH

Nearly every violation path defaults to `ViolationAction.KickAndBlacklist`, and the blacklist key is the IP-derived signature of the **source address on the datagram** — which the attacker chose. With `MaximumPacketSize` = 1400, a single spoofed 1401-byte datagram bearing a victim's source address is enough:

`InspectEstablished` → `FilterResult.Oversized` → `ViolationOccurred(victimEndpoint, victimSignature, …, KickAndBlacklist)` → the victim is disconnected and its signature added to a blacklist that has no TTL (H3).

The same one-packet outcome is reachable via a malformed header, an unknown packet type, a segment-count mismatch, or a reorder-buffer overflow. It works against endpoints that have never connected, too — pre-emptively banning a peer before it ever handshakes.

**Suggested fix:** never blacklist on evidence that consists solely of an unauthenticated datagram's source address. Require a violation threshold over time before blacklisting, default single-packet violations to `Drop`, and give blacklist entries a TTL.

### H10 — `BeaconClient` trusts any datagram claiming to be the beacon server

**File:** `SynapseBeacon/Client/BeaconClient.cs:179`
**Severity:** HIGH

The client accepts `PeerReady` (and other beacon replies) based only on the UDP source address matching the configured server endpoint. Because `Security.AllowUnknownPackets` must be enabled for the beacon to piggyback on the Synapse socket, these datagrams reach the client's handler ahead of any Synapse validation. A forged `PeerReady` redirects the joiner's hole-punch and subsequent `Connect` at an attacker-chosen endpoint.

**Suggested fix:** authenticate beacon replies — at minimum, bind them to a client-generated nonce sent in the corresponding request, so an off-path attacker cannot produce a valid reply.

### H11b / M21 — Beacon `JoinSession` is unauthenticated over a guessable ID space

**File:** `SynapseBeacon/Server/BeaconServer.cs:178-196`
**Severity:** HIGH

Session IDs come from `System.Random` seeded with `Environment.TickCount * 397 ^ ThreadId` — not cryptographic, and drawn from only 900,000 values. `HandleRegister` accepts any `JoinSession` naming a valid ID and responds by **disclosing the host's external IP:port to the joiner** and the joiner's endpoint to the host.

Consequences:

- **Enumeration.** `SendSessionNotFound` versus `PeerReady` is a clean oracle distinguishing valid IDs. 900,000 IDs is trivially sweepable, harvesting every host's endpoint.
- **Reflection / targeted punch.** The joiner's identity is taken from the (spoofable) UDP source, so a forged `JoinSession` makes every host in the registry fire a hole-punch burst at a victim of the attacker's choosing.
- `HandleHeartbeat` replies with a `HeartbeatAck` to **any** sender, whether or not the session exists or the sender is its host — another unauthenticated response to a forged source.

Note the XML on `HandleRegister` claims unknown sessions are "silently dropped so that rejected joiners cannot clog the server with retry-driven responses", but the code calls `SendSessionNotFound(from)` — the doc describes the safer behaviour that was intended.

**Suggested fix:** generate session IDs with `RandomNumberGenerator` over the full `uint` range; require a host-issued join secret alongside the session ID; drop the `SessionNotFound` reply (as the doc already claims) to remove the oracle; and rate-limit per source address.

### M6 / M7 / M8 — Forged ACK, sequence, and keep-alive

| | |
|---|---|
| **M6** `IngressEngine.cs:529` | A forged `Ack` for a guessed sequence removes the matching `PendingReliable` and releases its buffer. Retransmission stops for a packet that was never delivered, and nothing reports it — **silent data loss on the reliable channel.** Sequences are 16-bit and start at 0, so guessing is cheap. |
| **M7** `IngressEngine.cs:665` | A forged `Reliable` packet with a chosen sequence advances `NextExpectedSequence` (or fills the reorder buffer). The genuine packet then fails the half-space check and is discarded as "already delivered", permanently corrupting the ordered stream. |
| **M8** `IngressEngine.cs:526` | A forged 1-byte `KeepAlive` stamps `LastReceivedTicks` (`:515`) before the `switch`, so a peer that is actually gone never times out — the connection is pinned alive indefinitely, holding all its pooled state. |

All three are closed by the per-packet MAC in H8.

### M9 — NAT challenge echo has no origin binding or hop limit

**File:** `SynapseSocket/Transport/IngressEngine.Nat.cs:105-108`
**Severity:** MEDIUM (NAT traversal disabled by default)

```csharp
if (VerifyNatToken(fromEndPoint, payload))
    _sender.SendHandshake(fromEndPoint);
else
    _sender.SendNatChallenge(fromEndPoint, payload);   // echo back a token we did not issue
```

Two engines that receive each other's unrecognised tokens echo them back and forth indefinitely — neither can verify the other's token because each uses its own `_natChallengeSecret`. One forged datagram naming two victims starts a **permanent** exchange between them. The per-IP interval throttles it to one packet per `IntervalMilliseconds` per address, so this is a persistent low-rate loop rather than a livelock, but nothing ever terminates it.

Separately, `ProcessNatProbe` turns a 1-byte probe into a 9-byte challenge sent to a forged source — a ~9× reflection amplifier, per victim rather than per attacker packet.

**Suggested fix:** add a hop/echo flag so a challenge is echoed at most once, and drop tokens that do not verify instead of reflecting them.

---

## 4. CPU Loop Vulnerabilities

### H5 — `Drain()` has no per-poll datagram budget

**File:** `SynapseSocket/Transport/IngressEngine.cs:259`
**Severity:** HIGH

```csharp
while (true)
{
    if (_socket.Available == 0)
        break;
    ...
}
```

`Drain` returns only when the socket is empty. Because unknown senders are not rate limited at all (§2), a flood arriving faster than the engine processes it keeps `Available > 0` indefinitely and `Poll()` **never returns** — the host's frame loop stalls for as long as the flood lasts. In Unity this is a hung game thread, not a dropped frame.

The per-datagram work under flood is not small either: signature computation, blacklist lookup, and for handshakes an FNV fold of the whole payload (M22), a replay-cache insert, a connection rent, and a reply send.

**Suggested fix:** cap datagrams processed per `Drain` (a budget proportional to expected traffic, e.g. a few thousand), leaving the remainder in the kernel buffer for the next `Poll`. This bounds worst-case `Poll` time and converts a livelock into ordinary packet loss — which is what `SO_RCVBUF` is for.

### L1 — Catch-all `continue` has no forward-progress guarantee

**File:** `SynapseSocket/Transport/IngressEngine.cs:305-320`

```csharp
catch (SocketException) { continue; }
catch (Exception unexpectedException) { UnhandledException?.Invoke(unexpectedException); continue; }
```

Each `continue` returns to the top of the loop, which re-tests `Available`. For the error classes that actually occur — notably Windows `WSAECONNRESET` from an ICMP port-unreachable — the failing receive *does* consume the pending error, so the loop makes progress. But nothing in the structure *guarantees* it: any error that leaves the datagram queued and `Available` non-zero spins the host thread forever. Given the loop already needs a budget for H5, the same fix closes this hazard.

**Suggested fix:** the H5 iteration cap. Additionally, disable `SIO_UDP_CONNRESET` on Windows so ICMP-induced errors do not surface on the receive path at all.

### H12 — Malformed header forces a managed exception per packet

**File:** `SynapseSocket/Transport/IngressEngine.cs:481-491`; `Packets/PacketHeader.cs:96-114`

`PacketHeader.Read` signals a truncated header by `throw new ArgumentException(...)`, and `ProcessPacket` catches it. A managed throw/catch — allocation, message string, stack capture, two stack walks — costs on the order of microseconds, several orders of magnitude more than the parse it replaces.

An attacker flooding 2-byte malformed datagrams from **spoofed** sources pays essentially nothing while the engine pays a full exception each time. Spoofing matters here: a fixed source would be blacklisted after the first packet, but rotating source addresses defeats that, and the path has no rate limit (§2). This is both a CPU amplifier and sustained GC pressure on the receive path.

**Suggested fix:** convert `PacketHeader.Read` to a `TryRead` returning `bool`. The bounds checks themselves are correct (see §6) — only the signalling mechanism is wrong.

### M22 — `MixHandshakeNonce` hashes the entire attacker-controlled payload

**File:** `SynapseSocket/Transport/IngressEngine.cs:825`

A byte-at-a-time FNV-1a over the whole handshake payload — up to `MaximumPacketSize` (1400 bytes) — runs before any rate limiting, on every handshake. Combined with the unbounded replay cache (H6), each attacker packet buys a hash of its own choosing plus a dictionary insert.

**Suggested fix:** fold a bounded prefix (the 8-byte nonce the engine itself sends is sufficient) rather than the whole payload.

### M23 — `RemoveExpiredEntries` is an O(n) scan inline on the receive path

**File:** `SynapseSocket/Transport/IngressEngine.cs:858`

The sweep for `_seenHandshakes` (and `_natProbeLastResponseTicks`) enumerates the entire `ConcurrentDictionary` and is invoked **from inside `ProcessHandshake`** — the path an attacker floods. Since the attacker also controls `n` (H6), the once-a-minute pause is attacker-sized: a dictionary grown to millions of entries produces a multi-hundred-millisecond stall inside a single `Poll`, i.e. a visible frame hitch on a timer the attacker chooses.

**Suggested fix:** move both sweeps into `RunMaintenance` and make them incremental (bounded work per `Poll`).

### M18 — One lost segment retransmits all 255 segments

**File:** `SynapseSocket/Core/SynapseManager.Maintenance.cs:241`; `Transport/TransmissionEngine.cs:232`

A reliable segmented message takes **one** sequence number covering all its segments, and the receiver only ACKs when reassembly *completes*. A single lost segment therefore retransmits the entire ≈304 KB message every 250 ms, up to 10 times — ≈3 MB of traffic for one dropped datagram, and 2550 datagrams that will very likely trip the receiver's own rate limiter and get the sender blacklisted (H4).

**Suggested fix:** ACK segments individually (or carry a received-segment bitmap in the ACK) so retransmission is selective.

### M16 — ACK "batching" never coalesces

**File:** `SynapseSocket/Connections/SynapseConnection.cs:156-160`

```csharp
while (PendingAcks.TryDequeue(out ushort sequence))
    TransmissionEngine?.SendAck(this, sequence);
```

`AckBatchingEnabled` is true by default, but the flush sends **one 3-byte datagram and one blocking `SendTo` syscall per queued sequence**. Batching defers the ACKs without merging them, so it adds latency and gains nothing — and under a reliable-packet flood it emits one outbound datagram per inbound one, amplifying the attacker's flood back out of this host. `AckBatchIntervalMilliseconds` is never actually used to gate the flush; `FlushPendingAcks` runs every `Poll`.

**Suggested fix:** pack multiple sequences (or a base + bitfield) into a single ACK datagram, and honour the configured flush interval.

### H9 — Beacon heartbeat drives the single-threaded engine from a threadpool thread

**File:** `SynapseBeacon/Client/BeaconHostSession.cs:61` → `BeaconClient.cs:311` → `Transport/TransmissionEngine.cs:331`
**Severity:** HIGH

`BeaconHostSession`'s constructor starts `Task.Run(() => HeartbeatLoopAsync(...))`. That loop awaits `Task.Delay` and then calls `_client.SendHeartbeatAsync`, which reaches `SendAndReturnAsync` and calls `_synapse.SendRaw(...)` **synchronously on a threadpool thread** — the comment there even notes the engine is poll-driven.

That path enters `TransmissionEngine.SendDirect`, which on .NET 8+ does `TryGetValue` / `Clear` / `Add` on `_serializedSendTargets`, a **plain `Dictionary`** (`:67`). The host thread reaches the identical code inside `Poll()` on every handshake reply, ACK, keep-alive, and payload send. Two threads mutating one unsynchronised `Dictionary` is the classic corruption case: a resize interleaved with an insert can leave a cyclic bucket chain, and the next lookup spins forever inside `FindEntry`.

Presents as a rare, non-reproducible hang of the game thread, or an `IndexOutOfRangeException` surfacing from an unrelated send. Because the corrupted state lives in a shared engine field, restarting the beacon session does not clear it. When the latency simulator is enabled, the same off-thread call also races its plain `List<Deferred>` and shared `Random`.

Only the .NET 8+ build has `_serializedSendTargets`; the Unity/netstandard2.1 build escapes that specific field but still shares the socket and simulator queue.

**Suggested fix:** the heartbeat must not call into the engine off-thread. Queue the heartbeat request and have it drained by `Poll` on the engine thread, matching the documented threading model.

### M24 — Every per-`Poll` maintenance scan is attacker-sized

**File:** `SynapseSocket/Core/SynapseManager.Maintenance.cs:77`

`RunMaintenance` and `FlushPendingAcks` are O(connections) per `Poll`, and per connection they do more O(n) work (`RetransmitReliable` over pending, `RemoveExpiredSegments` over assemblies under a lock, renting a pooled list). Since the connection count is attacker-controlled (C4), a spoofed-handshake flood multiplies straight into per-frame CPU that persists for the full 15 s timeout window. Fixing C4 largely fixes this.

---

## 5. Memory Leaks

### The structural finding: connections are never returned to the pool

**M3 — `SynapseSocket/Connections/ConnectionManager.cs:64`, `:120` — MEDIUM**

`ResettableObjectPool<SynapseConnection>.Rent()` is called at exactly two sites. **`Return` is called at none** — a grep over the whole tree confirms it. Consequences:

- `SynapseConnection.OnReturn()` — which drains the pending reliable queue, returns every reorder-buffer array, and returns the splitter and reassembler — is **dead code**. It looks like the engine's cleanup safety net, but it never runs.
- The pool is write-only: every connection is a fresh allocation, and the pooling machinery (`[PoolResettableMember]`, the source generator, `OnRent`/`OnReturn`) is inert.
- **Every teardown path must therefore do its own cleanup explicitly** — and two of them do not (H7, M1) while a third does not either (M2).

**A note on characterisation:** because the connection object itself becomes garbage, unreturned `ArrayPool` buffers are collected by the GC rather than leaking without bound. The accurate description of H7/M1/M2/M4 is therefore **pool drain and sustained allocation churn** — the shared pool is progressively emptied and every subsequent rent allocates — rather than unbounded memory growth. On Mono that still shows up as frame-time spikes, and it defeats the entire point of the pooling design. The genuinely *unbounded* growth in this codebase is in §2 (blacklist, replay cache, connection table, NAT table, reorder buffer).

**Suggested fix:** return connections to the pool on every teardown path, which makes `OnReturn` the single cleanup implementation and eliminates H7, M1, and M2 as a class. This must be done together with an audit of connection references escaping to user code (`Connect()`'s return value, `PacketReceivedEventArgs.Connection`), because pooling a connection that user code still holds would introduce a use-after-free-class bug where a handler sends to the wrong peer.

### H7 — Remote `Disconnect` runs no cleanup at all

**File:** `SynapseSocket/Transport/IngressEngine.cs:519-524`
**Severity:** HIGH

```csharp
case PacketType.Disconnect:
    synapseConnection.State = ConnectionState.Disconnected;
    _connections.Remove(fromEndPoint, out _);
    ConnectionClosed?.Invoke(synapseConnection);
    ViolationOccurred?.Invoke(...);
    return;
```

Compare `SynapseManager.Disconnect`, which calls `ReturnConnectionSegmenters`, `ReturnReorderBufferToPool`, **and** `DrainPendingReliableQueue`. The remote path calls **none** of them, and `ConnectionManager.Remove` does not pool the connection (M3), so nothing else picks up the slack.

Every buffer the connection held is dropped on the floor: up to 64 reorder-buffer arrays, up to 256 `PendingReliable` backing arrays plus their pooled `List`s and pooled `PendingReliable` objects, the `PacketSplitter`, and the `PacketReassembler` together with every in-flight `SegmentAssembly`'s per-segment buffers (up to 16 assemblies × 255 segments).

**This is remotely triggerable by a single 1-byte datagram, and repeatable** — and because attribution is source-IP only (H8), an attacker can trigger it against any peer at will. Each cycle drains the shared pool a little further.

**Suggested fix:** route this through the same teardown helper as `SynapseManager.Disconnect`.

### M1 — Timeout teardown omits `ReturnConnectionSegmenters`

**File:** `SynapseSocket/Core/SynapseManager.Maintenance.cs:141`
**Severity:** MEDIUM

```csharp
Connections.Remove(synapseConnection.RemoteEndPoint, out _);
ReturnReorderBufferToPool(synapseConnection);
SynapseConnection.DrainPendingReliableQueue(synapseConnection);
// ← ReturnConnectionSegmenters(synapseConnection) is missing
```

Two of the three helpers run. The splitter and reassembler are never returned, taking with them every in-progress `SegmentAssembly`'s rented per-segment buffers. Timeouts are routine, not exceptional, so this is the highest-frequency instance of the pattern.

### M2 — `CreateNew` discards the replaced connection without reclaiming it

**File:** `SynapseSocket/Connections/ConnectionManager.cs:97-118`
**Severity:** MEDIUM

`Connect()` to an endpoint that already has a connection removes the old one from all tables and abandons it — no cleanup helpers, no pool return. Same buffer set as H7.

### M4 — Unreliable segmented send never returns the `ListPool` list

**File:** `SynapseSocket/Transport/TransmissionEngine.cs:236-247`
**Severity:** MEDIUM

`PacketSplitter.Split` returns a list rented from `ListPool<ArraySegment<byte>>` (`PacketSplitter.cs:56`). The reliable branch transfers ownership to `PendingReliable`, whose `OnReturn` returns it correctly. The unreliable branch returns `backingBuffer` to `ArrayPool` but **never returns `segments` to `ListPool`** — a leak on every unreliable segmented send, which is a hot path for large snapshot/voice payloads.

**Suggested fix:** `ListPool<ArraySegment<byte>>.Return(segments);` in the same `finally` that returns the backing buffer.

### M14 — A throwing `PacketReceived` subscriber leaks the delivery list and its payloads

**File:** `SynapseSocket/Transport/IngressEngine.cs:712-715`
**Severity:** MEDIUM

```csharp
foreach (ArraySegment<byte> deliverPayload in toDeliver)
    PayloadDelivered?.Invoke(synapseConnection, deliverPayload, isReliable, isPayloadRented: true);

ListPool<ArraySegment<byte>>.Return(toDeliver);
```

`SynapseManager.OnPayloadDelivered` returns each payload buffer in a `finally`, but it does **not** catch — so a throwing user handler propagates out of the loop. The pooled `toDeliver` list is never returned, and every payload after the throwing one is never delivered *and* never returned to `ArrayPool`. Draining a deep reorder buffer can lose dozens of buffers in one throw. Note that the manager's other event raises (`ConnectionEstablished`, `ConnectionClosed`, `PacketSent`) *do* wrap handlers in `try/catch` — this path is the inconsistent one.

**Suggested fix:** wrap the delivery loop in `try/finally` returning the list, and catch per-handler so one bad subscriber cannot abort the drain.

### M15 — `LatencySimulator.Flush` double-returns buffers if a send throws

**File:** `SynapseSocket/Diagnostics/LatencySimulator.cs:130-147`
**Severity:** MEDIUM (diagnostic feature, disabled by default)

If `sender(...)` throws mid-drain, the exception escapes `Flush` before the trailing `RemoveRange` compacts the list. Entries already sent and already returned to `ArrayPool` remain in `_deferred`, so the next `Flush` sends from **buffers that are back in the pool** (potentially owned by someone else) and returns them a **second time** — pool corruption, which is worse than a leak. The exception also escapes `Poll()` entirely, since step 5 has no `try/catch`.

**Suggested fix:** wrap the per-entry send in `try/catch`, and compact in a `finally`.

### M20 — `PendingReliableQueue` indexer overwrite orphans buffers

**File:** `SynapseSocket/Transport/TransmissionEngine.cs:190`, `:227`

```csharp
synapseConnection.PendingReliableQueue[sequence] = pendingReliable;
```

An indexer assignment silently replaces any existing entry for that sequence, orphaning its backing array, its pooled list, and the `PendingReliable` object. Reachable when `NextOutgoingSequence` wraps the 16-bit space while an old entry is still pending — a long-lived session with one stuck packet. `MaximumPending` (256) bounds the queue but does not prevent the collision.

**Suggested fix:** use `Add` and treat a collision as a hard error, or release the displaced entry explicitly.

### L2 — Sockets whose `Bind` throws are never disposed

**File:** `SynapseSocket/Core/SynapseManager.cs:195-213`

The `Socket` is constructed inside `try`, and on `SocketException` the handler raises `ConnectionFailed` and `continue`s without disposing it. The OS handle is held until finalization. Minor, but it is on the retry path of a bind conflict, which applications do retry.

---

## 6. Memory, GC, and CPU Performance

### H13 — The Unity/Mono build allocates 4–6 objects per received datagram

**File:** `SynapseSocket/Transport/IngressEngine.cs:292`

The netstandard2.1 receive path uses `ReceiveFrom(..., ref remoteEndPoint)`, which allocates a `SocketAddress`, an `IPEndPoint`, and an `IPAddress` **per datagram**; the send path re-serializes the target per datagram in `SendTo`. At 60 Hz with a handful of peers this is thousands of allocations per second on exactly the runtime whose GC handles them worst.

The .NET 8+ path is genuinely allocation-free for known senders (the `SocketAddress` overload plus the reverse lookup at `:333`), so this is a Unity-only gap.

**Suggested fix:** there is no allocation-free any-sender receive on that runtime, so the practical mitigations are (a) recommend `ConnectedSocketEnabled` for client builds — already implemented and already allocation-free — and (b) document the per-datagram cost for Unity server builds.

### L3 — Every datagram resolves its connection twice on the NET8 drain

**File:** `SynapseSocket/Transport/IngressEngine.cs:333` then `:369`

`Drain` calls `TryGetBySocketAddress` to resolve the sender, then discards the result and passes only the `IPEndPoint`; `HandleDatagram` immediately re-resolves the same connection via `ConnectionsByEndPoint.TryGetValue`. Two dictionary lookups per datagram where one would do, and the second uses the slower endpoint comparer.

**Suggested fix:** pass the already-resolved `SynapseConnection` through to `HandleDatagram`.

### L4 — Segmented receive rents and copies the payload twice

**File:** `SynapseSocket/Transport/IngressEngine.cs:575`, `:603`

Each arriving segment is copied from the receive buffer into a freshly rented buffer, handed to `TryReassemble`, which copies it **again** into `SegmentAssembly`'s own rented buffer, after which the first rental is immediately returned. The intermediate rental is pure overhead — `TryReassemble` takes a `ReadOnlySpan<byte>` and could read straight from the receive buffer.

### L5 — The send-path dictionary skips `IPEndPointComparer`

**File:** `SynapseSocket/Transport/TransmissionEngine.cs:67`

`_serializedSendTargets` is built with the default comparer while every other endpoint-keyed table uses `IPEndPointComparer.Default`. `IPEndPoint.GetHashCode`/`Equals` are slower and this dictionary is hit on every send.

### L6 / L7 — Pooled-list rentals for the empty case

`DeliverOrdered` (`:673`) rents a `ListPool` list to hold exactly one element in the loss-free common case. `RemoveExpiredSegments` (`PacketReassembler.cs:122`) takes a lock and rents a list on every connection on every `Poll`, almost always to remove nothing.

**Suggested fix:** deliver the single payload directly and only rent when the reorder buffer actually drains; in the reassembler, take the lock and rent only after a cheap check establishes there is something to expire.

### L8 — `Socket.Available` doubles the syscall count

**File:** `SynapseSocket/Transport/IngressEngine.cs:271`

Every datagram costs an `ioctl` plus a `recvfrom`. Switching to a non-blocking receive with `WouldBlock` as the exit condition halves the syscalls and simultaneously fixes C2.

### M19 — `HMACSHA256` allocated per NAT token computation

**File:** `SynapseSocket/Transport/IngressEngine.Nat.cs:132`

`using HMACSHA256 hmac = new(_natChallengeSecret);` allocates and re-runs the key schedule on every probe and every verification — and `VerifyNatToken` calls it **twice** (current and previous time bucket). All of this is on an unauthenticated path.

**Suggested fix:** cache a single `HMACSHA256` instance for the engine's lifetime (or use the static `HMACSHA256.HashData` overload).

### L12 / L13 — Minor

`Telemetry` uses `Interlocked` on every counter (`:81`) despite the engine being documented as single-threaded; it is correctly free when disabled. The reorder buffer and pending-reliable queue use `Dictionary<ushort, …>` where the sequence space is 16-bit and the window is bounded by config — a fixed ring buffer would remove hashing and allocation entirely from both.

---

## 7. Checked and Found Sound

Recording the negative results, because several are things a reviewer would reasonably suspect:

| Checked | Result |
|---|---|
| `SegmentAssembly.Initialize` seeing stale slots from a pooled list | **Sound.** `ListPool.Return` calls `value.Clear()`, so `Rent()` always yields an empty list and the fill loop always starts at 0. No cross-connection data disclosure. |
| Evicting an incomplete assembly throwing on unreceived slots | **Sound.** `PoolArrayIntoShared` null-checks `arraySegment.Array`. |
| Event args allocating per event | **Sound.** `PacketReceivedEventArgs`, `ConnectionEventArgs`, `ViolationEventArgs`, `PacketSentEventArgs` are all `struct` — no allocation. (Their XML comments describing them as "pooled" are stale; the behaviour is correct.) |
| `PacketHeader.Read` bounds checking | **Sound.** Every variant length-checks before reading, for all packet types including both segmented forms. Only the *signalling* (throwing) is a problem — see H12. The layout comment at `PacketHeader.cs:11` describes offsets that are wrong for `PacketType.Segmented`, but `Read` and `Write` agree with each other. |
| Double-return of the per-segment reassembly buffer | **Sound.** Returned exactly once on all four branches (complete, incomplete, protocol violation, early reject). |
| Zero-copy delivery returning the engine's live receive buffer to `ArrayPool` | **Sound.** The `isPayloadRented: false` flag is threaded correctly through `OnPayloadDelivered`. |
| Backward iteration in `RunMaintenance` vs mid-sweep removal | **Sound** *as written*, given the swap-remove semantics — but it becomes subtly wrong once C1 is fixed. See the note in C1. |
| `Telemetry` cost when disabled | **Sound.** Every method early-returns on `IsEnabled`. |
| NAT challenge token construction | **Sound.** Genuinely HMAC'd, bound to address + port + time bucket, with a two-bucket validity window and a secret never transmitted. The weakness is in the *echo* behaviour (M9), not the token. |

**Stale documentation (L14).** Four XML/comment claims are contradicted by the code and are worth correcting because they actively mislead: `SecurityProvider.InspectNew` ("delegates to `InspectEstablished` for size and rate-limit enforcement" — it does not rate-limit); the event-args "pooled" comments (they are structs); `BeaconServer.HandleRegister` ("silently dropped" — it replies `SessionNotFound`); and the `PacketHeader` layout comment. **Dead code (L11):** the private `EndPointKey` struct in `ConnectionManager.cs:205` is unreferenced.

---

## 8. Existing Test Coverage

The suite already covers oversized packets, blacklist-on-handshake, garbage bytes, truncated reliable headers, violation-action overrides, packet rate limiting, unknown-peer spoofed data, idle timeout, keep-alive, the receive-only-peer case, GC-free sends, connect/send/disconnect stress, out-of-order delivery, and segmentation.

Nothing in the suite exercises the paths behind the findings above. The highest-value additions, in order:

1. **Three or more concurrent connections, remove a middle one, assert `ConnectionsIndex` matches list position and that every remaining connection still receives maintenance** (C1). A pure unit test on `ConnectionManager` catches this in milliseconds.
2. **Zero-length datagram to the bound port, then assert the engine still receives** (C2).
3. **Handshake injected from a connected peer's endpoint, assert no handshake storm** (C3).
4. **Remote `Disconnect` and timeout teardown, asserting pool balance** — rent/return counters around a connect/disconnect cycle (H7, M1, M2, M4).
5. **Two large sends in one second, assert the peer is not blacklisted** (H4).

---

## 9. Suggested Fix Order

Ordered by (impact × ease), not by severity alone:

1. **C1** — one-line fix (`RemoveAt(lastConnectionsIndex)` plus hoisting the index reset), and it is the top finding on three axes.
2. **M4, M1, H7** — three missing cleanup calls; each is one to three lines.
3. **C2 + H5 + L1 + L8** — replace the `Available`-gated loop with a budgeted non-blocking drain. One rewrite of `Drain` closes a critical DoS, a livelock, an unbounded-spin hazard, and halves the syscall count.
4. **C4 + H3** — non-zero default connection cap, a bounded pre-connection rate limiter, and a TTL on the blacklist.
5. **H4** — default rate-limit violations to `Drop` rather than `KickAndBlacklist`.
6. **C3** — suppress the handshake reply for an already-`Connected` peer; breaks the loop without protocol changes.
7. **H9** — move the beacon heartbeat onto the engine thread.
8. **H12** — `PacketHeader.TryRead`.
9. **H1 + M17** — state-guard `LastReceivedTicks` and retry the initial handshake.
10. **M3** — return connections to the pool, making `OnReturn` the single cleanup path (audit escaping references first).
11. **H8** — per-packet authentication. The largest change on the list, and the only one that closes the spoofing class rather than individual instances; worth planning deliberately rather than patching around.
