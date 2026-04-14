using System;
using System.Buffers;
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
    private const int SendLoopCount = 20;
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
    public async Task MeasureSendReceiveThroughput_Reliable()
    {
        await RunThroughputTestAsync(isReliable: true);
    }

    [Fact(Timeout = ThroughputTestTimeout)]
    public async Task MeasureSendReceiveThroughput_Unreliable()
    {
        await RunThroughputTestAsync(isReliable: false);
    }

    private async Task RunThroughputTestAsync(bool isReliable)
    {
        int port = TestHarness.GetFreePort();
        // MaximumPending must exceed the total burst per client (SendLoopCount * StepCount) so fire-and-forget sends do not silently fail.
        const uint MaximumPendingForBurst = SendLoopCount * StepCount * 2;

        SynapseManager server = new(TestHarness.ServerConfig(port));
        SynapseManager[] clients = new SynapseManager[ClientCount];
        SynapseConnection[] clientToServerConnections = new SynapseConnection[ClientCount];

        for (int i = 0; i < ClientCount; i++)
            clients[i] = new(TestHarness.ClientConfig(c => c.Reliable.MaximumPending = MaximumPendingForBurst));

        try
        {
            await server.StartAsync();

            int connectedClientCount = 0;
            server.ConnectionEstablished += _ => Interlocked.Increment(ref connectedClientCount);

            for (int i = 0; i < ClientCount; i++)
            {
                clients[i].PacketReceived += packetReceivedEventArgs =>
                {
                    int size = packetReceivedEventArgs.Payload.Count;
                    byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(size);
                    try
                    {
                        packetReceivedEventArgs.Payload.AsSpan().CopyTo(rentedBuffer);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rentedBuffer);
                    }
                };

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
                        _ = clients[i].SendAsync(clientToServerConnections[i], segment, isReliable);
                }
            }

            // SendLoopCount * StepCount * ClientCount = 2 * 50 * 10 = 1000
            uint expectedMessageCount = SendLoopCount * StepCount * ClientCount;

            // Sum of byte counts per loop per client: (10+500)*50/2 = 12750; total across loops and clients: 2*12750*10 = 255000
            ulong expectedByteCount = SendLoopCount * (ulong)((MinimumSendByteCount + MaximumSendByteCount) * StepCount / 2) * ClientCount;

            while (Volatile.Read(ref receivedMessageCount) < expectedMessageCount)
                await Task.Delay(1).ConfigureAwait(false);

            stopwatch.Stop();

            double elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            double megabytesPerSecond = receivedByteCount / elapsedSeconds / (1024.0 * 1024.0);
            double messagesPerSecond = receivedMessageCount / elapsedSeconds;

            _output.WriteLine($"Delivery method   : {(isReliable ? "Reliable" : "Unreliable")}");
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
