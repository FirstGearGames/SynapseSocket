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
