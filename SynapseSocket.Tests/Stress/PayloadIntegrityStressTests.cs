using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace SynapseSocket.Tests.Stress;

/// <summary>
/// Attempts to reproduce, purely at the SynapseSocket level (no Nucleus, no transport wrapper), the
/// received-payload corruption seen in Nucleus's full test suite: a buffer handed to two owners so a
/// receiver reads bytes that were overwritten by an unrelated rental. Each message carries a verifiable
/// pattern; the receiver checks content exactly. Canary buffers rented from the shared pool are held
/// across engine activity and re-verified to catch cross-component double-returns into <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
public class PayloadIntegrityStressTests
{
    private readonly ITestOutputHelper _output;

    public PayloadIntegrityStressTests(ITestOutputHelper output) => _output = output;

    /// <summary>Payload sizes per message; mixes unsegmented (&lt;=MTU) and multi-segment payloads.</summary>
    private static readonly int[] PayloadSizes = [16, 200, 1_100, 1_500, 3_000, 5_000, 800, 2_400];

    /// <summary>
    /// Fills a buffer with a pattern derived from <paramref name="seed"/>: bytes 0..3 hold the seed,
    /// every later byte is (seed + index) truncated to a byte. Lets the receiver detect any aliasing
    /// overwrite, a wrong length, or a swapped payload.
    /// </summary>
    private static byte[] MakePatternedPayload(uint seed, int length)
    {
        byte[] payload = new byte[length];
        payload[0] = (byte)(seed & 0xFF);
        payload[1] = (byte)((seed >> 8) & 0xFF);
        payload[2] = (byte)((seed >> 16) & 0xFF);
        payload[3] = (byte)((seed >> 24) & 0xFF);
        for (int i = 4; i < length; i++)
            payload[i] = (byte)((seed + (uint)i) & 0xFF);
        return payload;
    }

    /// <summary>Returns a description of the first pattern violation in <paramref name="payload"/>, or null when it is intact.</summary>
    private static string? ValidatePattern(byte[] payload) => ValidatePattern(payload, payload.Length);

    /// <summary>Validates the first <paramref name="length"/> bytes of <paramref name="payload"/> against the seed-derived pattern.</summary>
    private static string? ValidatePattern(byte[] payload, int length)
    {
        if (length < 4)
            return $"length {length} below 4-byte header";

        uint seed = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
        for (int i = 4; i < length; i++)
        {
            byte expected = (byte)((seed + (uint)i) & 0xFF);
            if (payload[i] != expected)
                return $"seed={seed} len={length} byte[{i}]={payload[i]} expected={expected}";
        }

        return null;
    }

    [Fact]
    public async Task ReliablePayloads_StayIntact_AcrossManyCyclesAndSizes()
    {
        const int Cycles = 120;
        const int MessagesPerCycle = 24;

        ConcurrentBag<string> corruptions = [];
        int totalSent = 0;
        int totalReceived = 0;
        int totalLost = 0;

        for (int cycle = 0; cycle < Cycles && corruptions.IsEmpty; cycle++)
        {
            int port = TestHarness.GetFreePort();

            void Tweak(SynapseConfig config)
            {
                config.CopyReceivedPayloads = true;
                // Disable established-connection rate/oversize enforcement so an aggressive stress burst is not
                // kicked+blacklisted by the rate limiter; we are exercising buffer integrity, not flood defense.
                config.Security.Enabled = false;
            }

            SynapseManager server = new(TestHarness.ServerConfig(port, Tweak));
            SynapseManager client = new(TestHarness.ClientConfig(Tweak));

            using TestHarness.FailureObserver observer = TestHarness.ObserveFailures(server, client);

            // Validate on the ingress thread, the instant the payload is delivered, then record the seed as received.
            ConcurrentDictionary<uint, byte> receivedSeeds = new();
            server.PacketReceived += args =>
            {
                byte[] copy = args.Payload.ToArray();
                string? violation = ValidatePattern(copy);
                if (violation is not null)
                    corruptions.Add($"cycle {cycle}: content {violation}");
                else
                    receivedSeeds[(uint)(copy[0] | (copy[1] << 8) | (copy[2] << 16) | (copy[3] << 24))] = 1;
            };

            await server.StartAsync(CancellationToken.None);
            await client.StartAsync(CancellationToken.None);

            SynapseConnection connection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
            await TestHarness.WaitFor(() => connection.State == ConnectionState.Connected);

            // Hold a batch of shared-pool canaries across this cycle's traffic. If the engine returns one of
            // its own buffers twice, a later rental can collide with a canary and overwrite its pattern.
            List<(byte[] buffer, int length, uint seed)> canaries = RentCanaries(cycle);

            List<uint> sentSeeds = [];

            for (int message = 0; message < MessagesPerCycle; message++)
            {
                uint seed = (uint)(cycle * 10_000 + message + 1);
                int length = PayloadSizes[message % PayloadSizes.Length];
                byte[] payload = MakePatternedPayload(seed, length);
                sentSeeds.Add(seed);

                observer.ObserveSend(client.SendAsync(connection, payload, isReliable: true, CancellationToken.None));
                Interlocked.Increment(ref totalSent);
            }

            bool all = await TestHarness.WaitFor(() => receivedSeeds.Count >= sentSeeds.Count, 6_000);

            VerifyCanaries(canaries, cycle, corruptions);
            ReturnCanaries(canaries);

            foreach (uint seed in sentSeeds)
            {
                if (receivedSeeds.ContainsKey(seed))
                    Interlocked.Increment(ref totalReceived);
                else
                    Interlocked.Increment(ref totalLost);
            }

            if (!all)
                corruptions.Add($"cycle {cycle}: only {receivedSeeds.Count}/{sentSeeds.Count} received (lost)");

            observer.AssertNoFailures();

            await client.DisconnectAsync(connection, CancellationToken.None);
            await server.DisposeAsync();
            await client.DisposeAsync();
        }

        _output.WriteLine($"sent={totalSent} received={totalReceived} lost={totalLost} corruptions={corruptions.Count}");
        Assert.True(corruptions.IsEmpty, FormatCorruptions(corruptions));
    }

    [Fact]
    public async Task DeferredReadConsumer_ReliablePayloads_StayIntact()
    {
        // Mirrors how Nucleus's transport consumes payloads: copy each delivered payload into a shared-pool
        // rental on the ingress thread, then read it LATER. Every copy is HELD for the whole cycle (alongside a
        // batch of canaries) and validated only at the end, so any buffer the engine hands to a second owner
        // mid-cycle is caught as a broken pattern. Each copy is also validated immediately on arrival to tell a
        // corrupt-on-the-wire datagram (ARRIVED-CORRUPT) apart from receive-side pool poisoning (HELD-ALIASED).
        // Bidirectional reliable traffic with mixed unsegmented/segmented sizes churns both receive paths.
        const int Cycles = 120;
        const int Bursts = 2;
        const int MessagesPerBurst = 16;

        ConcurrentBag<string> corruptions = [];

        void Tweak(SynapseConfig config) => config.CopyReceivedPayloads = true;

        for (int cycle = 0; cycle < Cycles && corruptions.IsEmpty; cycle++)
        {
            int port = TestHarness.GetFreePort();

            SynapseManager server = new(TestHarness.ServerConfig(port, Tweak));
            SynapseManager client = new(TestHarness.ClientConfig(Tweak));

            using TestHarness.FailureObserver observer = TestHarness.ObserveFailures(server, client);

            ConcurrentQueue<(byte[] copy, int length)> deferred = new();
            int receivedCount = 0;

            void DeferredConsumer(SynapseSocket.Core.Events.PacketReceivedEventArgs args)
            {
                ArraySegment<byte> source = args.Payload;
                byte[] copy = ArrayPool<byte>.Shared.Rent(source.Count);
                if (source.Array is not null)
                    Array.Copy(source.Array, source.Offset, copy, 0, source.Count);

                string? arrived = ValidatePattern(copy, source.Count);
                if (arrived is not null)
                    corruptions.Add($"cycle {cycle}: ARRIVED-CORRUPT {arrived}");

                deferred.Enqueue((copy, source.Count));
                Interlocked.Increment(ref receivedCount);
            }

            SynapseConnection? toClient = null;
            server.ConnectionEstablished += args => toClient = args.Connection;
            server.PacketReceived += DeferredConsumer;
            client.PacketReceived += DeferredConsumer;

            await server.StartAsync(CancellationToken.None);
            await client.StartAsync(CancellationToken.None);

            SynapseConnection toServer = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
            await TestHarness.WaitFor(() => toServer.State == ConnectionState.Connected && toClient is not null);

            List<(byte[] buffer, int length, uint seed)> canaries = RentCanaries(cycle);

            // Concurrent drain: a third thread reads each deferred copy and returns it to the pool while sends and
            // receives are still in flight, widening the window in which an aliased buffer is overwritten.
            using CancellationTokenSource drainCancellation = new();
            Task drainTask = Task.Run(() =>
            {
                while (!drainCancellation.IsCancellationRequested)
                {
                    while (deferred.TryDequeue(out (byte[] copy, int length) item))
                    {
                        string? violation = ValidatePattern(item.copy, item.length);
                        if (violation is not null)
                            corruptions.Add($"cycle {cycle}: DEFERRED-ALIASED {violation}");
                        ArrayPool<byte>.Shared.Return(item.copy);
                    }
                    Thread.SpinWait(50);
                }
            });

            int expected = 0;
            for (int burst = 0; burst < Bursts; burst++)
            {
                for (int message = 0; message < MessagesPerBurst; message++)
                {
                    uint clientSeed = (uint)(cycle * 100_000 + burst * 1_000 + message + 1);
                    uint serverSeed = clientSeed + 500;
                    int length = PayloadSizes[message % PayloadSizes.Length];

                    observer.ObserveSend(client.SendAsync(toServer, MakePatternedPayload(clientSeed, length), isReliable: true, CancellationToken.None));
                    observer.ObserveSend(server.SendAsync(toClient!, MakePatternedPayload(serverSeed, length), isReliable: true, CancellationToken.None));
                    expected += 2;
                }
            }

            bool all = await TestHarness.WaitFor(() => Volatile.Read(ref receivedCount) >= expected, 8_000);
            await TestHarness.WaitFor(() => deferred.IsEmpty, 2_000);
            drainCancellation.Cancel();
            await drainTask;

            VerifyCanaries(canaries, cycle, corruptions);
            ReturnCanaries(canaries);

            if (!all)
                corruptions.Add($"cycle {cycle}: only {receivedCount}/{expected} received (lost)");

            observer.AssertNoFailures();

            await client.DisconnectAsync(toServer, CancellationToken.None);
            await server.DisposeAsync();
            await client.DisposeAsync();
        }

        Assert.True(corruptions.IsEmpty, FormatCorruptions(corruptions));
    }

    [Fact]
    public async Task TeardownWithPendingReliables_DoesNotPoisonSharedPool()
    {
        // Disconnect immediately after a reliable burst so the per-connection teardown drain races the
        // still-running ingress ACK thread and the maintenance retransmit sweep over the same pending entries.
        const int Cycles = 400;
        const int MessagesPerCycle = 12;

        ConcurrentBag<string> corruptions = [];

        for (int cycle = 0; cycle < Cycles && corruptions.IsEmpty; cycle++)
        {
            int port = TestHarness.GetFreePort();

            void Tweak(SynapseConfig config)
            {
                config.CopyReceivedPayloads = true;
                // Disable established-connection rate/oversize enforcement so an aggressive stress burst is not
                // kicked+blacklisted by the rate limiter; we are exercising buffer integrity, not flood defense.
                config.Security.Enabled = false;
            }

            SynapseManager server = new(TestHarness.ServerConfig(port, Tweak));
            SynapseManager client = new(TestHarness.ClientConfig(Tweak));

            using TestHarness.FailureObserver observer = TestHarness.ObserveFailures(server, client);

            server.PacketReceived += args =>
            {
                byte[] copy = args.Payload.ToArray();
                string? violation = ValidatePattern(copy);
                if (violation is not null)
                    corruptions.Add($"cycle {cycle}: content {violation}");
            };

            await server.StartAsync(CancellationToken.None);
            await client.StartAsync(CancellationToken.None);

            SynapseConnection connection = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
            await TestHarness.WaitFor(() => connection.State == ConnectionState.Connected, 2_000);

            List<(byte[] buffer, int length, uint seed)> canaries = RentCanaries(cycle);

            for (int message = 0; message < MessagesPerCycle; message++)
            {
                uint seed = (uint)(cycle * 10_000 + message + 1);
                int length = PayloadSizes[message % PayloadSizes.Length];
                byte[] payload = MakePatternedPayload(seed, length);
                observer.ObserveSend(client.SendAsync(connection, payload, isReliable: true, CancellationToken.None));
            }

            // No wait: tear down with reliables still pending and ACKs still in flight.
            await client.DisconnectAsync(connection, CancellationToken.None);
            await server.DisposeAsync();
            await client.DisposeAsync();

            VerifyCanaries(canaries, cycle, corruptions);
            ReturnCanaries(canaries);

            observer.AssertNoFailures();
        }

        Assert.True(corruptions.IsEmpty, FormatCorruptions(corruptions));
    }

    /// <summary>Rents and patterns a batch of shared-pool buffers in size classes that overlap the engine's wire/payload buffers.</summary>
    private static List<(byte[] buffer, int length, uint seed)> RentCanaries(int cycle)
    {
        int[] sizes = [64, 256, 512, 1_200, 1_500, 2_048, 4_096];
        List<(byte[], int, uint)> canaries = new(sizes.Length * 4);

        for (int repeat = 0; repeat < 4; repeat++)
        {
            for (int s = 0; s < sizes.Length; s++)
            {
                int length = sizes[s];
                uint seed = (uint)(0xCA_00_00_00 + cycle * 100 + repeat * 10 + s);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                byte[] pattern = MakePatternedPayload(seed, length);
                Array.Copy(pattern, buffer, length);
                canaries.Add((buffer, length, seed));
            }
        }

        return canaries;
    }

    /// <summary>Re-verifies each canary's pattern; a mismatch means another owner wrote into a buffer we still hold.</summary>
    private static void VerifyCanaries(List<(byte[] buffer, int length, uint seed)> canaries, int cycle, ConcurrentBag<string> corruptions)
    {
        foreach ((byte[] buffer, int length, uint seed) in canaries)
        {
            for (int i = 0; i < length; i++)
            {
                byte expected = i < 4 ? (byte)((seed >> (i * 8)) & 0xFF) : (byte)((seed + (uint)i) & 0xFF);
                if (buffer[i] != expected)
                {
                    corruptions.Add($"cycle {cycle}: CANARY seed={seed} len={length} byte[{i}]={buffer[i]} expected={expected}");
                    break;
                }
            }
        }
    }

    private static void ReturnCanaries(List<(byte[] buffer, int length, uint seed)> canaries)
    {
        foreach ((byte[] buffer, int _, uint _) in canaries)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    private static string FormatCorruptions(ConcurrentBag<string> corruptions)
    {
        string[] snapshot = [.. corruptions];
        int show = Math.Min(snapshot.Length, 15);
        return $"{snapshot.Length} corruption(s):" + Environment.NewLine + string.Join(Environment.NewLine, snapshot[..show]);
    }
}
