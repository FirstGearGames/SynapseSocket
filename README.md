# SynapseSocket

**A hardened UDP transport for .NET that you drive yourself: reliable and unreliable delivery over one socket, advanced only when you call `Poll()`.**

SynapseSocket carries two delivery channels over a single UDP socket, splits and reassembles payloads larger than the MTU, runs every inbound datagram through a security pipeline before its payload is touched, and can hole-punch through full-cone and address-restricted NAT. The companion **SynapseBeacon** project is a small rendezvous server that introduces peers to each other so they can punch.

The engine starts no background threads. `Connect`, `Send` and `Disconnect` put their datagrams on the wire immediately, on the calling thread. Everything else, meaning receiving, acknowledging, retransmitting, keep-alives, timeouts and NAT punching, happens inside `Poll()` on whichever thread calls it. If nothing calls `Poll()`, nothing arrives and nothing times out. SynapseBeacon is the exception to "no threads": a host session heartbeats from the thread pool, handing each heartbeat to the engine through the thread-safe `EnqueueRaw` so it still goes out on the next `Poll()`, and `BeaconServer` runs its own async receive loop.

---

## Why UDP

TCP's congestion control, head-of-line blocking and OS-level retransmission make it a poor fit for latency-sensitive work such as games, real-time simulation and peer-to-peer tools. SynapseSocket lets you choose per send whether delivery or speed matters more, and enforces that choice on the wire: a reliable message is acknowledged, retransmitted and delivered in order; an unreliable one is sent once and never waits on anything.

---

## Getting it

**Targets `netstandard2.1` and `net8.0`.** netstandard2.1 covers .NET Core 3.0+, .NET 5+ and Unity 2021.2+. There is no NuGet package: reference the projects, or build the DLLs and ship them.

**`CodeBoost.dll` is a runtime dependency.** `SynapseSocket.dll` is built against the shared CodeBoost assembly (pooling and utilities) rather than embedding a copy of it, so the two ship side by side. That is deliberate: Nucleus and every integration reference the same CodeBoost, so a CodeBoost type is one type across the whole stack, and Unity has no `extern alias` to tell two copies apart. On `netstandard2.1`, `System.Runtime.CompilerServices.Unsafe` is needed as well. `SynapseBeacon.dll` depends on `SynapseSocket.dll`.

### Building from source

```bash
git clone --recurse-submodules https://github.com/FirstGearGames/SynapseSocket.git
git clone https://github.com/FirstGearGames/CodeBoost.git
cd SynapseSocket
dotnet build
dotnet test SynapseSocket.Tests
```

The two clones must sit side by side. `SynapseSocket/SynapseSocket.csproj` references `..\..\CodeBoost\CodeBoost\CodeBoost.csproj`, so a missing sibling shows up as `CS0246` errors about CodeBoost types, which reads like a source problem but is a path problem. The `submodules/CodeBoost` submodule supplies only the build-time analyzers.

---

## Quick start

A server and a client in one process. The client connects, sends `hello` reliably, and the server echoes it back:

```csharp
using System.Net;
using System.Text;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;

using SynapseManager server = new(new SynapseConfig { BindEndPoints = [new(IPAddress.Loopback, 45000)] });
using SynapseManager client = new(new SynapseConfig { BindEndPoints = [new(IPAddress.Loopback, 0)] });

// Echo whatever arrives, on the channel it arrived on.
server.PacketReceived += e =>
{
    string text = Encoding.UTF8.GetString(e.Payload);   // e.Payload is only valid inside this handler
    server.Send(e.Connection, Encoding.UTF8.GetBytes($"echo: {text}"), e.IsReliable);
};

bool isDone = false;
client.ConnectionEstablished += e => client.Send(e.Connection, Encoding.UTF8.GetBytes("hello"), isReliable: true);
client.ConnectionFailed += e => Console.WriteLine($"connect failed: {e.Reason}");
client.PacketReceived += e =>
{
    Console.WriteLine(Encoding.UTF8.GetString(e.Payload));   // echo: hello
    isDone = true;
};

server.Start();
client.Start();
client.Connect(new IPEndPoint(IPAddress.Loopback, 45000));

// Connect and Send transmit immediately, but nothing is received, acknowledged, retried or timed out except inside Poll().
while (!isDone)
{
    server.Poll();
    client.Poll();
    Thread.Sleep(1);
}
```

Run it and it prints `echo: hello`. A few things in those lines are most of the model:

- **`Connect` returns at once**, with a connection still in the `Pending` state. `ConnectionEstablished` fires later, inside a `Poll`, on both sides. That is why the client sends from the event rather than straight after `Connect`. The reverse is not guaranteed: the accepting side is established as soon as it answers, so if that answer is lost or late, the connecting side can see `PacketReceived` for a connection that is still `Pending`. Do not assume state you create in `ConnectionEstablished` already exists in `PacketReceived`.
- **Handlers run synchronously on the engine's thread**, nearly all of them during `Poll`, so calling `Send` from a handler is safe.
- **`e.Payload` is borrowed.** Copy it if you need the bytes after the handler returns.
- **Port 0 lets the OS choose.** `BoundEndPoints` reports the endpoints the engine actually bound, including the port the OS picked.

`SynapseSocket.Demo` is a slightly longer version of the same exchange, adding a segmented payload and a telemetry printout.

---

## The poll loop

One call to `Poll()` does this, in order:

1. Drains every socket, up to `MaximumReceivesPerPoll` (4096) datagrams from each, running the security pipeline and raising events for each datagram.
2. Sweeps the handshake replay cache and the NAT probe table.
3. Advances pending NAT hole punches.
4. Runs maintenance on every connection: keep-alives, idle and handshake timeouts, handshake retries, reliable retransmission, segment-assembly timeouts.
5. Flushes batched acknowledgements.
6. Sends anything queued with `EnqueueRaw` from other threads.
7. Releases packets the latency simulator was holding back.
8. Returns connections closed since the previous poll to the pool, including any closed by `Disconnect` or `Connect` between polls.

Call it regularly, on the thread that calls `Send`: once per frame in a game loop, or in a loop on one dedicated thread. Do not drive it from a thread-pool timer such as `System.Threading.Timer` or `System.Timers.Timer`, whose callbacks move between threads and can overlap; the engine has no lock or thread check that would catch it. Intervals are measured on a monotonic clock, so a system clock change cannot time peers out, but a stalled `Poll` stalls retransmission and keep-alives along with everything else.

**The engine is single-threaded.** Call `Poll`, `Send`, `Connect`, `Disconnect`, `Start` and `Stop` from one thread. Only two things are safe from any other thread: `EnqueueRaw`, which queues raw bytes to be sent during the next `Poll`, and reading the `Telemetry` counters, which are read atomically so a UI thread or stats sampler can poll them directly.

---

## Events

| Event | Raised when |
|---|---|
| `ConnectionEstablished` | A handshake completes, on both sides |
| `ConnectionClosed` | A connection ends: timeout, kick, `Disconnect` (on both sides), or being replaced by a new `Connect` to the same endpoint. On the other side of such a reconnect, the fresh handshake raises `ConnectionClosed` and then at once `ConnectionEstablished` for the same connection object, which stays live and is not recycled |
| `ConnectionFailed` | A connection never happened; see [Connections](#connections) |
| `PacketReceived` | A payload arrives, reassembled if it was segmented |
| `PacketSent` | Synchronously inside `Send`, once per call |
| `ViolationDetected` | The security pipeline flags a peer. It also fires on every idle or handshake timeout (`Timeout`), when reliable retries run out (`ReliableExhausted`), and when a peer disconnects cleanly (`PeerDisconnect`), so not every violation is an attack. The handler's return value decides the response. See [Violations and bans](#violations-and-bans) |
| `UnknownPacketReceived` | A datagram with an unrecognised type byte arrives and `Security.AllowUnknownPackets` is true |
| `UnhandledException` | The engine's own work in `Poll` throws, or a `PacketReceived` or `UnknownPacketReceived` handler does |

**Exceptions thrown by handlers do not come back out of `Poll`, with one exception: the `UnhandledException` handler itself.** Exceptions from `PacketReceived` and `UnknownPacketReceived`, and most unexpected errors in the engine's own work, go to `UnhandledException`, which discards them if nothing is subscribed. Exceptions from `ConnectionEstablished`, `ConnectionClosed`, `ConnectionFailed` and `PacketSent` are dropped without being reported, and a `ViolationDetected` handler that throws gets that violation's default action. Four pieces of engine work swallow errors without reporting them at all: receiving from the socket, for every `SocketException` except an oversized datagram (so a Windows `ConnectionReset` from an ICMP port-unreachable is skipped silently); resending a reliable message; releasing a packet the latency simulator delayed; and applying a violation's action. So a socket error on receive, or a socket error or a throwing `IPacketTransform` during a resend, is never seen. Nothing guards `UnhandledException`, so an exception thrown from it leaves `Poll` and skips the rest of that poll. One case differs: while it is reporting a `PacketReceived` or `UnknownPacketReceived` exception, the per-datagram guard catches its throw and hands that back to `UnhandledException`, so the handler runs a second time with its own exception, and only a throw from that second call leaves `Poll`. Subscribe to it, log what it receives, and keep that handler from throwing, or a bug in a handler fails silently.

---

## Sending and receiving

`Send(connection, payload, isReliable)` picks the channel per call, and one connection carries both:

- **Reliable:** acknowledged, retransmitted every `Reliable.ResendMilliseconds` (250 ms) up to `Reliable.MaximumRetries` (10) times, delivered in order. Exhausting the retries drops the message and raises a `ReliableExhausted` violation whose default action, `Kick`, closes the connection locally without sending a disconnect packet. The message is gone whatever the handler returns. If the peer never received it, keeping the peer with `Ignore` or `Drop` stalls its reliable stream: the peer holds every later reliable message undelivered while it waits for the lost one, and once more than `Security.MaximumOutOfOrderReliablePackets` (64) are waiting it raises an `Oversized` violation and drops the connection.
- **Unreliable:** sent once, with no acknowledgement and no ordering.

A single unsegmented packet carries `MaximumPayloadSize` bytes, which is the MTU less 3 bytes of header: **1197 at the default 1200 MTU**. Larger payloads are split automatically and reassembled on arrival. Each segment carries the MTU less its header, 7 bytes for a reliable segment and 5 for an unreliable one, and a message may use at most 255 segments, or `Segment.MaximumSegments` when that is set. At the default MTU that caps a message at 304,215 bytes as reliable segments and 304,725 bytes as unreliable ones; a larger payload makes `Send` throw `InvalidOperationException`. **Check the size before sending rather than catching that exception.** A reliable send, or an unreliable one under `SegmentReliable`, has already used up a reliable sequence number when it throws, so it stalls the peer's reliable stream exactly as a lost message does.

`Segment.UnreliableMode` decides what happens to an oversized *unreliable* payload: split it unreliably (the default), split it into reliable segments, or refuse it with an exception. Turning off `Segment.ReliableEnabled` does not, on its own, make an oversized reliable send throw; see the [Segment](#segment) table.

On the receiving side, `PacketReceived` hands you the `Connection`, the `Payload` as an `ArraySegment<byte>`, and `IsReliable`. The payload is backed by a pooled buffer and is valid only for the duration of the handler.

Setting `CopyReceivedPayloads` to false removes the per-packet copy on the unreliable, unsegmented path: the segment then points straight into the engine's 64 KiB receive buffer. Handlers must honour its `Offset` and `Count`, since the rest of that array holds the packet-type byte and residue from earlier datagrams, and must not call `Poll` re-entrantly. Reliable and segmented receives always copy.

---

## Connections

- **`Connect`** sends a handshake immediately and returns a `Pending` connection. An unanswered handshake is retried every `Connection.HandshakeRetryIntervalMilliseconds` (300 ms), up to `Connection.HandshakeMaximumAttempts` (10) retries after that first send: 11 sends from this schedule. Full-cone NAT traversal adds a handshake to each probe burst on top of these. `Connect` to an endpoint that already has a connection tears the old one down first, raising `ConnectionClosed` for it.
- **`ConnectionEstablished`** fires on both sides once the handshake completes. `Connections` lists every connection, pending ones included, through `Connections.Connections`, `ConnectionsByEndPoint` and `Count`.
- **Keep-alives** go out after `Connection.KeepAliveIntervalMilliseconds` (5 s) without sending anything. A peer that sends nothing for `Connection.TimeoutMilliseconds` (15 s) is timed out.
- **A handshake still unanswered** after `Connection.HandshakeTimeoutMilliseconds` is timed out as well. That setting is unset by default, which falls back to the 15 s idle timeout. The handshake clock starts when the handshake began, so other traffic from the peer cannot hold it open.
- **`Disconnect`** raises `ConnectionClosed` locally before it returns, and sends the peer a single disconnect packet, on which the peer raises `ConnectionClosed` and a `PeerDisconnect` violation. The packet is never retransmitted. If it is lost, the peer finds out through its own timeouts, because this side now drops everything the peer sends: the idle timeout always catches it, and a reliable message the peer has in flight catches it sooner, exhausting its retries (about 2.75 s at the defaults) and raising `ReliableExhausted`.
- **`Stop` and `Dispose` drop every connection silently.** No disconnect packet is sent and no `ConnectionClosed` fires on either side, so peers notice only through their own timeouts: the idle timeout, or sooner a `ReliableExhausted` if they have reliable messages unacknowledged. To shut down cleanly, `Disconnect` each connection first, walking `Connections.Connections` from the end because each `Disconnect` removes an entry. A stopped engine can be started again with `Start`; a disposed one cannot, and `Start` throws `ObjectDisposedException`. `IsRunning` is false after either, so it cannot tell the two apart.

**Timeouts close the connection first.** Both kinds raise `ConnectionClosed` and only then report a `Timeout` violation, whose default action is `Kick`. The timed-out connection is already gone, so returning `Ignore` or `Drop` from a handler cannot keep it open, and the violation's `Connection` is normally null. The exception is a `ConnectionClosed` handler that immediately calls `Connect` to the same endpoint: the violation then carries that new connection, and the default `Kick` tears it straight down. If you reconnect from `ConnectionClosed`, return `Ignore` for `Timeout`, or call `Connect` after `Poll` returns.

**`ConnectionFailed`** reports rejected connection attempts, with one of these reasons: `BindFailed`, `SignatureRejected`, `Blacklisted`, `NatTraversalFailed` and `ServerFull`. (`ConnectionRejectedReason.Timeout` exists in the enum but is not raised.) It fires once per rejected datagram, not once per connection, so a refused peer's handshake retries raise it again each time, and a blacklisted endpoint raises `Blacklisted` for every datagram it sends. `BindFailed` names a local bind endpoint, not a peer. Two things about it are easy to get wrong:

- **A refusal is reported only on the side that refuses.** `ServerFull`, `SignatureRejected` and a blacklisted inbound handshake are raised by the engine that rejects the handshake, and the rejected peer is never told. It may get a reply first: at or above `HandshakeChallengeThreshold` (1024, below the default cap of 4096, so always the case when the engine is full) a new peer's handshake is answered with a challenge, and `ServerFull` or `SignatureRejected` is raised only when the peer's automatic echo arrives. A blacklisted peer is never challenged: `Blacklisted` is raised as soon as its datagrams arrive, at any occupancy. On the rejected peer's side the connection stays `Pending`, keeps retrying, and ends as a handshake timeout. `Connect` to an endpoint *this* side has blacklisted is different: it raises `ConnectionFailed(Blacklisted)` and then throws `InvalidOperationException`.
- **`NatTraversalFailed` does not close the connection.** It only stops the probing. The connection stays `Pending`, still establishes if the peer's handshake gets through, and is otherwise closed by the handshake timeout like any other.

---

## Configuration reference

`SynapseConfig` uses public fields with working defaults. Nested groups are set with object initializers, for example `Segment = { MaximumSegments = 128 }`.

### `SynapseConfig`

| Field | Default | Meaning |
|---|---|---|
| `BindEndPoints` | *(required)* | Local endpoints to bind. At most one per address family, so one IPv4 plus one IPv6 for dual-stack; two of the same family throw at `Start`. An endpoint that fails to bind raises `ConnectionFailed(BindFailed)` from inside `Start`, which throws only when *no* endpoint binds, so subscribe first and check `BoundEndPoints` afterwards |
| `MaximumPacketSize` | 1400 | Largest inbound datagram accepted; anything larger is an `Oversized` violation |
| `MaximumTransmissionUnit` | 1200 | Size outbound packets are packed to, less a `PacketTransform`'s `ReservedBytes` (batched acknowledgements excepted; see [Packet transform](#packet-transform)). It also bounds what this side accepts: a received segment larger than this side's MTU is a `Malformed` violation, so give peers the same value |
| `PacketTransform` | `null` | Optional `IPacketTransform` rewriting Synapse packet payloads in both directions. Segment acknowledgements and raw external-protocol datagrams bypass it |
| `MaximumConcurrentConnections` | 4096 | Cap on connections admitted by inbound handshake. It counts every connection, pending and outbound ones included, but `Connect` itself is never refused, so outbound connections can take the count past it. A new peer's handshake beyond it is dropped and raises `ConnectionFailed(ServerFull)` on this engine; the joining peer is never told and times out. 0 disables |
| `MaximumReceivesPerPoll` | 4096 | Datagrams drained from each socket per `Poll` before it moves on, so a flood cannot hold the loop. 0 disables |
| `NativeReceiveEnabled` | `true` | Uses direct `recvfrom`/`sendto` bindings on netstandard2.1, where the managed API allocates per datagram. No effect on net8.0, which already allocates nothing |
| `SocketReceiveBufferBytes` | 1 MiB | OS receive buffer for each socket |
| `SocketSendBufferBytes` | 0 | OS send buffer; 0 leaves the OS default |
| `CopyReceivedPayloads` | `true` | See [Sending and receiving](#sending-and-receiving) |
| `EnableTelemetry` | `false` | Turns on the `Telemetry` counters |
| `ConnectedSocketEnabled` | `false` | OS-connects the bound socket of the matching address family to the remote passed to `Connect` (each `Connect` re-points it), skipping per-datagram endpoint handling. Only for an engine that talks to exactly one peer, and cannot be combined with full-cone NAT traversal |

### `Segment`

| Field | Default | Meaning |
|---|---|---|
| `ReliableEnabled` | `true` | Split oversized reliable payloads, and accept reliable segments. When false, this side drops reliable segments unacknowledged, so a sender ends in `ReliableExhausted`; an oversized reliable send throws only if `UnreliableMode` is also `Disabled`, and otherwise still goes out as reliable segments |
| `UnreliableMode` | `SegmentUnreliable` | Oversized unreliable payloads: `SegmentUnreliable`, `SegmentReliable`, or `Disabled` (the send throws). `Disabled` also makes this side silently drop unreliable segments it receives, with no violation or event, so a peer on `SegmentUnreliable` never gets its oversized unreliable messages through |
| `MaximumSegments` | 0 | Most segments one message may use, in both directions: a send that needs more throws, and a received message declaring more is dropped. 0 means the protocol maximum of 255 |
| `MaximumConcurrentAssembliesPerConnection` | 16 | Incomplete messages a peer may have in flight; one more is a `Malformed` violation. 0 disables |
| `AssemblyTimeoutMilliseconds` | 5000 | Incomplete messages older than this, counted from their first segment, are discarded. 0 disables; above 300000 the constructor throws |

### `Reliable`

| Field | Default | Meaning |
|---|---|---|
| `MaximumPending` | 256 | Unacknowledged reliable messages per connection; the next reliable send throws |
| `ResendMilliseconds` | 250 | Retransmit interval |
| `MaximumRetries` | 10 | Retransmits before the message is dropped and `ReliableExhausted` is raised |
| `AckBatchingEnabled` | `true` | Coalesce acknowledgements and flush them once per `Poll` |

### `Connection`

| Field | Default | Meaning |
|---|---|---|
| `KeepAliveIntervalMilliseconds` | 5000 | Send-silence before a keep-alive goes out |
| `TimeoutMilliseconds` | 15000 | Receive-silence before a peer is timed out |
| `HandshakeTimeoutMilliseconds` | 0 | How long a `Pending` connection may wait, from when its handshake began. 0 falls back to `TimeoutMilliseconds` |
| `HandshakeRetryIntervalMilliseconds` | 300 | Interval between handshake retries |
| `HandshakeMaximumAttempts` | 10 | Handshake retries after the one `Connect` sends, so 11 sends in total at the default. Caps the retries only; the timeout still ends the attempt |

### `Security`

| Field | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | When false, per-connection rate and size enforcement is skipped. Pre-connection checks, the reorder cap and the assembly caps still apply |
| `MaximumPacketsPerSecond` | 2000 | Per-peer packet rate, counted over a fixed one-second window. 0 disables |
| `MaximumBytesPerSecond` | 2 MiB | Per-peer byte rate, over the same window. 0 disables |
| `MaximumOutOfOrderReliablePackets` | 64 | Reliable messages a peer may have buffered ahead of a gap; one more is an `Oversized` violation. 0 disables |
| `MaximumReassembledPacketSize` | 0 | Cap on a segmented message's declared segment count × effective MTU; going over is an `Oversized` violation. It is not the message's length: each segment also carries a header, so size it from segments × MTU rather than from your largest message. 0 disables |
| `HandshakeChallengeThreshold` | 1024 | Live connections at or above which an unknown endpoint must return a stateless token before any state is allocated for it. 0 never challenges |
| `ViolationsBeforeBlacklist` | 5 | `KickAndBlacklist` violations inside the window that earn a ban |
| `ViolationWindowMilliseconds` | 10000 | The window violations are counted over. It opens at the first violation and does not slide |
| `BlacklistDurationMilliseconds` | 300000 | How long a ban lasts. 0 makes bans permanent |
| `SignatureProvider` | `null` | Custom `ISignatureProvider`; `null` uses `DefaultSignatureProvider` |
| `SignatureValidator` | `null` | Optional `ISignatureValidator` consulted on every handshake |
| `AllowUnknownPackets` | `false` | Hand datagrams with an unrecognised type byte to `UnknownPacketReceived` instead of raising an `UnknownPacket` violation outright. The handler's `FilterResult` decides: anything other than `Allowed` still raises `UnknownPacket`. With no handler, or a handler that throws, the datagram passes. **SynapseBeacon requires `true`** |

### `NatTraversal`

| Field | Default | Meaning |
|---|---|---|
| `Mode` | `Disabled` | `FullCone` to hole-punch on `Connect` |
| `ProbeCount` | 3 | Probes per punch burst |
| `IntervalMilliseconds` | 200 | Time between bursts. Also the minimum gap between probe replies to one source address |
| `MaximumAttempts` | 10 | Bursts before `ConnectionFailed(NatTraversalFailed)` |
| `FullCone.DirectAttemptMilliseconds` | 500 | Grace period for a direct handshake before probing starts |

### `LatencySimulator`

| Field | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Delay, drop and reorder this engine's outbound packets. Its own handshake, NAT probe and NAT challenge packets skip it; raw datagrams from `SendRaw`/`EnqueueRaw`, including SynapseBeacon's, do not |
| `BaseLatencyMilliseconds` | 0 | Fixed delay |
| `JitterMilliseconds` | 0 | Random extra delay |
| `PacketLossChance` | 0.0 | Probability a packet is dropped |
| `ReorderChance` | 0.0 | Probability a packet is held back |
| `OutOfOrderExtraDelayMilliseconds` | 100 | Random extra delay for a reordered packet, from 0 up to this value |

---

## Security model

Every inbound datagram is filtered before its payload is copied.

**From an unknown sender:** the signature is computed, and if the provider cannot produce one the datagram is dropped with a `Malformed` violation carrying signature 0, which never counts toward a ban. A blacklisted signature is dropped with `ConnectionFailed(Blacklisted)`, and anything over `MaximumPacketSize` is an `Oversized` violation.

**From a connected peer:** size and the per-second packet and byte limits are checked, raising `Oversized` or `RateLimitExceeded`.

**While processing the packet:**

| Condition | Result |
|---|---|
| Unrecognised type byte | `UnknownPacket` violation. With `AllowUnknownPackets`, it goes to `UnknownPacketReceived` instead, and a return value other than `FilterResult.Allowed` still raises `UnknownPacket` |
| Header does not parse | `Malformed` violation |
| Handshake from a blacklisted signature | `ConnectionFailed(Blacklisted)` |
| Handshake from an unknown endpoint at or above `HandshakeChallengeThreshold` | Answered with a stateless token; no state is allocated until it comes back |
| Handshake beyond `MaximumConcurrentConnections` | `ConnectionFailed(ServerFull)` |
| Replayed handshake | `ConnectionFailed(SignatureRejected)` |
| Validator returns false | `ConnectionFailed(SignatureRejected)` |
| Reorder buffer over its cap, or a declared segment count × MTU over `MaximumReassembledPacketSize` | `Oversized` violation |
| Too many messages in reassembly at once, segment headers that contradict each other, or a segment larger than this side's MTU | `Malformed` violation. On the reliable channel, a segment carrying the last index of its message, including any single-segment message, is dropped without one |
| Packet transform rejects the payload | `TransformRejected` violation |

### Violations and bans

Each violation raises `ViolationDetected`, and **the handler's return value is the engine's decision**:

| Action | Effect |
|---|---|
| `Ignore`, `Drop` | The peer stays connected. Not for `Timeout` and `PeerDisconnect`, which are raised after the connection has already closed |
| `Kick` | The connection is closed on this side and `ConnectionClosed` fires. No disconnect packet is sent, so the peer finds out only through its own timeouts. To remove a peer *and* tell it, call `Disconnect` |
| `KickAndBlacklist` | As `Kick`, and the violation counts toward a ban |

A ban is not immediate. It lands once a signature reaches `ViolationsBeforeBlacklist` (5) `KickAndBlacklist` violations inside `ViolationWindowMilliseconds` (10 s), and it lasts `BlacklistDurationMilliseconds` (5 minutes). The window opens at the first violation and does not slide, so a burst that straddles the end of a window can go past 5 without a ban. Over UDP the source address is forgeable, so banning on a single datagram would let one forged packet lock any endpoint out.

```csharp
server.ViolationDetected += e =>
{
    // The return value is the decision. e.Action is the engine's default for this reason; assigning to it
    // does nothing, because the event args are a struct passed by value.
    if (e.Reason == ViolationReason.Oversized && e.Connection is not null)
        return ViolationAction.Drop;   // keep an established peer connected for any Oversized reason

    return e.Action;
};
```

`Oversized` from an established peer covers more than an oversized datagram: it is also raised for a reorder-buffer overflow and for a segmented message whose declared segment count × effective MTU exceeds `MaximumReassembledPacketSize`. The sample keeps the peer connected in all three cases, but that does not keep reliable delivery working. A reliable message dropped on reorder overflow has already been acknowledged, so the sender never resends it and this side's reliable stream stops there for good; a reliable segmented message dropped for its declared size is never acknowledged, so the sender eventually gives up with `ReliableExhausted`. With several subscribers, only the last one's return value counts.

With no handler attached, the engine applies its default for each reason. `Oversized`, `Malformed` and `UnknownPacket` default to `KickAndBlacklist`. Timeouts and retransmit exhaustion default to `Kick`. `RateLimitExceeded` and `TransformRejected` default to `Drop`: the offending packets are discarded, the peer stays connected, and nothing counts toward a ban. A peer's own clean disconnect also arrives here, as `PeerDisconnect` with a default of `Ignore`, so a handler that returns `KickAndBlacklist` unconditionally would count ordinary disconnects toward bans.

### Peer identity

`DefaultSignatureProvider` hashes the remote **IP address and port** with FNV-1a, which keeps clients behind one NAT address distinct. The consequence is that **a ban is per IP:port, not per IP**: a banned peer that reconnects from a new source port has a new signature. A custom `ISignatureProvider` can key on the address alone, at the cost of every client behind one address sharing a signature.

`ISignatureValidator.Validate` receives the endpoint, the signature and the handshake payload, and rejecting raises `ConnectionFailed(SignatureRejected)`. The built-in handshake carries only a random 8-byte nonce. Once live connections reach `HandshakeChallengeThreshold`, the payload a new peer's validator sees is 16 bytes: the nonce followed by the 8-byte token the peer echoed back. `Connect` offers no way to add application data, so today a validator is suited to allowing or denying by endpoint or signature rather than checking a token.

---

## Packet transform

`IPacketTransform` is a single hook for rewriting the bytes of Synapse packets as they enter or leave the socket: encryption, compression, obfuscation, or a custom integrity check, with no change to any send or receive call site. Assign an implementation to `SynapseConfig.PacketTransform` and both directions are covered.

**One packet type is not covered.** The selective segment acknowledgement (`PacketType.SegmentAck`) currently bypasses the transform in both directions, so its message sequence and segment bitmap travel as plain bytes, and a transform used for encryption or integrity neither hides nor authenticates them.

A transform only ever sees the payload region. The Synapse header is copied through verbatim, which is what lets the receiving side identify a packet before reversing the transform and lets external protocols that piggyback on the socket (`SendRaw` / `UnknownPacketReceived`) keep flowing untouched. Returning `false` from an inbound `TryTransform` discards the packet and raises a `TransformRejected` violation.

**Reserved MTU.** A transform that adds bytes would otherwise push packets past the MTU. `IPacketTransform.ReservedBytes` declares that growth up front and the engine deducts it from the configured MTU before packing anything:

```csharp
config.MaximumTransmissionUnit = 1400;   // the wire ceiling
config.PacketTransform = new MyEncryption();  // ReservedBytes => 20

engine.MaximumTransmissionUnit;  // 1380: what the engine packs against
engine.MaximumPayloadSize;       // 1377: largest unsegmented payload
```

Data, segment and control packets therefore land at or under the 1400 that was configured. **Batched acknowledgements are the exception:** they are still sized against the configured MTU rather than the reduced one, so a batch near the maximum of 699 acks to one peer can land up to `ReservedBytes` over it, and a receiver whose `MaximumPacketSize` equals that MTU treats it as `Oversized`. Both peers must run an equivalent transform.

### Worked example: `ExampleXorPacketTransform`

> **This example is not secure and must never protect real traffic.** It XOR-masks each payload with a four-byte value and then writes that value into the packet in front of the masked bytes, so anyone reading the packet can undo it with no key and no effort. It authenticates nothing either: a tampered packet yields garbage, not an error. It exists to show the shape of a transform and, more importantly, the length accounting. Replace it with a real authenticated cipher before shipping.

```csharp
config.MaximumTransmissionUnit = 1400;
config.PacketTransform = new ExampleXorPacketTransform();   // ReservedBytes => 4

engine.MaximumTransmissionUnit;  // 1396
engine.MaximumPayloadSize;       // 1393  → 1396 + 4 = 1400 on the wire
```

The mask makes every payload exactly four bytes longer, `ReservedBytes` declares those four bytes, and the engine deducts them up front, so masked data and segment packets never outgrow the configured MTU (batched acknowledgements aside, as above). A fresh mask is drawn per packet, so identical payloads produce different datagrams; that is visible on the wire but buys no secrecy, because the mask ships beside the data it masks.

---

## NAT traversal

### Full-cone hole punching

When both peers know each other's external endpoint, set `FullCone` on **both** of them and call `Connect`:

```csharp
SynapseConfig config = new()
{
    BindEndPoints = [new(IPAddress.Any, 0)],
    NatTraversal = { Mode = NatTraversalMode.FullCone },
};
```

`Connect` sends a handshake straight away. If the connection is still pending after 500 ms, it sends a burst of 3 probes and a handshake every 200 ms, up to 10 bursts, and then raises `ConnectionFailed(NatTraversalFailed)`: about 2.5 seconds in all. That event ends the probing but does not close the connection, which still establishes if the peer's handshake gets through and is otherwise closed by the handshake timeout. A probe is answered with an address-bound challenge rather than a handshake, so a probe forged from someone else's address cannot make the engine send that address a handshake.

This works through full-cone and address-restricted-cone NAT. There is no relay, so a symmetric NAT on either side ends in `NatTraversalFailed`. `ConnectedSocketEnabled` cannot be combined with `FullCone`, because an OS-connected socket cannot receive the probes (`Connect` throws). A `HandshakeTimeoutMilliseconds` set below the punch schedule is rejected by the constructor, because it would end the connection mid-punch. When it is left unset the handshake is held to `TimeoutMilliseconds` instead, and that value is *not* checked, so under `FullCone` do not lower `TimeoutMilliseconds` below the schedule (2500 ms at the defaults).

### Rendezvous with SynapseBeacon

Server-assisted rendezvous, where a small signalling service matches peers and exchanges their external endpoints before hole-punching, lives in the companion **[SynapseBeacon](SynapseBeacon/)** project. SynapseBeacon piggybacks on the Synapse UDP socket via the `SynapseManager.EnqueueRaw` + `UnknownPacketReceived` extension hooks, so the NAT mapping opened to the beacon server is the same mapping used for peer-to-peer traffic after the punch.

```csharp
SynapseConfig config = new()
{
    BindEndPoints = [new(IPAddress.Any, 0)],
    NatTraversal = { Mode = NatTraversalMode.FullCone },
    Security = { AllowUnknownPackets = true },   // beacon replies arrive as unknown packets
};

using SynapseManager synapse = new(config);
synapse.Start();

using BeaconClient beacon = new(synapse, new BeaconClientConfig(new IPEndPoint(beaconAddress, 47776)));

// Host: create a session, share session.SessionId with players, punch back at each joiner.
Task<BeaconHostSession> hosting = beacon.HostAsync(cancellationToken);
PollUntil(synapse, hosting);
BeaconHostSession session = hosting.GetAwaiter().GetResult();

session.PeerReady += joiner =>   // raised inside Poll, so this is already the polling thread
{
    if (!synapse.Connections.ConnectionsByEndPoint.ContainsKey(joiner))
        synapse.Connect(joiner);
};

// Join: resolve the host's external endpoint, then punch from the polling thread.
Task<IPEndPoint> joining = beacon.JoinAsync(sessionId, cancellationToken);
PollUntil(synapse, joining);
synapse.Connect(joining.GetAwaiter().GetResult());

// The beacon's replies arrive through the engine's socket, so poll while waiting for them.
static void PollUntil(SynapseManager synapse, Task task)
{
    while (!task.IsCompleted)
    {
        synapse.Poll();
        Thread.Sleep(1);
    }
}
```

**`HostAsync` and `JoinAsync` complete only while something keeps calling `synapse.Poll()`**, because the request goes out and the reply comes in through the engine's socket. Waiting on them without polling can never succeed: the call ends in a `TimeoutException` after `BeaconClientConfig.ResponseTimeoutMilliseconds` (10 s). A rejection fails them sooner with `InvalidOperationException`: `JoinAsync` when the session ID is unknown or expired, `HostAsync` when the server is at its session cap, and either one straight away if the same request is already running on that client.

**Mind which thread calls `Connect`.** `PeerReady` is raised inside `Poll`, so connecting from that handler is safe. Code after a plain `await beacon.JoinAsync(...)`, though, resumes on a thread-pool thread unless the app has a synchronization context that brings it back to the polling thread. Unity's main thread has one, so with `Poll` in `Update` a plain `await` is fine; a console or server app has none, which is why the sample polls until the task completes and then carries on from the polling thread.

The host connects back to each joiner because address-restricted NAT drops the joiner's packets until the host has sent something to the joiner's address. The guard matters: `Connect` tears down any existing session to that endpoint, so if the joiner's handshake has already arrived, an unguarded `Connect` would close a working connection and start over.

**A host session stays open until you close it.** Its heartbeats, every `HeartbeatIntervalMilliseconds` (30 s), keep it registered, and anyone holding the ID can keep joining and raising `PeerReady`. `CloseAsync`, `Dispose` or `DisposeAsync` stops the heartbeats and asks the server to drop the session. That request is queued and goes out on the next `Poll`, so poll at least once more before disposing the `SynapseManager`; otherwise the server keeps the session until it evicts it for missing heartbeats.

Joining takes two round trips rather than one. The first `JoinSession` is answered with a `JoinChallenge` carrying a cookie bound to the joiner's own address; the joiner repeats the join with that cookie, and only the second request is matched. The challenge is sent before the session is looked up, so it is identical whatever ID was named.

This costs one extra round trip per joiner, once, during matchmaking. Nothing is added to the data path, and established peer traffic never touches the beacon. What it buys is that a forged `JoinSession` naming a victim's address cannot make a host aim a hole-punch burst at that victim: a source that cannot receive at the address it claimed never obtains a cookie. The beacon server also rate-limits each source address to 20 requests per second, and acknowledges a heartbeat only to the session's own host.

**Running the server.** `SynapseBeacon.Host` runs a `BeaconServer` as a console service until Ctrl+C:

```bash
dotnet run --project SynapseBeacon.Host -- --port 47776 --session-timeout-ms 300000 --max-sessions 0
```

Those are the defaults: UDP port 47776, a session evicted after 5 minutes without a host heartbeat, and no cap on concurrent sessions. The server listens on IPv4 only, on every IPv4 interface, so point `BeaconClientConfig.ServerEndPoint` at one of its IPv4 addresses; aimed at an IPv6 address it never replies. Session IDs are cryptographically random 32-bit values. To embed the server instead, construct `BeaconServer` and await its `RunAsync`.

---

## Telemetry and latency simulation

With `EnableTelemetry` set, `SynapseManager.Telemetry` counts `BytesIn`, `BytesOut`, `PacketsIn`, `PacketsOut`, `PacketsDroppedIn`, `ReliableResends` and `PacketsLost`. The counters cost nothing when telemetry is off, and they are safe to read from any thread. A `PacketsDroppedOut` property also exists, but nothing increments it, so it always reads 0; packets the latency simulator drops are not counted anywhere.

The latency simulator delays, drops and reorders **this engine's outbound** packets, which is useful for seeing how an application behaves on a bad network without needing one. Synapse's own connection-setup packets (handshakes, NAT probes and NAT challenges) are exempt and always go out immediately, so a direct connection still establishes even with `PacketLossChance` at 1.0. Everything else is degraded, including raw sends from `SendRaw` and `EnqueueRaw` such as SynapseBeacon's requests. Those are never retried, so a dropped beacon request ends `HostAsync` or `JoinAsync` in a `TimeoutException`, and at 1.0 a beacon-mediated connection never gets as far as its handshake. To degrade both directions, enable it on both engines. Delayed packets are released during `Poll`.

---

## Things that look like bugs but are not

- **Nothing arrives, and nothing times out, without `Poll`.** Sends go out immediately, but receiving, retransmitting and every timeout run inside `Poll`.
- **An unreliable send made before `ConnectionEstablished` can be lost.** It is delivered only if the peer has already processed your handshake when it arrives. If it overtakes the handshake, the handshake is lost, or a busy peer answers with a challenge, the peer has no connection for you yet and drops it. Wait for `ConnectionEstablished` before sending unreliably. Reliable sends made that early are retransmitted, so they arrive.
- **A `SynapseConnection` is reused after it closes.** Connection objects are pooled and returned at the end of the `Poll` that closed them, or of the next `Poll` if they were closed outside one by `Disconnect` or `Connect`. After that their fields are reset and the instance can represent a different peer. Do not hold one past `ConnectionClosed`, and drop your references when you call `Stop` or `Dispose`, which recycle every connection without raising it.
- **The 257th unacknowledged reliable send throws** `InvalidOperationException: Reliable backpressure limit reached.` That is `Reliable.MaximumPending` doing its job.
- **A flooding peer is not kicked.** `RateLimitExceeded` defaults to `Drop`: the excess packets are discarded and the peer stays connected.
- **`KickAndBlacklist` does not ban on the first offence.** It takes 5 of them inside a fixed 10-second window, and the ban expires after 5 minutes.
- **A ban does not stop a peer reconnecting from another port.** The default signature includes the port.
- **Assigning `e.Action` in a violation handler changes nothing.** Return the action instead. With several subscribers, only the last one's return value counts.
- **A timeout never shows up in `ConnectionFailed`.** Both idle and handshake timeouts arrive as `ConnectionClosed` plus a `Timeout` violation, even for a connection that was never established.
- **A refused peer is never told.** When this side rejects a handshake as `ServerFull`, `SignatureRejected` or `Blacklisted`, `ConnectionFailed` fires here and nothing is sent back to say so. At or above `HandshakeChallengeThreshold` the other side may first get a challenge and answer it, but its handshake is still never accepted, so it eventually times out.
- **`NatTraversalFailed` leaves the connection pending.** It stops the probing; the handshake timeout closes the connection later.
- **Peers with different MTUs can kick each other.** A received segment larger than this side's MTU is `Malformed`, and a datagram larger than `MaximumPacketSize` (1400) is `Oversized`. Give every peer the same MTU, at or below every peer's `MaximumPacketSize`.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| No `ConnectionEstablished`, nothing received | `Poll` is not being called on one side, or the port is blocked | Poll both engines regularly; check the bind endpoint and the firewall |
| One side's `Connect` never establishes while the other side logs `ConnectionFailed` | The other side refused the handshake and never sent a refusal (at most a handshake challenge) | On the refusing side: `ServerFull` means it reached `MaximumConcurrentConnections`; `SignatureRejected` means its `Security.SignatureValidator` returned false or the handshake was a replay, and `e.Message` says which; for `Blacklisted`, see the `ConnectionFailed(Blacklisted)` row further down |
| Payload bytes are wrong when read after the handler | `e.Payload` was kept past the handler | Copy it inside the handler |
| A stored `SynapseConnection` has a null endpoint or someone else's | It was closed and returned to the pool | Drop references on `ConnectionClosed`, `Stop` and `Dispose` |
| `InvalidOperationException: Reliable backpressure limit reached.` | Reliable sends outpacing acknowledgements | Send less often, or raise `Reliable.MaximumPending` |
| `InvalidOperationException` that a payload exceeds the MTU-based limit | Unreliable send: `Segment.UnreliableMode` is `Disabled`. Reliable send: `Segment.ReliableEnabled` is false *and* `Segment.UnreliableMode` is `Disabled` | Reliable send: set `Segment.ReliableEnabled` on **both** peers; turning on only `Segment.UnreliableMode` stops the throw, but the payload still goes out as reliable segments, which a receiver with `ReliableEnabled` off drops unacknowledged. Unreliable send: enable `Segment.UnreliableMode` on the sender, and make sure the receiver can take what it sends: `UnreliableMode` enabled for `SegmentUnreliable`, or `ReliableEnabled` on for `SegmentReliable`. Or send less |
| An engine keeps raising `ConnectionFailed(Blacklisted)` for one endpoint, and that peer never connects | This engine has banned that peer's signature; the peer is sent nothing | Wait out `BlacklistDurationMilliseconds` (5 minutes), or on the banning engine call `Security.RemoveFromBlacklist(Security.ComputeSignature(endPoint, ReadOnlySpan<byte>.Empty))`. A ban added with `AddToBlacklist` never expires |
| `Start` throws about an address family | Two bind endpoints of the same family | Bind at most one IPv4 and one IPv6 endpoint |
| `Connect` throws with full-cone traversal enabled | `ConnectedSocketEnabled` is also set | Turn one of them off |
| The constructor throws about the hole-punch schedule | `HandshakeTimeoutMilliseconds` is shorter than the punch | Leave it unset, or set it to at least `FullCone.DirectAttemptMilliseconds` + `MaximumAttempts` × `IntervalMilliseconds` (2500 ms at the defaults; the exception message states the exact figure) |
| `ConnectionFailed(NatTraversalFailed)` | The punch exhausted its bursts | Check both peers use `FullCone` and dial each other's *external* endpoint; symmetric NAT cannot be punched |
| `BeaconClient` constructor throws `InvalidOperationException` | `Security.AllowUnknownPackets` is false | Set it to true |
| `HostAsync` or `JoinAsync` throws `TimeoutException` | The engine is not being polled while the call is outstanding, the beacon server is unreachable, or a request or reply was lost: the client sends each request once and never retransmits it | Keep calling `Poll` while waiting; check `BeaconClientConfig.ServerEndPoint` (an IPv4 address) and that the server is running; catch `TimeoutException` and call again |
| `CS0246` errors about CodeBoost types when building | The sibling `CodeBoost` checkout is missing | Clone CodeBoost beside SynapseSocket; see [Building from source](#building-from-source) |

---

## Project layout

```
SynapseSocket/
├── Core/
│   ├── SynapseManager.cs              public API: lifecycle, Poll, Connect/Send, events
│   ├── SynapseManager.Maintenance.cs  keep-alive, timeouts, retransmission (run from Poll)
│   ├── SynapseManager.Nat.cs          full-cone hole-punch scheduling
│   ├── Clock.cs                       monotonic tick source for every interval
│   ├── IPEndPointComparer.cs          value-equality comparer for IPEndPoint keys
│   ├── Configuration/                 SynapseConfig and its nested groups
│   └── Events/                        handlers, event args, violation and rejection enums
├── Transport/
│   ├── IngressEngine.cs               per-socket receive drain, security pipeline, dispatch
│   ├── IngressEngine.Nat.cs           NAT probe and challenge handling
│   ├── TransmissionEngine.cs          send path, reliable queue, keep-alives, disconnects
│   ├── TransmissionEngine.Nat.cs      NAT probe and challenge sends
│   └── NativeSocket.cs                allocation-free recvfrom/sendto for netstandard2.1
├── Connections/                       ConnectionManager and per-peer SynapseConnection state
├── Packets/                           header, segmentation, reassembly, IPacketTransform
├── Security/                          SecurityProvider, signature provider and validator
└── Diagnostics/                       Telemetry, LatencySimulator

SynapseBeacon/
├── Client/                            BeaconClient, BeaconHostSession, BeaconClientConfig
├── Server/                            BeaconServer, BeaconSessionRegistry
└── Wire/                              packet types and wire format

SynapseSocket.Demo/                    minimal client/server exchange
SynapseBeacon.Demo/                    rendezvous with one host and two joiners, in one process
SynapseBeacon.Host/                    command-line beacon server
SynapseSocket.Tests/                   the test suite
```

---

## Tests

132 xUnit tests in 19 suites. Almost all of them spin up real engines over loopback UDP sockets, with no mocking. Adversarial tests send hand-built datagrams from a plain `Socket`, the same thing a hostile peer on the network does.

| Suite | Tests | Coverage |
|---|---|---|
| `HandshakeAndChannelTests` | 6 | Handshake on both sides, unreliable and reliable delivery, in-order delivery, echo from a callback, graceful disconnect |
| `EngineLifecycleTests` | 8 | Start and bind, double start, a config with no bind endpoints or a null config, received payloads copied by default, dispose, send before start, bind failure |
| `KeepAliveAndTimeoutTests` | 3 | Idle timeout, keep-alive preventing it, a receive-only peer surviving |
| `HandshakeTimeoutTests` | 5 | A pending handshake timed out from when it began, the unset fallback, the full-cone schedule check |
| `ConnectedSocketTests` | 2 | OS-connected sockets in both directions, and the full-cone combination throwing |
| `ConnectionsListIntegrityTests` | 3 | Every connection's recorded list index matching its slot across removals |
| `SegmentationTests` | 3 | A large unreliable payload split and reassembled, an oversized reliable send throwing when segmentation is disabled, a declared assembly size above `MaximumReassembledPacketSize` rejected |
| `OutOfOrderTests` | 3 | Reordered segmented and unsegmented traffic arriving intact |
| `PacketTransformTests` | 7 | `IPacketTransform` end to end through the public API |
| `ExampleXorPacketTransformTests` | 5 | The XOR example, with the masking observed on the wire |
| `NatTraversalTests` | 6 | Probe, challenge, echo and handshake; a forged token drawing an echo rather than a handshake; an already-echoed challenge never echoed again; probe rate limiting; a simultaneous punch |
| `ExploitDefenseTests` | 10 | Oversized and garbage packets, rate floods, blacklisting, malformed headers, handler overrides |
| `SignatureValidatorTests` | 3 | Custom validators and signature providers |
| `BeaconSecurityTests` | 5 | The beacon's join challenge, forged cookies, host-only heartbeat acknowledgement, per-address rate limiting |
| `SweepFindingTests` | 42 | The robustness-sweep findings, each asserting the corrected behaviour; see `docs/ROBUSTNESS_SWEEP.md` |
| `TelemetryAndLatencyTests` | 11 | Telemetry counts, and the latency simulator's delay, jitter, loss and reordering |
| `ConnectionStressTests` | 4 | Many clients sending rapidly, and repeated connect, send and disconnect cycles |
| `PayloadIntegrityStressTests` | 5 | Received payloads never sharing a buffer with an unrelated rental |
| `GarbageCollectionTests` | 1 | Steady-state sending not triggering a collection |

A few assertions are timing-sensitive and can fail occasionally when the whole suite runs on a heavily loaded machine; re-running the single test settles whether a failure is real.

---

## Status

Under active development. The engine is considered stable and is still being hardened. `docs/ROBUSTNESS_SWEEP.md` records every finding from the last security and robustness sweep, and its status table says which are fixed and which remain open.
