using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using Xunit.Abstractions;

namespace SynapseSocket.Tests;

public sealed class ConnectionStressTests
{
    private const int ClientCount = 2000;
    private const int SendsPerClient = 4;
    private const int PayloadSize = 300;
    private const int ExpectedPackets = ClientCount * SendsPerClient;
    private const int StressTestTimeoutMs = 120000;
    private const int LifecycleTestTimeoutMs = 300000;

    private static readonly byte[] SendBuffer;

    private readonly ITestOutputHelper _output;

    static ConnectionStressTests()
    {
        SendBuffer = new byte[PayloadSize];
        Array.Fill(SendBuffer, (byte)0xAB);
    }

    public ConnectionStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = LifecycleTestTimeoutMs)]
    public async Task StressTest_1000Clients_ConnectSendDisconnect_4Cycles()
    {
        const int CycleClientCount = 2000;
        const int CycleCount = 4;
        const int ConnectWaitMs = 20000;
        const int ReceiveWaitMs = 15000;
        const int AckWaitMs = 5000;
        const int DisconnectWaitMs = 20000;
        const bool WaitForAck = true;

        int port = TestHarness.GetFreePort();
        SynapseManager server = new(TestHarness.ServerConfig(port, c => c.DisableHandshakeReplayProtection = true));
        SynapseManager[] clients = new SynapseManager[CycleClientCount];
        SynapseConnection[] connections = new SynapseConnection[CycleClientCount];

        for (int i = 0; i < CycleClientCount; i++)
            clients[i] = new(TestHarness.ClientConfig(c => c.Reliable.MaximumRetries = 120));

        try
        {
            await server.StartAsync(CancellationToken.None);

            // Start all clients once; they reconnect each cycle.
            Task[] startTasks = new Task[CycleClientCount];

            for (int i = 0; i < CycleClientCount; i++)
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
            for (int i = 0; i < CycleClientCount; i++)
                clients[i].ConnectionClosed += _ => Interlocked.Increment(ref totalClientClosedCount);

            async Task RunCycleAsync()
            {
                int connectTarget = Volatile.Read(ref totalConnectedCount) + CycleClientCount;
                int receiveTarget = Volatile.Read(ref totalReceivedCount) + CycleClientCount;
                int closeTarget = Volatile.Read(ref totalClientClosedCount) + CycleClientCount;

                Task[] connectTasks = new Task[CycleClientCount];

                for (int i = 0; i < CycleClientCount; i++)
                {
                    int idx = i;
                    connectTasks[idx] = Task.Run(async () =>
                        connections[idx] = await clients[idx].ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None));
                }

                await Task.WhenAll(connectTasks);

                Assert.True(await TestHarness.WaitFor(() => Volatile.Read(ref totalConnectedCount) >= connectTarget, ConnectWaitMs),
                    $"Only {Volatile.Read(ref totalConnectedCount)} / {connectTarget} clients connected.");

                ArraySegment<byte> payload = new(SendBuffer);

                for (int i = 0; i < CycleClientCount; i++)
                    _ = clients[i].SendAsync(connections[i], payload, false, CancellationToken.None);

                Assert.True(await TestHarness.WaitFor(() => Volatile.Read(ref totalReceivedCount) >= receiveTarget, ReceiveWaitMs),
                    $"Only {Volatile.Read(ref totalReceivedCount)} / {receiveTarget} packets received.");

                if (WaitForAck)
                {
                    Assert.True(await TestHarness.WaitFor(() =>
                    {
                        for (int i = 0; i < CycleClientCount; i++)
                        {
                            if (!connections[i].PendingReliableQueue.IsEmpty)
                                return false;
                        }

                        return true;
                    }, AckWaitMs), "Not all reliable sends were acknowledged within the ACK wait window.");
                }

                Task[] disconnectTasks = new Task[CycleClientCount];

                for (int i = 0; i < CycleClientCount; i++)
                {
                    int idx = i;
                    disconnectTasks[idx] = clients[idx].DisconnectAsync(connections[idx], CancellationToken.None);
                }

                await Task.WhenAll(disconnectTasks);

                Assert.True(await TestHarness.WaitFor(() => Volatile.Read(ref totalClientClosedCount) >= closeTarget, DisconnectWaitMs),
                    $"Only {Volatile.Read(ref totalClientClosedCount)} / {closeTarget} connections closed.");
            }

            // Warmup — primes object pools before GC baseline.
            await RunCycleAsync();

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);

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

            _output.WriteLine($"Clients           : {CycleClientCount:N0}");
            _output.WriteLine($"Cycles            : {CycleCount}");
            _output.WriteLine($"Payload size      : {PayloadSize} bytes");

            for (int i = 0; i < CycleCount; i++)
                _output.WriteLine($"Cycle {i + 1}           : {cycleTimes[i]:F2} ms");

            _output.WriteLine($"Total elapsed     : {totalStopwatch.Elapsed.TotalMilliseconds:F2} ms");
            _output.WriteLine($"GC Gen0           : {gen0After - gen0Before}");
            _output.WriteLine($"GC Gen1           : {gen1After - gen1Before}");
            _output.WriteLine($"GC Gen2           : {gen2After - gen2Before}");

            Assert.Equal(0, gen2After - gen2Before);
        }
        finally
        {
            await server.DisposeAsync();

            Task[] disposeTasks = new Task[CycleClientCount];

            for (int i = 0; i < CycleClientCount; i++)
                disposeTasks[i] = clients[i].DisposeAsync().AsTask();

            await Task.WhenAll(disposeTasks);
        }
    }

    [Fact(Timeout = StressTestTimeoutMs)]
    public async Task StressTest_2000Clients_4SendsEach_Unreliable()
    {
        int port = TestHarness.GetFreePort();

        SynapseManager server = new(TestHarness.ServerConfig(port));
        SynapseManager[] clients = new SynapseManager[ClientCount];
        SynapseConnection[] clientToServerConnections = new SynapseConnection[ClientCount];

        for (int i = 0; i < ClientCount; i++)
            clients[i] = new(TestHarness.ClientConfig());

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
                $"Only {Volatile.Read(ref connectedCount)} / {ClientCount} clients connected within timeout.");

            int receivedCount = 0;
            server.PacketReceived += _ => Interlocked.Increment(ref receivedCount);

            ArraySegment<byte> payload = new(SendBuffer);
            Stopwatch stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < ClientCount; i++)
            {
                for (int s = 0; s < SendsPerClient; s++)
                    _ = clients[i].SendAsync(clientToServerConnections[i], payload, false, CancellationToken.None);
            }

            while (Volatile.Read(ref receivedCount) < ExpectedPackets)
                await Task.Delay(1).ConfigureAwait(false);

            stopwatch.Stop();

            double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            double elapsedSec = stopwatch.Elapsed.TotalSeconds;
            double messagesPerSec = ExpectedPackets / elapsedSec;

            _output.WriteLine($"Clients           : {ClientCount:N0}");
            _output.WriteLine($"Sends per client  : {SendsPerClient}");
            _output.WriteLine($"Payload size      : {PayloadSize} bytes");
            _output.WriteLine($"Packets received  : {Volatile.Read(ref receivedCount):N0} / {ExpectedPackets:N0}");
            _output.WriteLine($"Elapsed           : {elapsedMs:F2} ms");
            _output.WriteLine($"Message rate      : {messagesPerSec:F0} msg/s");

            Assert.Equal(ExpectedPackets, Volatile.Read(ref receivedCount));
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

    [Fact(Timeout = StressTestTimeoutMs)]
    public async Task StressTest_2000Clients_4SendsEach_Reliable()
    {
        int port = TestHarness.GetFreePort();

        SynapseManager server = new(TestHarness.ServerConfig(port));
        SynapseManager[] clients = new SynapseManager[ClientCount];
        SynapseConnection[] clientToServerConnections = new SynapseConnection[ClientCount];

        for (int i = 0; i < ClientCount; i++)
            clients[i] = new(TestHarness.ClientConfig(c => c.Reliable.MaximumPending = SendsPerClient * 2));

        try
        {
            await server.StartAsync(CancellationToken.None);

            int connectedCount = 0;
            server.ConnectionEstablished += _ => Interlocked.Increment(ref connectedCount);

            // Start and connect all clients concurrently.
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
                $"Only {Volatile.Read(ref connectedCount)} / {ClientCount} clients connected within timeout.");

            int receivedCount = 0;
            server.PacketReceived += _ =>  Interlocked.Increment(ref receivedCount);

            ArraySegment<byte> payload = new(SendBuffer);
            Stopwatch stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < ClientCount; i++)
            {
                for (int s = 0; s < SendsPerClient; s++)
                    _ = clients[i].SendAsync(clientToServerConnections[i], payload, true, CancellationToken.None);
            }

            while (Volatile.Read(ref receivedCount) < ExpectedPackets)
                await Task.Delay(1).ConfigureAwait(false);

            stopwatch.Stop();

            double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            double elapsedSec = stopwatch.Elapsed.TotalSeconds;
            double messagesPerSec = ExpectedPackets / elapsedSec;

            _output.WriteLine($"Clients           : {ClientCount:N0}");
            _output.WriteLine($"Sends per client  : {SendsPerClient}");
            _output.WriteLine($"Payload size      : {PayloadSize} bytes");
            _output.WriteLine($"Packets received  : {Volatile.Read(ref receivedCount):N0} / {ExpectedPackets:N0}");
            _output.WriteLine($"Elapsed           : {elapsedMs:F2} ms");
            _output.WriteLine($"Message rate      : {messagesPerSec:F0} msg/s");

            Assert.Equal(ExpectedPackets, Volatile.Read(ref receivedCount));
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
