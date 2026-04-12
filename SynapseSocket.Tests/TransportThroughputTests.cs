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

public sealed class TransportThroughputTests
{
    private const int ClientCount = 10;
    private const int ThroughputTestTimeout = 60000;
    private const int MinimumSendByteCount = 10;
    private const int MaximumSendByteCount = 500;
    private const int SendByteCountStep = 10;
    private const int SendLoopCount = 2;
    private const int StepCount = (MaximumSendByteCount - MinimumSendByteCount) / SendByteCountStep + 1; // 50

    private static readonly byte[] SendBuffer;

    private readonly ITestOutputHelper _output;

    static TransportThroughputTests()
    {
        SendBuffer = new byte[MaximumSendByteCount];
        Array.Fill(SendBuffer, (byte)0xAB);
    }

    public TransportThroughputTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = ThroughputTestTimeout)]
    public async Task MeasureSendReceiveThroughput()
    {
        int port = TestHarness.GetFreePort();
        SynapseManager server = new(TestHarness.ServerConfig(port));
        SynapseManager[] clients = new SynapseManager[ClientCount];
        SynapseConnection[] clientToServerConnections = new SynapseConnection[ClientCount];

        for (int i = 0; i < ClientCount; i++)
            clients[i] = new(TestHarness.ClientConfig());

        try
        {
            await server.StartAsync();

            int connectedClientCount = 0;
            server.ConnectionEstablished += _ => Interlocked.Increment(ref connectedClientCount);

            for (int i = 0; i < ClientCount; i++)
            {
                await clients[i].StartAsync();
                clientToServerConnections[i] = await clients[i].ConnectAsync(new(IPAddress.Loopback, port));
            }

            Assert.True(await TestHarness.WaitFor(() => connectedClientCount >= ClientCount, 5000),
                "Not all clients established a connection to the server.");

            // Accumulate receive stats on the server side.
            uint receivedMessageCount = 0u;
            ulong receivedByteCount = 0ul;

            server.PacketReceived += packetReceivedEventArgs =>
            {
                Interlocked.Add(ref receivedByteCount, (ulong)packetReceivedEventArgs.Payload.Count);
                Interlocked.Increment(ref receivedMessageCount);
            };

            Stopwatch stopwatch = Stopwatch.StartNew();

            for (int loopIndex = 0; loopIndex < SendLoopCount; loopIndex++)
            {
                for (int byteCount = MinimumSendByteCount; byteCount <= MaximumSendByteCount; byteCount += SendByteCountStep)
                {
                    ArraySegment<byte> segment = new(SendBuffer, 0, byteCount);

                    for (int i = 0; i < ClientCount; i++)
                        _ = clients[i].SendAsync(clientToServerConnections[i], segment, isReliable: true);
                }
            }

            // SendLoopCount * StepCount * ClientCount = 2 * 50 * 10 = 1000
            uint expectedMessageCount = SendLoopCount * StepCount * ClientCount;

            // Sum of byte counts per loop per client: (10+500)*50/2 = 12750; total across loops and clients: 2*12750*10 = 255000
            ulong expectedByteCount = SendLoopCount * (ulong)((MinimumSendByteCount + MaximumSendByteCount) * StepCount / 2) * ClientCount;

            Assert.True(await TestHarness.WaitFor(() => Volatile.Read(ref receivedMessageCount) >= expectedMessageCount, 30000),
                $"Not all messages were received. Got {receivedMessageCount} of {expectedMessageCount}.");

            stopwatch.Stop();

            double elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            double megabytesPerSecond = receivedByteCount / elapsedSeconds / (1024.0 * 1024.0);
            double messagesPerSecond = receivedMessageCount / elapsedSeconds;

            _output.WriteLine($"Messages received : {receivedMessageCount:N0} / {expectedMessageCount:N0}");
            _output.WriteLine($"Bytes received    : {receivedByteCount:N0} / {expectedByteCount:N0}");
            _output.WriteLine($"Elapsed           : {elapsedMilliseconds:F2} ms");
            _output.WriteLine($"Throughput        : {megabytesPerSecond:F3} MB/s");
            _output.WriteLine($"Message rate      : {messagesPerSecond:F0} msg/s");

            Assert.Equal(expectedMessageCount, receivedMessageCount);
            Assert.Equal(expectedByteCount, Volatile.Read(ref receivedByteCount));
        }
        finally
        {
            await server.DisposeAsync();

            foreach (SynapseManager client in clients)
                await client.DisposeAsync();
        }
    }
}
