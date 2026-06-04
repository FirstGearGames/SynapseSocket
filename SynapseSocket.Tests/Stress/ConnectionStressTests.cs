using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using Xunit.Abstractions;

namespace SynapseSocket.Tests.Stress;

[SettleAfterTest(3000)]
public sealed class ConnectionStressTests
{
    // The engine is poll-driven and these tests pump 2001 engines on a single thread, so they are throughput-bound
    // rather than latency-bound; the timeouts are generous to tolerate that (the rework's goal is correctness — no
    // drops, no corruption — not raw single-thread speed).
    private const int StressTestTimeoutMs = 120000;
    private const int LifecycleTestTimeoutMs = 240000;

    private readonly ITestOutputHelper _output;

    public ConnectionStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void StressTest_Clients_RapidSends_Reliable() => RunRapidSendStress(reliable: true);

    [Fact]
    public void StressTest_Clients_RapidSends_Unreliable() => RunRapidSendStress(reliable: false);

    [Fact]
    public void StressTest_Clients_ConnectSendDisconnect_Cycles_Reliable() => RunConnectSendDisconnectCycles(reliable: true);

    [Fact]
    public void StressTest_Clients_ConnectSendDisconnect_Cycles_Unreliable() => RunConnectSendDisconnectCycles(reliable: false);

    private void RunRapidSendStress(bool reliable)
    {
        const int ClientCount = 2000;
        const int RapidSendsPerClient = 50;
        const int PayloadSize = 50;

        byte[] sendBuffer = new byte[PayloadSize];
        Array.Fill(sendBuffer, (byte)0xAB);

        int expectedPackets = ClientCount * RapidSendsPerClient;
        int port = TestHarness.GetFreePort();

        SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Security.MaximumBytesPerSecond = 0;
            c.Security.MaximumPacketsPerSecond = 0;
            c.Security.MaximumOutOfOrderReliablePackets = 0;
        }));
        SynapseManager[] clients = new SynapseManager[ClientCount];
        SynapseConnection[] clientToServerConnections = new SynapseConnection[ClientCount];

        for (int i = 0; i < ClientCount; i++)
        {
            clients[i] = new(TestHarness.ClientConfig(c =>
            {
                if (reliable)
                    c.Reliable.MaximumPending = RapidSendsPerClient * 2;
            }));
        }

        SynapseManager[] allEngines = BuildEngineArray(server, clients);

        using TestHarness.FailureObserver failureObserver = TestHarness.ObserveFailures(allEngines);

        try
        {
            server.Start();

            int connectedCount = 0;
            server.ConnectionEstablished += _ => Interlocked.Increment(ref connectedCount);

            for (int i = 0; i < ClientCount; i++)
            {
                clients[i].Start();
                clientToServerConnections[i] = clients[i].Connect(new(IPAddress.Loopback, port));
            }

            Assert.True(TestHarness.PumpUntil(() => Volatile.Read(ref connectedCount) >= ClientCount, 60000, allEngines),
                $"Only {connectedCount} / {ClientCount} clients connected within timeout.");

            int receivedCount = 0;
            server.PacketReceived += _ => Interlocked.Increment(ref receivedCount);

            ArraySegment<byte> payload = new(sendBuffer);
            Stopwatch stopwatch = Stopwatch.StartNew();

            // Interleave sends with a poll each round so the server's kernel receive buffer never overflows
            // (single-threaded: the server can only drain when this thread polls it).
            for (int s = 0; s < RapidSendsPerClient; s++)
            {
                for (int i = 0; i < ClientCount; i++)
                {
                    clients[i].Send(clientToServerConnections[i], payload, reliable);

                    // Drain the server incrementally so a large burst can't overflow its kernel receive buffer
                    // before this single thread gets a chance to poll it (unreliable loss would be unrecoverable).
                    if ((i & 255) == 255)
                        server.Poll();
                }

                for (int e = 0; e < allEngines.Length; e++)
                    allEngines[e].Poll();
            }

            TestHarness.PumpUntil(() => Volatile.Read(ref receivedCount) >= expectedPackets || failureObserver.HasFailures, 90000, allEngines);

            stopwatch.Stop();

            double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            double messagesPerSec = expectedPackets / stopwatch.Elapsed.TotalSeconds;

            _output.WriteLine($"Channel           : {(reliable ? "Reliable" : "Unreliable")}");
            _output.WriteLine($"Clients           : {ClientCount:N0}");
            _output.WriteLine($"Sends per client  : {RapidSendsPerClient}");
            _output.WriteLine($"Payload size      : {PayloadSize} bytes");
            _output.WriteLine($"Packets received  : {receivedCount:N0} / {expectedPackets:N0}");
            _output.WriteLine($"Elapsed           : {elapsedMs:F2} ms");
            _output.WriteLine($"Message rate      : {messagesPerSec:F0} msg/s");

            failureObserver.AssertNoFailures();
            Assert.Equal(expectedPackets, receivedCount);
        }
        finally
        {
            server.Dispose();

            for (int i = 0; i < ClientCount; i++)
                clients[i].Dispose();
        }
    }

    private void RunConnectSendDisconnectCycles(bool reliable)
    {
        const int ClientCount = 2000;
        const int ReconnectSendsPerClient = 4;
        const int PayloadSize = 1000;

        const int CycleCount = 4;
        const int ConnectWaitMs = 60000;
        const int ReceiveWaitMs = 90000;
        const int AckWaitMs = 60000;
        const int DisconnectWaitMs = 60000;
        const int DisconnectDelayMilliseconds = 500;

        byte[] sendBuffer = new byte[PayloadSize];
        Array.Fill(sendBuffer, (byte)0xAB);

        int port = TestHarness.GetFreePort();

        SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Security.MaximumPacketsPerSecond = 0;
            c.Security.MaximumBytesPerSecond = 0;
            c.Security.MaximumOutOfOrderReliablePackets = 0;
        }));
        SynapseManager[] clients = new SynapseManager[ClientCount];
        SynapseConnection[] connections = new SynapseConnection[ClientCount];

        for (int i = 0; i < ClientCount; i++)
            clients[i] = new(TestHarness.ClientConfig(c =>
            {
                c.Reliable.MaximumRetries = 500;
            }));

        SynapseManager[] allEngines = BuildEngineArray(server, clients);

        try
        {
            server.Start();

            for (int i = 0; i < ClientCount; i++)
                clients[i].Start();

            int totalConnectedCount = 0;
            int totalReceivedCount = 0;
            int totalClientClosedCount = 0;

            server.ConnectionEstablished += _ => Interlocked.Increment(ref totalConnectedCount);
            server.PacketReceived += _ => Interlocked.Increment(ref totalReceivedCount);

            // Client-side close is guaranteed: Disconnect fires ConnectionClosed synchronously
            // after removing the connection, regardless of whether the server received the packet.
            for (int i = 0; i < ClientCount; i++)
                clients[i].ConnectionClosed += _ => Interlocked.Increment(ref totalClientClosedCount);

            void RunCycle()
            {
                int connectTarget = totalConnectedCount + ClientCount;
                int receiveTarget = totalReceivedCount + ClientCount * ReconnectSendsPerClient;
                int closeTarget = totalClientClosedCount + ClientCount;

                for (int i = 0; i < ClientCount; i++)
                    connections[i] = clients[i].Connect(new(IPAddress.Loopback, port));

                Assert.True(TestHarness.PumpUntil(() => Volatile.Read(ref totalConnectedCount) >= connectTarget, ConnectWaitMs, allEngines),
                    $"Only {totalConnectedCount} / {connectTarget} clients connected.");

                ArraySegment<byte> payload = new(sendBuffer);

                for (int s = 0; s < ReconnectSendsPerClient; s++)
                {
                    for (int i = 0; i < ClientCount; i++)
                    {
                        clients[i].Send(connections[i], payload, reliable);

                        // Drain the server incrementally so a large burst (2000 × 1 KB) can't overflow its kernel
                        // receive buffer before this single thread polls it — unreliable loss would be unrecoverable.
                        if ((i & 255) == 255)
                            server.Poll();
                    }

                    for (int e = 0; e < allEngines.Length; e++)
                        allEngines[e].Poll();
                }

                Assert.True(TestHarness.PumpUntil(() => Volatile.Read(ref totalReceivedCount) >= receiveTarget, ReceiveWaitMs, allEngines),
                    $"Only {totalReceivedCount} / {receiveTarget} packets received.");

                if (reliable)
                {
                    Assert.True(TestHarness.PumpUntil(() =>
                    {
                        for (int i = 0; i < ClientCount; i++)
                        {
                            if (connections[i].PendingReliableQueue.Count != 0)
                                return false;
                        }

                        return true;
                    }, AckWaitMs, allEngines), "Not all reliable sends were acknowledged within the ACK wait window.");
                }
                else
                {
                    TestHarness.PumpFor(DisconnectDelayMilliseconds, allEngines);
                }

                for (int i = 0; i < ClientCount; i++)
                    clients[i].Disconnect(connections[i]);

                Assert.True(TestHarness.PumpUntil(() => Volatile.Read(ref totalClientClosedCount) >= closeTarget, DisconnectWaitMs, allEngines),
                    $"Only {totalClientClosedCount} / {closeTarget} connections closed.");
            }

            // Warmup — primes object pools before GC baseline.
            RunCycle();

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            int gen2Before = GC.CollectionCount(2);

            double[] cycleTimes = new double[CycleCount];
            Stopwatch totalStopwatch = Stopwatch.StartNew();

            for (int cycle = 0; cycle < CycleCount; cycle++)
            {
                Stopwatch cycleStopwatch = Stopwatch.StartNew();
                RunCycle();
                cycleStopwatch.Stop();
                cycleTimes[cycle] = cycleStopwatch.Elapsed.TotalMilliseconds;
            }

            totalStopwatch.Stop();

            int gen2After = GC.CollectionCount(2);

            _output.WriteLine($"Channel           : {(reliable ? "Reliable" : "Unreliable")}");
            _output.WriteLine($"Clients           : {ClientCount:N0}");
            _output.WriteLine($"Cycles            : {CycleCount}");

            for (int i = 0; i < CycleCount; i++)
                _output.WriteLine($"Cycle {i + 1}           : {cycleTimes[i]:F2} ms");

            _output.WriteLine($"Total elapsed     : {totalStopwatch.Elapsed.TotalMilliseconds:F2} ms");
            _output.WriteLine($"GC Gen2           : {gen2After - gen2Before}");

            Assert.Equal(0, gen2After - gen2Before);
        }
        finally
        {
            server.Dispose();

            for (int i = 0; i < ClientCount; i++)
                clients[i].Dispose();
        }
    }

    private static SynapseManager[] BuildEngineArray(SynapseManager server, SynapseManager[] clients)
    {
        SynapseManager[] allEngines = new SynapseManager[clients.Length + 1];
        allEngines[0] = server;
        Array.Copy(clients, 0, allEngines, 1, clients.Length);
        return allEngines;
    }
}
