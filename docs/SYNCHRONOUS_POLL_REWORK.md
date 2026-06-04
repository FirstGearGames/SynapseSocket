# SynapseSocket synchronous / poll-driven rework — design + plan

**Status: IN PROGRESS — branch `poll-driven-rework` (off `main` @ a6d7492). `main` is intentionally
left stable so the Nucleus project can keep consuming the current engine.**

## Why this rework

Two problems dogged the engine, both rooted in its background-thread model:

1. **ThreadPool starvation.** Every long-lived loop (per-socket receive, maintenance, pending-ACK)
   ran via `Task.Run` on the shared .NET ThreadPool. Under bursty parallel load the pool's slow growth
   couldn't keep up, so continuations (and the app's callbacks) ran late — latency tails and test
   timeouts. See `THREADPOOL_STARVATION_HARDENING.md`.
2. **Latent concurrency corruption.** Trying to move the receive loop off the pool (any mechanism —
   dedicated threads *or* a cooperative bounded pool) reproducibly poisoned `ArrayPool.Shared` under
   load: the pooled `PacketReassembler`/`PacketSplitter` and deferred reliable buffers are touched by
   the receive thread while teardown returns them to their pool, so another connection re-rents an
   in-use object. These are pre-existing teardown-vs-receive data races that the current timing keeps
   dormant. (Full analysis + the abandoned attempt: branch `backup/threadpool-hardening-attempt`.)

Both problems are *consequences of multi-threading shared per-connection state.* Rather than harden a
web of delicate cross-thread races (and pay lock contention forever), this rework **removes the
concurrency**: a synchronous send + poll-driven engine that is **single-threaded per socket**.

- **Starvation → gone:** no background loops, nothing on the shared ThreadPool.
- **Corruption → impossible by construction:** one thread owns a socket's entire lifecycle
  (receive, reassembly, reliable send/retransmit, teardown), so pooled objects are never shared.
- **Scalability → preserved/improved:** scale out by running N sockets, each on its own thread, and
  sharding connections across them (`SO_REUSEPORT` or endpoint hashing). No shared state ⇒ linear core
  scaling with no locks. The current engine's per-socket receive is *already* serial, so no real
  per-socket parallelism is lost.

## Target architecture

The host drives the engine from its own thread (e.g. a game loop). The engine never spawns threads.

- **Send — synchronous, immediate.** `socket.SendTo(...)` (blocking) on the caller's thread. No
  `Task`, no continuation, no per-send allocation. This is the standard high-throughput UDP server
  pattern.
- **Receive + maintenance — `Poll()`.** The host calls `Poll()` regularly (each frame/tick). One
  `Poll()`:
  1. Drains the socket: non-blocking `ReceiveFrom` in a loop until `WouldBlock`, processing each
     datagram inline (filter → reassemble → order).
  2. Delivers completed/ordered messages (see "Delivery model" — the one open decision).
  3. Runs maintenance off the wall clock: keep-alive, timeout, reliable retransmit, ACK flush,
     segment-assembly timeout.
  The kernel `SO_RCVBUF` (already raised to 1 MiB) absorbs bursts between polls.
- **Lifecycle — synchronous.** `Start()` binds; `Stop()`/`Dispose()` closes. No `CancellationToken`,
  no task joins, no `Task.Run`.
- **Threading contract.** An engine instance is **not thread-safe**; drive it from one thread (or
  serialize externally). Scale with multiple engines/sockets + sharding.

## What gets deleted or simplified (the big win)

Single-threaded ownership lets a large amount of concurrency machinery go away:

- `IngressEngine.ReceiveLoopAsync` → `Drain()` (non-blocking receive pump, called by `Poll`).
- `SynapseManager.MaintenanceLoopAsync` + `PendingAckLoopAsync` → folded into `Poll` as plain methods.
- `CancellationTokenSource`, `_ingressTasks`, `_maintenanceTask`, `_pendingAckTask`, all `Task.Run` →
  removed.
- `ReceiveDispatcher` / `BoundedTaskScheduler` → not needed (live only on the backup branch).
- `PendingReliableQueue` `ConcurrentDictionary` → plain `Dictionary`; `PendingAcks` `ConcurrentQueue`
  → plain `Queue`; `ReliableLock` and `ConnectionManager._connections` lock → removed.
- The deferred reliable-release dance (`DeferReliableRelease` / `DrainReliableReleases` / the two
  swap lists) → removed; buffers are freed immediately on ACK/eviction/teardown because no other
  thread can be reading them. **This is exactly the machinery whose races caused the corruption.**
- `async`/`await`/`CancellationToken` parameters across the public + internal send surface → removed.

Net effect: the engine gets materially smaller and is race-free by construction.

## Public API (proposed — breaking; Nucleus will need updating)

```
// lifecycle
void Start();                                   // was StartAsync(CancellationToken)
void Stop();  void Dispose();                   // was StopAsync / DisposeAsync

// the pump — host calls every frame/tick
void Poll();                                    // drain socket + deliver + maintenance

// send (synchronous, immediate)
void Send(SynapseConnection c, ReadOnlySpan<byte> payload, bool reliable);   // was SendAsync(...)
void SendRaw(IPEndPoint target, ReadOnlySpan<byte> data);
SynapseConnection Connect(IPEndPoint endPoint);  // was ConnectAsync(...)
void Disconnect(SynapseConnection c);            // was DisconnectAsync(...)
```

Existing events (`PacketReceived`, `ConnectionEstablished`, `ConnectionClosed`, `ConnectionFailed`,
`ViolationDetected`, `UnhandledException`, `UnknownPacketReceived`) are **retained**, but they now fire
**synchronously inside `Poll()`** on the caller's thread instead of from a background loop.

### The one open decision — delivery model

- **(A) Events during `Poll()` [recommended].** Keep the current event surface; events fire while the
  host is inside `Poll()`. Smallest blast radius for Nucleus — it keeps `PacketReceived += …` and only
  adds a `Poll()` call to its loop. Matches "data is delivered when you poll."
- **(B) Poll-drain queue.** `Poll()` returns a batch / the host drains a `TryReceive(out msg)` queue.
  More explicit, but a larger rewrite for every consumer.

This doc assumes (A) until decided.

## Edge cases to handle during implementation

- **Latency simulator** (test-only) currently schedules delayed async sends. In a sync model it must
  buffer "in-flight" datagrams and release them during `Poll()` off the wall clock (a small internal
  timed queue), or be marked test-only and reworked accordingly.
- **NAT punch** (`NatPunchAsync`, timed probe bursts) becomes tick-driven inside `Poll()` (per-pending
  connection state machine advanced by elapsed time) rather than a fire-and-forget `Task`.
- **Maintenance cadence.** No more progressive per-tick slicing needed; `Poll()` is host-paced. Sweep
  all connections each `Poll()`, or time-budget if a host polls very infrequently with huge tables.
- **Reliable retransmit/ACK timing** now advance per `Poll()`; document that poll cadence bounds
  retransmit granularity (fine for typical 60Hz loops; expose intervals as today).

## Scaling guidance (to document for consumers)

- One engine = one (or few) sockets driven by one thread. A single core handles very high UDP volume.
- To use multiple cores: run multiple engines, each its own socket+thread; shard connections via
  `SO_REUSEPORT` (kernel load-balances datagrams) or by hashing the client endpoint. Independent
  shards ⇒ no shared state ⇒ no locks ⇒ linear scaling. Optionally provide a thin `SynapseHost` helper
  that owns N shards.

## Phased plan

The core (phases 2–6) is highly interdependent, so expect a large coherent change rather than many
independently-shippable steps. Suggested order:

1. **Sync transmission core.** `TransmissionEngine` synchronous `SendTo`/`SendDirect`; drop async send
   plumbing. (Self-contained; needed by every variant.)
2. **Poll ingress.** `IngressEngine.Drain()` — non-blocking receive pump that processes inline.
3. **Tick maintenance.** Convert the maintenance + ACK loops into plain methods invoked by `Poll`.
4. **Lifecycle + `Poll`.** `SynapseManager.Start/Stop/Poll`; delete loops, CTS, tasks, dispatcher;
   wire `Poll` → ingress drain + delivery + maintenance.
5. **De-concurrency.** Remove `ConcurrentDictionary`/`ConcurrentQueue`/locks/deferred-release from the
   connection + manager types now that access is single-threaded.
6. **API cleanup.** Remove `async`/`CancellationToken` from the public surface; rename `*Async` → sync.
7. **Tests + demos.** Rewrite to drive `Poll()` (Send → loop `Poll()` + assert). Re-validate the
   integrity/stress tests show **no corruption and no starvation timeouts** under parallel runs.
8. **Sharding helper + consumer-facing docs** (optional but recommended for the game-server target).

## Verification

- Build both TFMs (`netstandard2.1;net8.0`).
- The `Stress/PayloadIntegrityStressTests` (corruption canaries) and `ConnectionStressTests` must pass
  **in the full parallel suite** with zero corruption and no pool-starvation timeouts — the two
  failure modes that motivated this rework.
- Confirm functional parity: handshake, ordered/reliable delivery, reassembly, keep-alive/timeout,
  violations, NAT, telemetry, allocation profile (`docs/ALLOCATION_AUDIT.md`).
