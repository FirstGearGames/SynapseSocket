# SynapseSocket threadpool-starvation hardening — handoff

**Status: OPEN — analysis + recommendation only. No SynapseSocket code changed yet.**

This is a resumable handoff. A fresh session can pick the work up by reading this file top-to-bottom, then opening the files referenced under "Where it lives," then doing the work under "Recommended changes" and "How to verify." Nothing here has been implemented; the engine is byte-correct as-is (see "Why this is a perf/robustness issue, not a correctness bug").

---

## TL;DR

SynapseSocket runs every long-lived background loop — the per-socket **receive loop**, the **maintenance loop**, and the **pending-ACK loop** — via `Task.Run`, i.e. on the shared **.NET ThreadPool**. One `SynapseManager` (engine) therefore puts ~2–3 long-lived async loops on the pool; a process that stands up many engines (e.g. a server hosting many sockets, or a test suite running many CoreManagers in parallel) multiplies that. Under bursty parallel load the pool fills with engine work and its **growth heuristic (~1–2 new threads/second) can't keep up**, so short continuations — send completions, `Task.Delay` ticks, the application's own callbacks — get **delayed**. That presents as intermittent latency spikes and, in tests, hard timeouts.

The fix direction the owner asked for: **throttle / move this work off the shared pool onto dedicated threads, and make shutdown deterministically join them.** Recommendation below: run the receive loop (and ideally maintenance/ACK) on **dedicated background threads** rather than `Task.Run`, make the receive await cancellable, and keep the bounded join on stop.

---

## Why this surfaced now

This came out of the Nucleus "shared-pool corruption" investigation (`get_CurrentReadSpan` over-read, ~25% of full-suite runs). That hunt **exonerated the SynapseSocket engine**: a wire-checksum embedded at the engine's send entry and re-verified at every ingress delivery site survived 90+ reproducer rounds (including failing ones) with zero mismatches. The corruption was a **Nucleus-side** off-loop pooled-buffer return racing the network loop, now fixed in Nucleus (see `Docs/synapse-buffer-investigation.md` in the Nucleus repo). 

But that same investigation repeatedly noted the engine's acute sensitivity to **threadpool starvation under the full parallel suite** (e.g. `ConnectionStressTests` passes alone in ~4 s but hard-times-out at 6000 ms under suite parallelism). That starvation is a real, separate robustness issue and is what this doc is about. It matters in production: SynapseSocket targets heavy loads / game servers, where many connections and bursty traffic produce exactly the continuation storms the pool struggles with.

## Why this is a perf/robustness issue, not a correctness bug

The loops are genuinely `async` and release their thread on every `await` (the receive loop awaits `ReceiveFromAsync`; maintenance/ACK await `Task.Delay`), so they do not pin threads while idle. The problem is **aggregate**: many engines × bursts of ready continuations (received-packet processing, send completions) > available pool threads, with slow pool growth. So nothing corrupts — work just runs **late**. Don't expect a deterministic functional repro; expect latency/timeout tails that worsen with engine count and parallelism.

## Where it lives (file:line)

- `SynapseSocket/Transport/IngressEngine.cs:189` — `StartAsync` → `return Task.Run(() => ReceiveLoopAsync(cancellationToken), cancellationToken);`
- `SynapseSocket/Transport/IngressEngine.cs:197` — `ReceiveLoopAsync`: `while (!cancellationToken.IsCancellationRequested) { ... await _socket.ReceiveFromAsync(...); <process inline> }`
  - **Note:** the receive await is passed `CancellationToken.None` (line ~214/216), so it is **not** cancellable — the loop only unblocks on a received datagram or on `ObjectDisposedException` when the socket is closed. Shutdown therefore depends on socket disposal to break the await.
- `SynapseSocket/Core/SynapseManager.cs:255` — `_maintenanceTask = Task.Run(() => MaintenanceLoopAsync(...));`
- `SynapseSocket/Core/SynapseManager.cs:257` — `_pendingAckTask = Task.Run(() => PendingAckLoopAsync(...));` (only when ACK batching enabled)
- `SynapseSocket/Core/SynapseManager.cs:301` — `_ = Task.Run(() => NatPunchAsync(...));` (FullCone NAT only; fire-and-forget)
- `SynapseSocket/Core/SynapseManager.cs:458-475` and `:503+` (`ShutdownCoreAsync`) — cancels the CTS, collects `_ingressTasks` + `_maintenanceTask` + `_pendingAckTask`, and joins with `Task.WhenAll(pendingTasks).Wait(5000)`. So a **bounded 5 s join already exists**; loops not finished by then are abandoned.

## Recommended changes (prioritized)

1. **Receive loop → dedicated background thread with a blocking receive (highest impact).**
   Replace `Task.Run(ReceiveLoopAsync)` with a `new Thread(ReceiveLoop) { IsBackground = true, Name = "Synapse-Ingress-<port>" }` running a **synchronous** loop: `_socket.ReceiveFrom(...)` (blocking) → process inline → repeat. This is the standard high-throughput UDP server pattern and removes per-packet receive continuations from the shared pool entirely. A dedicated thread blocked in `ReceiveFrom` costs almost nothing while idle.
   - Make shutdown break the blocking receive deterministically: close/`Dispose` the socket (causes `ReceiveFrom` to throw `ObjectDisposedException`/`SocketException(Interrupted)` — catch and exit), then `Thread.Join(timeout)`. This also fixes the current `CancellationToken.None` non-cancellability.
   - Keep `ProcessPacket` strictly single-threaded per socket (it already is, since the loop doesn't re-enter the receive until processing returns) — preserve that invariant on the dedicated thread.

2. **Maintenance + pending-ACK loops → dedicated thread(s) or a single shared timer thread (medium impact).**
   These are periodic (`await Task.Delay(interval)`). Either run each on its own dedicated background thread using a blocking wait (`Thread.Sleep` or a `WaitHandle.WaitOne(interval)` that the stop signal can release early), or fold both onto one shared periodic worker. Goal: keep their wakeups off the shared pool so they neither add to nor wait behind pool congestion.

3. **Make the receive await cancellable if loop 1 is deferred (low effort, partial).**
   If the dedicated-thread rewrite is too large for now, at minimum pass the real `cancellationToken` to `ReceiveFromAsync` so the loop can unwind promptly on stop instead of relying on socket disposal. (Still on the pool, so it does not address starvation — it only improves shutdown latency.)

4. **Throttle / bound, if engine count is the driver (optional).**
   If a host legitimately stands up many engines, consider a shared bounded worker/scheduler for engine background work, or document a recommended max-engines-per-process. For the common single-server-socket case, loops 1–2 are sufficient.

5. **Keep the bounded join on shutdown.**
   The existing `Task.WhenAll(...).Wait(5000)` is good; mirror it with `Thread.Join(5000)` for any threads introduced by loops 1–2 so stop stays deterministic and bounded.

## How to verify

- **Repro the starvation** (not the corruption — that's fixed Nucleus-side): stand up N parallel engines (or run the Nucleus full suite with the Synapse tests enabled) and watch for latency tails / timeouts that scale with N and parallelism. The Nucleus signal was `ConnectionStressTests` / message round-trips timing out under suite parallelism while passing in isolation.
- **After loops 1–2**: the same parallel load should show flatter latency and no pool-growth-limited timeouts, because the receive/maintenance work no longer competes for pool threads. Confirm no functional regression (ordering, reassembly, reliable delivery) and that shutdown still completes within the bounded join.
- **Allocation**: a dedicated-thread receive loop should not increase steady-state allocations vs the async loop; cross-check against `docs/ALLOCATION_AUDIT.md`.

## Current status / what's done / what's next

- **Done:** root-caused the Nucleus corruption (engine exonerated); documented the starvation mechanism and the concrete hardening direction here. No SynapseSocket code changed.
- **Next (resume here):** implement recommendation #1 (dedicated receive thread + cancellable/disposable shutdown), then #2, then re-verify under parallel load. Engine source is otherwise unchanged.
- **Related context:** Nucleus repo `Docs/synapse-buffer-investigation.md` (the corruption resolution and why the engine is exonerated). The Nucleus-side fix already makes off-loop send completions safe for Nucleus, so this hardening is a pure perf/robustness improvement, not a correctness prerequisite.
