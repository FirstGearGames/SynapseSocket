using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using Xunit.Abstractions;

namespace SynapseSocket.Tests.Stress;

[SettleAfterTest(3000)]
public sealed class ConnectionStressTests
{
    private const int StressTestTimeoutMs = 6000;
    private const int LifecycleTestTimeoutMs = 120000;

    private readonly ITestOutputHelper _output;

    public ConnectionStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = StressTestTimeoutMs)]
    public Task StressTest_Clients_RapidSends_Reliable() => RunRapidSendStressAsync(reliable: true);

    [Fact(Timeout = StressTestTimeoutMs)]
    public Task StressTest_Clients_RapidSends_Unreliable() => RunRapidSendStressAsync(reliable: false);

    [Fact(Timeout = LifecycleTestTimeoutMs)]
    public Task StressTest_Clients_ConnectSendDisconnect_Cycles_Reliable() => RunConnectSendDisconnectCyclesAsync(reliable: true);

    [Fact(Timeout = LifecycleTestTimeoutMs)]
    public Task StressTest_Clients_ConnectSendDisconnect_Cycles_Unreliable() => RunConnectSendDisconnectCyclesAsync(reliable: false);

    private async Task RunRapidSendStressAsync(bool reliable)
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

        using TestHarness.FailureObserver failureObserver = TestHarness.ObserveFailures([server, .. clients]);

        try
        {
            await server.StartAsync(CancellationToken.None);

            int connectedCount = 0;
            server.ConnectionEstablished += _ => Interlocked.Increment(ref connectedCount);

            Task[] connectTasks = new Task[ClientCount];

            for (int i = 0; i < ClientCount; i++)
            {
                int idx = i;
                connectTasks[idx] = Task.Run(async () =>
                {
                    await clients[idx].StartAsync(CancellationToken.None);
                    clientToServerConnections[idx] = await clients[idx].ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
                });
            }

            await Task.WhenAll(connectTasks);

            Assert.True(await TestHarness.WaitFor(() => Volatile.Read(ref connectedCount) >= ClientCount, 30000),
                $"Only {connectedCount} / {ClientCount} clients connected within timeout.");

            int receivedCount = 0;
            server.PacketReceived += _ => Interlocked.Increment(ref receivedCount);

            ArraySegment<byte> payload = new(sendBuffer);
            Stopwatch stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < ClientCount; i++)
            {
                for (int s = 0; s < RapidSendsPerClient; s++)
                    failureObserver.ObserveSend(clients[i].SendAsync(clientToServerConnections[i], payload, reliable, CancellationToken.None));
            }

            while (Volatile.Read(ref receivedCount) < expectedPackets && !failureObserver.HasFailures)
                await Task.Delay(1).ConfigureAwait(false);

            stopwatch.Stop();

            double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            double elapsedSec = stopwatch.Elapsed.TotalSeconds;
            double messagesPerSec = expectedPackets / elapsedSec;

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
            await server.DisposeAsync();

            Task[] disposeTasks = new Task[ClientCount];

            for (int i = 0; i < ClientCount; i++)
                disposeTasks[i] = clients[i].DisposeAsync().AsTask();

            await Task.WhenAll(disposeTasks);
        }
    }

    private async Task RunConnectSendDisconnectCyclesAsync(bool reliable)
    {
        const int ClientCount = 2000;
        const int ReconnectSendsPerClient = 4;
        const int PayloadSize = 1000;

        const int CycleCount = 4;
        const int ConnectWaitMs = 20000;
        const int ReliableReceiveWaitMs = 60000;
        const int UnreliableReceiveWaitMs = 15000;
        int receiveWaitMs = reliable ? ReliableReceiveWaitMs : UnreliableReceiveWaitMs;
        const int AckWaitMs = 10000;
        const int DisconnectWaitMs = 20000;
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

        try
        {
            await server.StartAsync(CancellationToken.None);

            // Start all clients once; they reconnect each cycle.
            Task[] startTasks = new Task[ClientCount];

            for (int i = 0; i < ClientCount; i++)
            {
                int idx = i;
                startTasks[idx] = clients[idx].StartAsync(CancellationToken.None);
            }

            await Task.WhenAll(startTasks);

            int totalConnectedCount = 0;
            int totalReceivedCount = 0;
            int totalClientClosedCount = 0;

            server.ConnectionEstablished += _ => Interlocked.Increment(ref totalConnectedCount);
            server.PacketReceived += _ => Interlocked.Increment(ref totalReceivedCount);

            // Client-side close is guaranteed: DisconnectAsync fires ConnectionClosed synchronously
            // after removing the connection, regardless of whether the server received the packet.
            for (int i = 0; i < ClientCount; i++)
                clients[i].ConnectionClosed += _ => Interlocked.Increment(ref totalClientClosedCount);

            async Task RunCycleAsync()
            {
                int connectTarget = totalConnectedCount + ClientCount;
                int receiveTarget = totalReceivedCount + ClientCount * ReconnectSendsPerClient;
                int closeTarget = totalClientClosedCount + ClientCount;

                Task[] connectTasks = new Task[ClientCount];

                for (int i = 0; i < ClientCount; i++)
                {
                    int idx = i;
                    connectTasks[idx] = Task.Run(async () =>
                        connections[idx] = await clients[idx].ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None));
                }

                await Task.WhenAll(connectTasks);

                Assert.True(await TestHarness.WaitFor(() => Volatile.Read(ref totalConnectedCount) >= connectTarget, ConnectWaitMs),
                    $"Only {totalConnectedCount} / {connectTarget} clients connected.");

                ArraySegment<byte> payload = new(sendBuffer);

                for (int i = 0; i < ClientCount; i++)
                {
                    for (int s = 0; s < ReconnectSendsPerClient; s++)
                        _ = clients[i].SendAsync(connections[i], payload, reliable, CancellationToken.None);
                }

                Assert.True(await TestHarness.WaitFor(() => Volatile.Read(ref totalReceivedCount) >= receiveTarget, receiveWaitMs),
                    $"Only {totalReceivedCount} / {receiveTarget} packets received.");

                if (reliable)
                {
                    Assert.True(await TestHarness.WaitFor(() =>
                    {
                        for (int i = 0; i < ClientCount; i++)
                        {
                            if (!connections[i].PendingReliableQueue.IsEmpty)
                                return false;
                        }

                        return true;
                    }, AckWaitMs), "Not all reliable sends were acknowledged within the ACK wait window.");
                }
                else
                {
                    await Task.Delay(DisconnectDelayMilliseconds);
                }

                Task[] disconnectTasks = new Task[ClientCount];

                for (int i = 0; i < ClientCount; i++)
                {
                    int idx = i;
                    disconnectTasks[idx] = clients[idx].DisconnectAsync(connections[idx], CancellationToken.None);
                }

                await Task.WhenAll(disconnectTasks);

                Assert.True(await TestHarness.WaitFor(() => Volatile.Read(ref totalClientClosedCount) >= closeTarget, DisconnectWaitMs),
                    $"Only {totalClientClosedCount} / {closeTarget} connections closed.");
            }

            // Warmup — primes object pools before GC baseline.
            await RunCycleAsync();

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            int gen3Before = GC.CollectionCount(3);
            int gen4Before = GC.CollectionCount(4);

            double[] cycleTimes = new double[CycleCount];
            Stopwatch totalStopwatch = Stopwatch.StartNew();

            for (int cycle = 0; cycle < CycleCount; cycle++)
            {
                Stopwatch cycleStopwatch = Stopwatch.StartNew();
                await RunCycleAsync();
                cycleStopwatch.Stop();
                cycleTimes[cycle] = cycleStopwatch.Elapsed.TotalMilliseconds;
            }

            totalStopwatch.Stop();

            int gen0After = GC.CollectionCount(0);
            int gen1After = GC.CollectionCount(1);
            int gen2After = GC.CollectionCount(2);
            int gen3After = GC.CollectionCount(3);
            int gen4After = GC.CollectionCount(4);

            _output.WriteLine($"Channel           : {(reliable ? "Reliable" : "Unreliable")}");
            _output.WriteLine($"Clients           : {ClientCount:N0}");
            _output.WriteLine($"Cycles            : {CycleCount}");
            _output.WriteLine($"Sends per client  : {ReconnectSendsPerClient}");
            _output.WriteLine($"Payload size      : {PayloadSize} bytes");

            for (int i = 0; i < CycleCount; i++)
                _output.WriteLine($"Cycle {i + 1}           : {cycleTimes[i]:F2} ms");

            _output.WriteLine($"Total elapsed     : {totalStopwatch.Elapsed.TotalMilliseconds:F2} ms");
            _output.WriteLine($"GC Gen0           : {gen0After - gen0Before}");
            _output.WriteLine($"GC Gen1           : {gen1After - gen1Before}");
            _output.WriteLine($"GC Gen2           : {gen2After - gen2Before}");
            _output.WriteLine($"GC Gen3           : {gen3After - gen3Before}");
            _output.WriteLine($"GC Gen4           : {gen4After - gen4Before}");

            Assert.Equal(0, gen2After - gen2Before);
        }
        finally
        {
            await server.DisposeAsync();

            Task[] disposeTasks = new Task[ClientCount];

            for (int i = 0; i < ClientCount; i++)
                disposeTasks[i] = clients[i].DisposeAsync().AsTask();

            await Task.WhenAll(disposeTasks);
        }
    }
}
