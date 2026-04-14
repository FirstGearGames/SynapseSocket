using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;

namespace SynapseSocket.Tests;

public sealed class GarbageCollectionTests
{
    private const int ClientCount = 10;
    private const int GarbageCollectionTestTimeout = 35000;
    private const int MinimumSendByteCount = 10;
    private const int MaximumSendByteCount = 500;
    private const int SendByteCountStep = 10;
    private const int SendLoopCount = 2;
    private const int SendDelayMilliseconds = 100;
    private const int WarmupDelayMilliseconds = 50;

    private static readonly byte[] SendBuffer;

    static GarbageCollectionTests()
    {
        SendBuffer = new byte[MaximumSendByteCount];
        Array.Fill(SendBuffer, (byte)0xAB);
    }

    [Fact(Timeout = GarbageCollectionTestTimeout)]
    public async Task SendsDoNotTriggerGarbageCollection()
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

            int connectedClientCount = 0;
            server.ConnectionEstablished += _ => Interlocked.Increment(ref connectedClientCount);

            for (int i = 0; i < ClientCount; i++)
            {
                await clients[i].StartAsync(CancellationToken.None);
                clientToServerConnections[i] = await clients[i].ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
            }

            Assert.True(await TestHarness.WaitFor(() => connectedClientCount >= ClientCount, 5000),
                "Not all clients established a connection to the server.");

            // Single rolling counter used for both warmup and measured phases.
            int totalReceivedCount = 0;
            server.PacketReceived += _ => Interlocked.Increment(ref totalReceivedCount);

            // Prime SynapseSocket's internal pools across all send byte counts so that the
            // measured loops do not trigger pool growth allocations.
            int stepCount = (MaximumSendByteCount - MinimumSendByteCount) / SendByteCountStep + 1;
            int warmupExpectedCount = stepCount * ClientCount;

            for (int byteCount = MinimumSendByteCount; byteCount <= MaximumSendByteCount; byteCount += SendByteCountStep)
            {
                ArraySegment<byte> segment = new(SendBuffer, 0, byteCount);

                for (int i = 0; i < ClientCount; i++)
                    _ = clients[i].SendAsync(clientToServerConnections[i], segment, isReliable: true, CancellationToken.None);

                Thread.Sleep(WarmupDelayMilliseconds);
            }

            Assert.True(await TestHarness.WaitFor(() => totalReceivedCount >= warmupExpectedCount, 10000),
                $"Warmup incomplete: server received {totalReceivedCount} of {warmupExpectedCount} packets.");

            // Collect all warmup and initialization allocations to establish a stable baseline.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            int measuredExpectedTotal = warmupExpectedCount + SendLoopCount * stepCount * ClientCount;

            for (int loopIndex = 0; loopIndex < SendLoopCount; loopIndex++)
            {
                for (int byteCount = MinimumSendByteCount; byteCount <= MaximumSendByteCount; byteCount += SendByteCountStep)
                {
                    ArraySegment<byte> segment = new(SendBuffer, 0, byteCount);

                    for (int i = 0; i < ClientCount; i++)
                        _ = clients[i].SendAsync(clientToServerConnections[i], segment, isReliable: true, CancellationToken.None);

                    Thread.Sleep(SendDelayMilliseconds);
                }
            }

            Assert.True(await TestHarness.WaitFor(() => totalReceivedCount >= measuredExpectedTotal, 15000),
                $"Measured sends incomplete: server received {totalReceivedCount} of {measuredExpectedTotal} total packets.");

            Assert.Equal(gen0Before, GC.CollectionCount(0));
            Assert.Equal(gen1Before, GC.CollectionCount(1));
            Assert.Equal(gen2Before, GC.CollectionCount(2));
        }
        finally
        {
            await server.DisposeAsync();

            foreach (SynapseManager client in clients)
                await client.DisposeAsync();
        }
    }
}
