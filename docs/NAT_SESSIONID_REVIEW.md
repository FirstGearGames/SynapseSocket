# NAT Session ID Refactor — Review Findings

**Date:** 2026-04-11
**Scope:** Inspect `NatServer.cs`, `IngressEngine.Nat.cs`, `TransmissionEngine.Nat.cs`, `SynapseManager.Nat.cs`, `NatHostSession.cs` after the session ID type was changed from `string` to `uint` (still 6 digits). Plus review of flagged variable-capture issues in `HostViaNatServerAsync`.

---

## Severity Legend
- **BUG** — incorrect behaviour, observable at runtime.
- **RACE** — thread-safety hole; will bite under load.
- **STALE-DOC** — XML or comment no longer matches the code.
- **DEAD** — unused member left over from the refactor.
- **STYLE** — formatting/rule violations that do not affect behaviour.

---

## 1. `SynapseManager.Nat.cs` — `heartbeatCts` captured/disposed races

The IDE is correct to flag this. There are multiple related problems in `HostViaNatServerAsync`:

### 1a. RACE: handler reads `_natHostPeerHandler` without the lock

`SynapseManager.Nat.cs:193`
```csharp
internal void OnNatPeerReady(IPEndPoint peerEndPoint)
{
    _natHostPeerHandler?.Invoke(peerEndPoint);   // ← lock-free read
    ...
}
```

All *writers* of `_natHostPeerHandler` take `_natRendezvousLock` (lines 84–91, 104–105, 118–119, 176–177), but `OnNatPeerReady` reads the delegate without the lock. The sequence that blows up:

1. Ingress thread reads `_natHostPeerHandler` (non-null).
2. Timeout path (line 104–107) enters the lock, sets `_natHostPeerHandler = null`, releases the lock, then calls `heartbeatCts.Dispose()`.
3. Ingress thread invokes the captured lambda, which calls `heartbeatCts.Token` → **`ObjectDisposedException`**.

### 1b. BUG: `heartbeatCts` disposed while the peer handler lambda still captures it

`SynapseManager.Nat.cs:90`
```csharp
_natHostPeerHandler = peerEndPoint => _ = Task.Run(() =>
    ConnectAfterRendezvousAsync(peerEndPoint, heartbeatCts.Token));
```

The lambda closes over `heartbeatCts`. On the timeout path (line 107) and the generic catch path (line 121), `heartbeatCts.Dispose()` runs while the pre-assigned handler may still be live on the ingress thread. Even with the lock tightened (fix for 1a), the window is still open between *reading the delegate* and *invoking `.Token`*.

**Fix options:**
- Capture `heartbeatCts.Token` into a local **before** assigning the lambda, or
- Do not pre-assign `_natHostPeerHandler` until after the session ID has been received (move lines 84–91 to after line 111), and wrap the error paths so the handler can never outlive the `CancellationTokenSource`.

### 1c. BUG/RACE: `cancellationToken` used instead of the linked token

`SynapseManager.Nat.cs:97, 100`
```csharp
await _sender.SendNatRequestSessionAsync(serverEndPoint, cancellationToken)...
Task timeoutTask = Task.Delay(..., cancellationToken);
```

`heartbeatCts` (line 82) is linked from both `cancellationToken` **and** the manager's shutdown token. Using the caller token alone here means:
- If the engine is stopped mid-setup, the initial `SendNatRequestSessionAsync` and the timeout `Task.Delay` will not cancel.
- The rendezvous wait can hang past engine shutdown.

**Fix:** use `heartbeatCts.Token` on lines 97 and 100. That is in fact what the IDE is suggesting.

### 1d. RACE: `_natSessionSource` reads/writes without the lock

`SynapseManager.Nat.cs:227, 238`
```csharp
internal void OnNatSessionCreated(uint sessionId)
{
    TaskCompletionSource<uint>? source = _natSessionSource;
    _natSessionSource = null;
    source?.TrySetResult(sessionId);
}
```

`HostViaNatServerAsync` writes `_natSessionSource` on line 93 outside the lock, and `OnNatSessionCreated` / `OnNatSessionUnavailable` read-then-null it without the lock either. Two racing server responses (e.g. delayed `NatSessionUnavailable` after `NatSessionCreated`, or two concurrent `HostViaNatServerAsync` calls) can drop a completion or complete the wrong source.

**Fix:** either take `_natRendezvousLock` in both OnNat* handlers, or replace the pair with a single `Interlocked.Exchange`.

---

## 2. `NatSessionRegistry.cs` — concurrency + security + dead field

### 2a. RACE (pre-existing, exposed by the uint dictionary): `Dictionary<uint, Entry>` is not thread-safe

`NatSessionRegistry.cs:41`
```csharp
private readonly Dictionary<uint, Entry> _sessions = [];
```

`TryCreateSession`, `Register`, `Heartbeat`, `CloseSession` run on the `NatServer` receive loop. `EvictExpired` runs on the eviction `Timer` thread (`NatServer.cs:57`). No locking → classic `Dictionary` corruption bug under load. This was latent before the type change but remains present.

**Fix:** either serialise `EvictExpired` onto the receive loop (queue a flag), or switch `_sessions` to `ConcurrentDictionary<uint, Entry>` and adapt `EvictExpired` to use `TryRemove`.

### 2b. BUG (security): `GenerateId` now uses a non-cryptographic PRNG

`NatSessionRegistry.cs:167`
```csharp
private static uint GenerateId() => (uint)Random.Shared.Next(100000, 1000000);
```

The previous implementation used `RandomNumberGenerator.Fill`. `Random.Shared` is a standard PRNG — an attacker who can observe a few issued session IDs can predict future ones and hijack sessions by racing legitimate joiners. Session IDs are the only secret in the NAT matching flow, so this matters.

**Fix:**
```csharp
private static uint GenerateId()
{
    Span<byte> bytes = stackalloc byte[4];
    RandomNumberGenerator.Fill(bytes);
    uint raw = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(bytes);
    return raw % 900_000u + 100_000u;
}
```

### 2c. DEAD: `Entry.Joiners` is written but never read

`NatSessionRegistry.cs:28`
```csharp
internal readonly List<IPEndPoint> Joiners = []; //Unused?
```

`Register` adds to this list but nothing ever reads it. The inline `//Unused?` comment is itself a rule violation (Formatting.md — comments only at sentence ends, and the `What`/`Why` rules in CLAUDE.md forbid "tentative" comments).

**Fix:** delete the field, the add, and the comment. If it is kept for future use, remove the `//Unused?` and document the intent in XML.

### 2d. STALE-DOC: `TryParseSessionId` summary says "Returns null"

`NatSessionRegistry.cs:153`
```csharp
/// <summary>
/// Parses a fixed-length uint session ID from raw packet bytes.
/// Returns null if the slice is too short.
/// </summary>
internal static bool TryParseSessionId(byte[] data, int offset, out uint sessionId)
```

Method now returns `bool` with an `out uint`. Change the second line to "Returns false if the slice is too short."

---

## 3. `IngressEngine.Nat.cs`

### 3a. STALE-DOC: `TryParseNatSessionId`

`IngressEngine.Nat.cs:225`
```csharp
/// <summary>
/// Parses a fixed-length ASCII session ID from a <see cref="PacketType.NatSessionCreated"/> payload.
/// Returns null if the payload is too short.
/// </summary>
private static bool TryParseNatSessionId(ReadOnlySpan<byte> payload, out uint sessionId)
```

Both the "ASCII" wording and the "Returns null" wording are stale. Replace with: "Parses the uint session ID from a `NatSessionCreated` payload. Returns false if the payload is too short."

### 3b. STYLE: stray blank lines inside the method body

`IngressEngine.Nat.cs:230, 236` have trailing whitespace and an empty first line inside the method, which violates Formatting.md.

---

## 4. `TransmissionEngine.Nat.cs`

### 4a. STALE-DOC: `SendNatServerPacketAsync`

`TransmissionEngine.Nat.cs:34`
```csharp
/// <summary>
/// Builds and sends a NAT server packet with the session identifier encoded as a fixed-length ASCII payload.
/// </summary>
```

Now written as a big-endian `uint`, not ASCII. Replace "as a fixed-length ASCII payload" with "as a 4-byte big-endian uint".

No behavioural issues found in this file. `IngressEngine.NatSessionIdBytes` is referenced, which is correct — but see §6 below about the duplication between `IngressEngine.NatSessionIdBytes` and `NatServer.SessionIdBytes`.

---

## 5. `NatHostSession.cs`

Clean. `SessionId` is `uint`, disposal ordering is sensible (`Cancel` + defer to `CloseNatHostSessionAsync`). No issues from the type change.

Minor observation: `_heartbeatCts` is never `Dispose()`d anywhere (neither `CloseAsync` nor `DisposeAsync`). This leaks the underlying `WaitHandle`. Pre-existing, not caused by the uint refactor, but worth fixing while in the area.

---

## 6. Cross-file: duplicated `SessionIdBytes` constants

Two separate constants now exist:
- `NatServer.SessionIdBytes` (internal, `SynapseSocket.NatServer`) — `NatServer.cs:33`
- `IngressEngine.NatSessionIdBytes` (internal, `SynapseSocket.Transport`) — `IngressEngine.Nat.cs:43`

Both equal `sizeof(uint)` and must stay in sync. Consider promoting a single `ServerNatConfig.SessionIdBytes` const so wire format drift cannot happen silently.

---

## Summary Table

| # | File | Severity | Issue | Status |
|---|---|---|---|---|
| 1a | SynapseManager.Nat.cs | RACE | `OnNatPeerReady` reads `_natHostPeerHandler` without the lock | RESOLVED |
| 1b | SynapseManager.Nat.cs | BUG | Peer-handler lambda captures `heartbeatCts`, which is disposed in error paths | RESOLVED |
| 1c | SynapseManager.Nat.cs | BUG | Lines 97/100 use `cancellationToken` instead of the linked `heartbeatCts.Token` | RESOLVED |
| 1d | SynapseManager.Nat.cs | RACE | `_natSessionSource` is read/written outside the lock in On* callbacks | RESOLVED |
| 2a | NatSessionRegistry.cs | RACE | Non-thread-safe `Dictionary<uint, Entry>` with timer-thread eviction | RESOLVED (`ConcurrentDictionary`) |
| 2b | NatSessionRegistry.cs | BUG | Session IDs come from `Random.Shared` — predictable | intentional (not crypto-sensitive) |
| 2c | NatSessionRegistry.cs | DEAD | `Entry.Joiners` written but never read | RESOLVED (deleted) |
| 2d | NatSessionRegistry.cs | STALE-DOC | `TryParseSessionId` summary still says "Returns null" | RESOLVED |
| 3a | IngressEngine.Nat.cs | STALE-DOC | `TryParseNatSessionId` summary says "ASCII" and "Returns null" | RESOLVED |
| 3b | IngressEngine.Nat.cs | STYLE | Stray blank line / trailing whitespace in method body | RESOLVED |
| 4a | TransmissionEngine.Nat.cs | STALE-DOC | `SendNatServerPacketAsync` summary says "fixed-length ASCII payload" | RESOLVED |
| 5 | NatHostSession.cs | BUG (minor) | `_heartbeatCts` is never disposed | RESOLVED (`DisposeAsync`) |
| 6 | cross-file | STYLE | Two copies of `SessionIdBytes` | RESOLVED (`NatWireFormat.SessionIdBytes`) |

---

## Recommended Order of Fixes

1. **2b** — session ID predictability is a security regression from the previous implementation. Revert to `RandomNumberGenerator`.
2. **1c** — one-line fix, removes a real cancellation hole.
3. **1a + 1b + 1d** — lock discipline around `_natHostPeerHandler` and `_natSessionSource`; delay lambda capture until after session ID is received.
4. **2a** — switch to `ConcurrentDictionary` or serialise eviction.
5. **2c** — remove dead `Joiners` field and its `//Unused?` comment.
6. **2d, 3a, 4a** — XML doc sweep.
7. **5** — dispose `_heartbeatCts` in `NatHostSession.CloseAsync`.
8. **6** — consolidate `SessionIdBytes` into a single shared constant.
