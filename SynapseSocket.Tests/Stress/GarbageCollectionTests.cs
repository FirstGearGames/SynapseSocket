using System;
using System.Net;
using System.Threading;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;

namespace SynapseSocket.Tests.Stress;

[SettleAfterTest(beforeMs: 10000)]
public sealed class GarbageCollectionTests
{
    private const int ClientCount = 10;
    private const int GarbageCollectionTestTimeout = 300000;
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

    [Fact]
    public void SendsDoNotTriggerGarbageCollection()
    {
        int port = TestHarness.GetFreePort();
        SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Security.MaximumPacketsPerSecond = 0;
            c.Security.MaximumBytesPerSecond = 0;
            c.Security.MaximumOutOfOrderReliablePackets = 0;
            c.Connection.TimeoutMilliseconds = 180000;
        }));
        SynapseManager[] clients = new SynapseManager[ClientCount];
        SynapseConnection[] clientToServerConnections = new SynapseConnection[ClientCount];

        for (int i = 0; i < ClientCount; i++)
            clients[i] = new(TestHarness.ClientConfig(c =>
            {
                c.Reliable.MaximumRetries = 500;
            }));

        // One reusable array of every engine so the pump never allocates a params array during the measured phase.
        SynapseManager[] allEngines = new SynapseManager[ClientCount + 1];
        allEngines[0] = server;
        for (int i = 0; i < ClientCount; i++)
            allEngines[i + 1] = clients[i];

        try
        {
            server.Start();

            int connectedClientCount = 0;
            server.ConnectionEstablished += _ => Interlocked.Increment(ref connectedClientCount);

            for (int i = 0; i < ClientCount; i++)
            {
                clients[i].Start();
                clientToServerConnections[i] = clients[i].Connect(new(IPAddress.Loopback, port));
            }

            Assert.True(TestHarness.PumpUntil(() => connectedClientCount >= ClientCount, 5000, allEngines),
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
                    clients[i].Send(clientToServerConnections[i], segment, isReliable: true);

                TestHarness.PumpFor(WarmupDelayMilliseconds, allEngines);
            }

            Assert.True(TestHarness.PumpUntil(() => Volatile.Read(ref totalReceivedCount) >= warmupExpectedCount, 60000, allEngines),
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
                        clients[i].Send(clientToServerConnections[i], segment, isReliable: true);

                    TestHarness.PumpFor(SendDelayMilliseconds, allEngines);
                }
            }

            Assert.True(TestHarness.PumpUntil(() => Volatile.Read(ref totalReceivedCount) >= measuredExpectedTotal, 45000, allEngines),
                $"Measured sends incomplete: server received {totalReceivedCount} of {measuredExpectedTotal} total packets.");

            Assert.Equal(gen0Before, GC.CollectionCount(0));
            Assert.Equal(gen1Before, GC.CollectionCount(1));
            Assert.Equal(gen2Before, GC.CollectionCount(2));
        }
        finally
        {
            server.Dispose();

            foreach (SynapseManager client in clients)
                client.Dispose();
        }
    }
}
