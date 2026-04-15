# SynapseSocket

A high-performance, hardened UDP transport engine for .NET — built for real-time applications that need both reliability and speed without the overhead of TCP.

SynapseSocket gives you two delivery channels over a single UDP socket, a layered security pipeline that neutralises common denial-of-service vectors before any payload is copied, and optional NAT traversal for peer-to-peer connections — all through a clean event-driven API.

---

## Why UDP?

TCP's congestion control, head-of-line blocking, and OS-level retransmission make it a poor fit for latency-sensitive workloads like games, real-time simulations, or peer-to-peer tools. SynapseSocket lets you choose per-send whether delivery matters more or speed does, and enforces that contract all the way down to the wire.

---

## Features

### Dual delivery channels
Every `SendAsync` call is either **reliable** (in-order, acknowledged, retransmitted until ACKed or the retry cap is hit) or **unreliable** (fire-and-forget, lowest possible latency). The channel is chosen at call time — the same connection supports both simultaneously.

### Automatic segmentation
Payloads larger than the MTU are transparently split into segments on the send side and reassembled on the receive side. Segmentation can be applied to reliable sends, unreliable sends, or both — configurable per engine. Segment count, MTU, and maximum reassembled size are all tuneable.

### NAT traversal
Two hole-punching modes are supported out of the box:

- **Full-cone** — direct peer-to-peer punching when both endpoints are known in advance.
- **Server-assisted (rendezvous)** — a lightweight rendezvous server exchanges external endpoints and coordinates the punch, requiring no relay after the initial handshake.

### Security pipeline
Every inbound datagram passes through a multi-stage filter **before** any payload is copied:

| Stage | What it does |
|---|---|
| Signature | Computes a 64-bit peer identity; drops packets with no computable identity |
| Blacklist | Instantly drops any datagram from a banned signature |
| Size check | Rejects datagrams exceeding `MaximumPacketSize` as `Oversized` |
| Rate limit | Token-bucket per signature; excess traffic raises `RateLimitExceeded` |
| Replay cache | Tracks seen handshake signatures; replayed handshakes are rejected |
| Assembly cap | Limits concurrent incomplete segment assemblies per connection |
| Reorder cap | Limits out-of-order reliable packets buffered per connection |

The `ViolationDetected` event exposes every security decision to the application. Handlers can inspect the violation reason and override the engine's default action (`Drop`, `Kick`, or `KickAndBlacklist`) — enabling custom policies like graduated responses or allow-listing.

### Pluggable signature provider
The `ISignatureProvider` interface controls how peer identities are computed. The built-in `DefaultSignatureProvider` derives signatures from the remote endpoint. Custom providers can incorporate pre-shared keys, tokens, or any other credential, enabling application-level authentication at the transport layer.

### Optional signature validator
`ISignatureValidator` allows handshake payloads to be inspected before a connection is accepted — useful for token-based admission without a separate authentication round-trip.

### Object pool architecture
SynapseSocket is allocation-minimal on the hot path. Packet buffers, event-args objects, packet splitters, reassemblers, and segment assemblies are all rented from `ResettableObjectPool<T>` or `ArrayPool<byte>` and returned after use. No per-packet heap pressure in steady state.

### Telemetry
An optional `Telemetry` object tracks bytes in/out, packets in/out, dropped packets, reliable resends, and lost packets. Counters are per-engine and zero-cost when telemetry is disabled.

### Latency simulation
A built-in `LatencySimulator` can inject configurable artificial delay and packet loss into the send path — useful for testing application behaviour under poor network conditions without needing a real bad network.

### Graceful lifecycle
Engines support `StartAsync` / `StopAsync` / `DisposeAsync`. A stopped engine can be restarted. Keep-alive packets maintain idle connections; unresponsive peers are timed out and a `ConnectionClosed` event fires. Disconnect packets notify peers of intentional disconnection.

---

## Getting Started

```csharp
// Server
SynapseConfig serverConfig = new()
{
    BindEndPoints = [new IPEndPoint(IPAddress.Any, 45000)],
    MaximumSegments = 128,
};

await using SynapseManager server = new(serverConfig);

server.ConnectionEstablished += e => Console.WriteLine($"peer connected: {e.Connection.RemoteEndPoint}");
server.PacketReceived += async e =>
{
    // Echo back on the same channel.
    await server.SendAsync(e.Connection, e.Payload.ToArray(), e.IsReliable);
};

await server.StartAsync();
```

```csharp
// Client
SynapseConfig clientConfig = new()
{
    BindEndPoints = [new IPEndPoint(IPAddress.Any, 0)],
    MaximumSegments = 128,
};

await using SynapseManager client = new(clientConfig);
await client.StartAsync();

SynapseConnection connection = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 45000));

// Reliable send — guaranteed delivery, in-order.
await client.SendAsync(connection, Encoding.UTF8.GetBytes("hello"), isReliable: true);

// Unreliable send — lowest latency, no retransmit.
await client.SendAsync(connection, Encoding.UTF8.GetBytes("ping"), isReliable: false);

await client.DisconnectAsync(connection);
```

---

## Configuration Reference

`SynapseConfig` uses public fields with sensible defaults. Override only what you need.

| Field | Default | Description |
|---|---|---|
| `BindEndPoints` | *(required)* | Local endpoints to bind — supports dual-stack (IPv4 + IPv6 simultaneously) |
| `MaximumPacketSize` | 1400 | Maximum inbound datagram size; larger packets raise an `Oversized` violation |
| `MaximumTransmissionUnit` | 1200 | Per-segment wire size used for segmentation |
| `MaximumPacketsPerSecond` | 2000 | Rate limit per peer signature; 0 disables |
| `MaximumSegments` | disabled | Maximum segments a payload may be split into; 0 disables segmentation |
| `MaximumConcurrentSegmentAssembliesPerConnection` | 16 | Cap on in-flight segment assemblies per connection |
| `MaximumReassembledPacketSize` | 0 | Rejects declared assemblies exceeding this size; 0 disables the check |
| `MaximumOutOfOrderReliablePackets` | 64 | Reorder buffer cap per connection; overflow raises a violation |
| `MaximumConcurrentConnections` | 0 | Maximum simultaneous peers; 0 disables the cap |
| `UnreliableSegmentMode` | `SegmentUnreliable` | How oversized unreliable payloads are handled |
| `IsUnreliablePayloadCopied` | `true` | When false, the ingress buffer is handed directly to the callback (zero-copy) |
| `SegmentAssemblyTimeoutMilliseconds` | 5000 | Incomplete assemblies older than this are evicted; 0 disables |
| `IsTelemetryEnabled` | `false` | Enables byte/packet counters |
| `Reliable.MaximumPending` | 256 | Unacknowledged reliable packets per connection before backpressure |
| `Reliable.ResendMilliseconds` | 250 | Retransmit interval |
| `Reliable.MaximumRetries` | 10 | Max retransmit attempts before the connection is terminated |
| `Connection.KeepAliveIntervalMilliseconds` | — | Keep-alive heartbeat interval |
| `Connection.TimeoutMilliseconds` | — | Idle timeout before a connection is declared lost |
| `NatTraversal.Mode` | `Disabled` | `FullCone` or `Server` to enable hole-punching |

---

## Security Model

### Violation pipeline

```
Inbound datagram
    │
    ├─ Size check           → Oversized violation
    ├─ Signature compute    → drops unknown identities
    ├─ Blacklist check      → ConnectionFailed(Blacklisted)
    ├─ Rate limit           → RateLimitExceeded violation
    │
    └─ ProcessPacket
           ├─ Header parse failure    → Malformed violation
           ├─ Handshake replay        → ConnectionFailed(SignatureRejected)
           ├─ Connection cap exceeded → ConnectionFailed(ServerFull)
           ├─ Assembly cap exceeded   → protocol violation (KickAndBlacklist)
           ├─ Reorder buffer cap      → Oversized violation (KickAndBlacklist)
           └─ Declared segment size   → Oversized violation (KickAndBlacklist)
```

### Customising violation responses

```csharp
server.ViolationDetected += e =>
{
    // Graduated response: drop first offence, blacklist second.
    if (e.Reason == ViolationReason.Oversized)
    {
        if (!alreadyWarned.Contains(e.Signature))
        {
            alreadyWarned.Add(e.Signature);
            e.Action = ViolationAction.Drop;  // override default KickAndBlacklist
        }
        // Second offence: leave Action as KickAndBlacklist.
    }
};
```

---

## NAT Traversal

### Full-cone (peer-to-peer)

Both sides must know each other's external endpoint in advance (e.g., from a lobby service). SynapseSocket sends probe packets to open NAT mappings, then completes a normal handshake once the path is open.

```csharp
SynapseConfig config = new()
{
    BindEndPoints = [new IPEndPoint(IPAddress.Any, 0)],
    NatTraversal = { Mode = NatTraversalMode.FullCone }
};
```

### Server-assisted (rendezvous)

Server-assisted rendezvous — where a small signalling service matches peers and exchanges their
external endpoints before hole-punching — lives in the companion **[SynapseBeacon](SynapseBeacon/)**
project. SynapseBeacon piggybacks on the Synapse UDP socket via the `SynapseManager.SendRawAsync` +
`UnknownPacketReceived` extension hooks, so the NAT mapping opened to the beacon server is the same
mapping used for peer-to-peer traffic after the punch. See `SynapseBeacon.Demo` for an end-to-end
example.

---

## Test Suite

SynapseSocket ships a full xUnit integration test suite that spins up real engines over loopback sockets — no mocking.

| Suite | Coverage |
|---|---|
| `HandshakeAndChannelTests` | Handshake fires events on both sides; unreliable and reliable delivery; in-order guarantee; echo without deadlock; graceful disconnect |
| `SegmentationTests` | Large payload segmented and fully reassembled; oversized reliable send throws clearly; declared assembly exceeding `MaximumReassembledPacketSize` rejected |
| `ExploitDefenseTests` | Oversized packet dropped + violation fired; rate-limit flood detected; blacklisted signature rejected; garbage bytes don't crash the engine; truncated header flagged as malformed; violation handler can override kick to drop; two-strike handler pattern; `KickAndBlacklist` blocks reconnect |
| `KeepAliveAndTimeoutTests` | Keep-alive fires; silent peer triggers timeout and `ConnectionClosed` |
| `SignatureValidatorTests` | Custom validator gates connection admission |
| `TelemetryAndLatencyTests` | Counters increment correctly; latency simulator delays delivery measurably |
| `EngineLifecycleTests` | Start/stop/restart; double-start throws; dispose cleans up resources |

---

## Architecture

SynapseSocket is structured as a set of partial classes and focused subsystems:

```
SynapseManager          — public API; owns sockets, lifecycle, and event dispatch
├── IngressEngine       — per-socket receive loop; security pipeline; packet dispatch
│   └── IngressEngine.Nat.cs   — NAT probe + challenge-response handling (FullCone)
├── TransmissionEngine  — send path; reliable queue; keep-alive; disconnect packets
│   └── TransmissionEngine.Nat.cs  — NAT probe + challenge sends (FullCone)
├── SynapseManager.Maintenance.cs  — background loop: keep-alive sweep, reliable
│                                     retransmit, segment eviction, rate-bucket sweep
└── SynapseManager.NatTraversal.cs — outbound hole-punch orchestration

ConnectionManager       — thread-safe connection registry (by endpoint + signature)
SynapseConnection       — per-peer state: sequence numbers, reorder buffer, pending reliable queue
SecurityProvider        — signature, blacklist, rate limiting
PacketReassembler       — receive-side segmentation; per-connection assembly dictionary
PacketSplitter          — send-side segmentation; MTU-aware segment generation
LatencySimulator        — optional artificial delay/loss on the send path
Telemetry               — lock-free counters
```

---

## Target Framework

`netstandard2.1` — compatible with .NET Core 3.0+, .NET 5+, and Unity 2021.2+.

No external runtime dependencies. CodeBoost is a development-time submodule (analyzers and utilities); it is not required at runtime.

---

## Status

SynapseSocket is under active development and not yet recommended for production use.
