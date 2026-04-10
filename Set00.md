# Set00 — SynapseSocket Vulnerability Report

_Generated 2026-04-09. Supersedes prior Set00._

## 🔴 Critical

### ~~V1 — Per-connection segment state is unbounded~~ ✅ Fixed
`MaximumConcurrentSegmentAssembliesPerConnection` (default 16) added to `SynapseConfig`. Enforced in `PacketReassembler.TryReassemble`; exceeding the cap raises a protocol violation.

### ~~V2 — No connection count cap~~ ✅ Fixed
`MaximumConcurrentConnections` (default 0 = disabled) added to `SynapseConfig`. `ProcessHandshake` rejects new peers with `ConnectionRejectedReason.ServerFull` when the limit is reached.

### V3 — `SecurityProvider._blacklist` grows unbounded, forever
Blacklist is intentionally permanent — the user must explicitly call `RemoveFromBlacklist` to lift a ban. No automated eviction.

### ~~V4 — `SecurityProvider._rateBuckets` grows unbounded~~ ✅ Fixed
`RateBucket` now tracks `LastAccessTicks`. `RemoveExpiredRateBuckets` sweeps buckets idle for 5 minutes, called from the maintenance loop.

### ~~V5 — `_seenHandshakes` evicted only every 100 handshakes~~ ✅ Fixed
Counter replaced with time-based CAS eviction; cache is swept at most once per minute regardless of traffic volume.

### ~~V6 — `_natProbeRateLimiter` same problem~~ ✅ Fixed
Same time-based CAS eviction applied to `_natProbeRateLimiter`.

---

## 🟠 High

### ~~V7 — Reorder buffer is unbounded~~ ✅ Fixed
`MaximumOutOfOrderReliablePackets` (default 64) added to `SynapseConfig`. `DeliverOrdered` returns the pooled buffer and raises a `KickAndBlacklist` violation when the cap is exceeded.

### ~~V8 — `ArrayPool` buffers in `ReorderBuffer` leak on silent disconnect~~ ✅ Fixed
`ReturnReorderBufferToPool` drains and returns all pooled segments under `ReliableLock`. Called from `ProgressiveKeepAliveSweep` (timeout), `DisconnectAsync`, and `DisconnectAndBlacklist`.

### ~~V10 — `ConnectionManager.GetOrAdd` last-write-wins on signature~~ ✅ Fixed
`GetOrAdd` now uses `TryAdd`; on failure it overwrites the slot (newer connection wins) and fires `SignatureCollisionDetected` event for host telemetry.

### ~~V11 — Handshake replay cache is effective only for handshakes with identical bytes~~ ✅ Fixed
`ISignatureProvider.TryCompute` XML doc now explicitly warns that implementations **must** incorporate `handshakePayload` into the signature when non-empty; providers that hash only the endpoint bypass the replay cache and provide no replay protection.

---

## 🟡 Medium

### ~~V12 — `RemoveExpiredHandshakeEntries` / `RemoveExpiredProbeLimitEntries` enumerate under concurrent mutation~~ ✅ Fixed
Both methods collapsed to expression-body one-liners delegating to a shared generic helper `RemoveExpiredEntries<TKey>`. The helper uses `ListPool<TKey>` to collect keys first, then removes them — zero intermediate heap allocation, no concurrent-mutation concern.

### ~~V13 — `TryParsePeerEndPoint` copies the address bytes into a new `byte[]` twice per parse~~ ✅ Fixed
`new IPAddress(span.ToArray())` replaced with `new IPAddress(ReadOnlySpan<byte>)` overload (netstandard 2.1). No heap allocation for the address bytes.

### ~~V14 — No cap on `SegmentAssemblyTimeoutMilliseconds`~~ ✅ Fixed
`SynapseManager` constructor now throws `ArgumentOutOfRangeException` when `SegmentAssemblyTimeoutMilliseconds` is non-zero and exceeds 300 000 ms (5 minutes).

---

## 🟢 Low

### ~~V16 — `ProcessNatProbe` responds to every non-blacklisted, non-established peer after rate-limit passes~~ ✅ Documented
`NatTraversalConfig.IntervalMilliseconds` XML doc now quantifies the amplification window (2 packets per source IP per interval). Inline comment in `ProcessNatProbe` confirms the bounding argument. No code change required — the per-IP rate limiter is the correct and sufficient mitigation.

### ~~V17 — `ViolationAction` can be downgraded by a handler~~ ✅ Documented
`ViolationEventArgs.Action` and the `ViolationDetected` event XML docs now explicitly warn that downgrading from `KickAndBlacklist` to `Ignore` suppresses all protective action and that only confirmed false positives justify the downgrade.

### ~~V18 — `Dictionary<ushort, SegmentAssembly> _currentSegments` is under a single `lock`~~ ✅ Accepted
Per-connection lock; held only for in-memory dictionary mutations, not I/O. Contention at per-connection granularity is negligible in practice. Not a vulnerability. No change warranted.
