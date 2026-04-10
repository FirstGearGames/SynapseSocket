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

### V9 — Handler-thrown exceptions and pool-backed `Payload` reference
`OnPayloadDelivered` is safe (try/finally returns the buffer), but if `PacketReceived` subscribers capture `packetReceivedEventArgs.Payload` without copying and the handler throws mid-work, the buffer is still returned to the pool while the caller holds a reference. Documentation hazard, not a code bug — the XML doc correctly warns that `Payload` is valid only for the duration of the handler. Consider a `Debug.Assert` or poisoning the segment post-callback in debug builds.

### V10 — `ConnectionManager.GetOrAdd` last-write-wins on signature
`_bySignature[signature] = synapseConnection;` overwrites any prior entry with the same signature. If two endpoints hash to the same 64-bit signature (birthday bound ~2^32), the earlier connection's reverse lookup is silently broken and `TryGetBySignature` can return the wrong connection. Low probability, non-zero. Consider logging/telemetry on collisions.

### V11 — Handshake replay cache is effective only for handshakes with identical bytes
Current check relies on the nonce making each legitimate handshake unique, but the protection only works if `ISignatureProvider.TryCompute` actually includes the full handshake payload (including the nonce). Custom provider hashing only the endpoint bypasses the replay cache. Recommend a doc note on `ISignatureProvider`.

---

## 🟡 Medium

### V12 — `RemoveExpiredHandshakeEntries` / `RemoveExpiredProbeLimitEntries` enumerate under concurrent mutation
Enumerate a `ConcurrentDictionary` while calling `TryRemove` inside. Safe for correctness, but enumeration allocates an enumerator struct and copies snapshots.

### V13 — `TryParsePeerEndPoint` copies the address bytes into a new `byte[]` twice per parse
Attacker-controlled input forces a new `IPAddress` + `IPEndPoint` allocation per received NAT server packet. Rate limited only by `SecurityProvider` bucket. Recommend `new IPAddress(ReadOnlySpan<byte>)` (available on netstandard 2.1) to eliminate the `ToArray()` copy.

### V14 — No cap on `SegmentAssemblyTimeoutMilliseconds`
Setting to 0 disables eviction; very large values combined with the (now fixed) V1 gave attackers a bigger window. Document a safe max (e.g. `<= 10_000`).

---

## 🟢 Low

### V16 — `ProcessNatProbe` responds to every non-blacklisted, non-established peer after rate-limit passes
If `ISignatureProvider` returns stable signatures per IP, the blacklist works; otherwise amplification-for-free exists. Already rate-limited.

### V17 — `ViolationAction` can be downgraded by a handler
By design, but if a malicious/buggy listener forces `Ignore`, kick/blacklist effects are bypassed. Document as intended.

### V18 — `Dictionary<ushort, SegmentAssembly> _currentSegments` is under a single `lock`
Serializes all segment arrivals for one connection; a slow handler on a completion path can hold the lock briefly. Not a vuln, a perf pinch point.
