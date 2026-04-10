# Set00 — SynapseSocket Vulnerability Report

_Generated 2026-04-09. Supersedes prior Set00._

## Pre-existing file corruption discovered during audit

**C1 — `SynapseSocket/Core/Configuration/SynapseConfig.cs` is truncated mid-comment on line 101.** No `public NatTraversalConfig NatTraversal = new();` field, no closing `}`, no EOF newline. `SynapseManager.cs:205`, `SynapseManager.NatTraversal.cs:48-51,65`, and `IngressEngine.cs:387,437-441` all reference `Config.NatTraversal.Mode` / `Config.NatTraversal.Server.*`. The solution cannot currently compile. Not fixed — awaiting decision.

**C2 — `SynapseSocket/Transport/IngressEngine.cs` had 57 trailing `\x00` bytes** after the final `}`. File parsed as "data" instead of text. Fixed via `tr -d '\000'`; no code was changed.

---

## 🔴 Critical

### V1 — Per-connection segment state is unbounded
`PacketSegmenter._pending` is `Dictionary<ushort, SegmentAssembly>` with no cap. An attacker who completes a handshake can inject one segment for up to 65 536 distinct `segmentId` values. Each allocates a `SegmentAssembly` holding a `ListPool<byte[]?>` (size `segmentCount`), an `ArrayPool<int>` (size `segmentCount`), plus up to `MaximumSegments - 1` per-segment `ArrayPool<byte>` buffers. With `MaximumSegments = 255` that is ~65 536 assemblies × up to 254 pooled byte arrays each, held for `SegmentAssemblyTimeoutMilliseconds` (default 5 s).

**No cap on concurrent `SegmentAssembly` count and no cap on concurrent distinct `segmentId`s.**

Recommend two new `SynapseConfig` fields, e.g. `MaximumConcurrentSegmentAssembliesPerConnection` (default ~16), enforced on the "new assembly" branch of `TryReassemble` by raising a violation.

### V2 — No connection count cap
`ConnectionManager` accepts unlimited peers. Combined with V1, a single attacker with many source IPs can force per-connection segmenter state × N. Recommend `SynapseConfig.MaximumConcurrentConnections` with handshake rejection when reached.

### V3 — `SecurityProvider._blacklist` grows unbounded, forever
No eviction on the blacklist `ConcurrentDictionary`. Attacker with a spoofable source can cause permanent memory growth via repeated kick+blacklist cycles. Recommend a timed expiry (LRU or ticks-based).

### V4 — `SecurityProvider._rates` grows unbounded
One `RateBucket` is allocated per distinct signature and never evicted. Attacker cycling through source ports creates a new signature per port. Recommend an age-based sweep from the maintenance loop.

### V5 — `IngressEngine._seenHandshakes` is only evicted every 100 handshakes
A flood of 99 unique handshakes stays in memory indefinitely if traffic then stops. Low severity alone, combines with other memory-pressure vectors. Consider tying eviction to maintenance loop time, not counter.

### V6 — `IngressEngine._natProbeRateLimiter` same problem
Eviction every 100 probes. Combined with NAT amplification concerns, worth moving to the maintenance loop.

---

## 🟠 High

### V7 — Reorder buffer is unbounded
`SynapseConnection.ReorderBuffer : Dictionary<ushort, ArraySegment<byte>>`. Out-of-order reliable packets rent an `ArrayPool<byte>` buffer, wrap it in an `ArraySegment`, and stuff it in the dictionary. Keys are `ushort`, so max 65 535 entries before collisions, but each entry holds a pooled buffer up to `MaximumPacketSize`. Attacker can deliberately skip sequences to fill reorder storage with ~65 535 × 1400 B ≈ 90 MB per connection, with no eviction path. Recommend a cap `MaximumOutOfOrderReliablePackets` with a violation when exceeded.

### V8 — `ArrayPool` buffers in `ReorderBuffer` are never returned if the peer silently disconnects
On timeout in `ProgressiveKeepAliveSweep`, the connection is removed but `ReorderBuffer`'s pooled buffers leak back into the ArrayPool without being returned. Recommend a disposal pass inside `Connections.Remove` (or on the `ConnectionClosed` side) that iterates `ReorderBuffer` and returns `.Array` for each entry.

### V9 — Handler-thrown exceptions and pool-backed `Payload` reference
`OnPayloadDelivered` is safe (try/finally returns the buffer), but if `PacketReceived` subscribers capture `packetReceivedEventArgs.Payload` without copying and the handler throws mid-work, the buffer is still returned to the pool while the caller holds a reference. Documentation hazard, not a code bug — the XML doc correctly warns that `Payload` is valid only for the duration of the handler. Consider a `Debug.Assert` or poisoning the segment post-callback in debug builds.

### V10 — `ConnectionManager.GetOrAdd` last-write-wins on signature
`_bySignature[signature] = synapseConnection;` overwrites any prior entry with the same signature. If two endpoints hash to the same 64-bit signature (birthday bound ~2^32), the earlier connection's reverse lookup is silently broken and `TryGetBySignature` can return the wrong connection. Low probability, non-zero. Consider logging/telemetry on collisions.

### V11 — Handshake replay cache is effective only for handshakes with identical bytes
Current check relies on the nonce making each legitimate handshake unique, but the protection only works if `ISignatureProvider.TryCompute` actually includes the full handshake payload (including the nonce). Custom provider hashing only the endpoint bypasses the replay cache. Recommend a doc note on `ISignatureProvider`.

---

## 🟡 Medium

### V12 — `EvictStaleHandshakeEntries` / `EvictStaleProbeLimitEntries` enumerate under concurrent mutation
Enumerate a `ConcurrentDictionary` while calling `TryRemove` inside. Safe for correctness, but enumeration allocates an enumerator struct and copies snapshots.

### V13 — `TryParsePeerEndPoint` copies the address bytes into a new `byte[]` twice per parse
Attacker-controlled input forces a new `IPAddress` + `IPEndPoint` allocation per received NAT server packet. Rate limited only by `SecurityProvider` bucket. Recommend stackalloc + `new IPAddress(ReadOnlySpan<byte>)` (available on netstandard 2.1).

### V14 — No cap on `SegmentAssemblyTimeoutMilliseconds`
Setting to 0 disables eviction; very large values combined with V1 give attackers a big window. Document a safe max (e.g. `<= 10_000`).

### V15 — `PendingReliableQueue` cap is not re-verified here
`TransmissionEngine.SendReliable` throws `InvalidOperationException("Reliable backpressure limit reached.")` — confirm the cap is per-connection and not per-engine.

---

## 🟢 Low

### V16 — `HandleNatProbe` responds to every non-blacklisted, non-established peer after rate-limit passes
If `ISignatureProvider` returns stable signatures per IP, the blacklist works; otherwise amplification-for-free exists. Already rate-limited.

### V17 — `ViolationAction` can be downgraded by a handler
By design, but if a malicious/buggy listener forces `Ignore`, kick/blacklist effects are bypassed. Document as intended.

### V18 — `Dictionary<ushort, SegmentAssembly> _pending` is under a single `lock`
Serializes all segment arrivals for one connection; a slow handler on a completion path can hold the lock briefly. Not a vuln, a perf pinch point.

---

## Biggest wins if you want to act

1. **V1** — concurrent-assembly cap per connection.
2. **V7** — reorder buffer cap + violation.
3. **V8** — return pooled reorder buffers on connection removal.
4. **V3/V4** — evict `_blacklist` and `_rates` from the maintenance loop.
