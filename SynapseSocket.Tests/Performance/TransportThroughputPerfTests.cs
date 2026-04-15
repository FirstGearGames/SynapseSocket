using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
#if DEBUG
using SynapseSocket.Diagnostics;
#endif
using SynapseSocket.Connections;
using SynapseSocket.Core;
using Xunit;
using Xunit.Abstractions;
namespace SynapseSocket.Tests.Performance;

public sealed class TransportThroughputPerfTests
{
    private const int ClientCount = 10;
    private const int ThroughputTestTimeout = 60000;
    private const int MinimumSendByteCount = 10;
    private const int MaximumSendByteCount = 500;
    private const int SendByteCountStep = 10;
    private const int SendLoopCount = 6;
    private const int StepCount = (MaximumSendByteCount - MinimumSendByteCount) / SendByteCountStep + 1; // 50
    private static readonly byte[] SendBuffer;
    private readonly ITestOutputHelper _output;

    static TransportThroughputPerfTests()
    {
        SendBuffer = new byte[MaximumSendByteCount];
        Array.Fill(SendBuffer, (byte)0xAB);
    }

    public TransportThroughputPerfTests(ITestOutputHelper output)
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
            await server.StartAsync(CancellationToken.None);

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

                await clients[i].StartAsync(CancellationToken.None);
                clientToServerConnections[i] = await clients[i].ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
            }

            Assert.True(await TestHarness.WaitFor(() => connectedClientCount >= ClientCount, 5000), "Not all clients established a connection to the server.");

            // Accumulate receive stats on the server side.
            uint receivedMessageCount = 0u;
            ulong receivedByteCount = 0ul;

            server.PacketReceived += packetReceivedEventArgs =>
            {
                int size = packetReceivedEventArgs.Payload.Count;
                byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    packetReceivedEventArgs.Payload.AsSpan().CopyTo(rentedBuffer);
                    Interlocked.Add(ref receivedByteCount, (ulong)size);
                    Interlocked.Increment(ref receivedMessageCount);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
            };

            Stopwatch stopwatch = Stopwatch.StartNew();

            for (int loopIndex = 0; loopIndex < SendLoopCount; loopIndex++)
            {
                for (int byteCount = MinimumSendByteCount; byteCount <= MaximumSendByteCount; byteCount += SendByteCountStep)
                {
                    ArraySegment<byte> segment = new(SendBuffer, 0, byteCount);

                    for (int i = 0; i < ClientCount; i++)
                        _ = clients[i].SendAsync(clientToServerConnections[i], segment, isReliable, CancellationToken.None);
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

            #if PERFTEST
            PerfCounters perf = server.Perf;
            long ppCount = perf.ProcessPacketCallCount;

            static string Avg(long total, long count) => count == 0 ? "n/a" : $"{PerfCounters.TicksToMilliseconds(total / count) * 1000.0:F3} µs";

            _output.WriteLine("");
            _output.WriteLine("-- ProcessPacket (server) --");
            _output.WriteLine($"  Calls : {server.Perf.ProcessPacketCallCount:N0}");
            _output.WriteLine($"  Last  : {PerfCounters.TicksToMilliseconds(server.Perf.ProcessPacketLastElapsedTicks):F4} ms");
            _output.WriteLine($"  Avg   : {PerfCounters.TicksToMilliseconds(server.Perf.ProcessPacketTotalElapsedTicks) / server.Perf.ProcessPacketCallCount:F4} ms");

            long totalCount = perf.SecurityFilterCallCount;

            _output.WriteLine(string.Empty);
            _output.WriteLine($"── Receive loop breakdown (server ingress, {totalCount:N0} datagrams) ────────────");
            _output.WriteLine($"  DateTimeUtcNow        avg {Avg(perf.DateTimeUtcNowTotalElapsedTicks, totalCount),10}   total {PerfCounters.TicksToMilliseconds(perf.DateTimeUtcNowTotalElapsedTicks):F2} ms");
            _output.WriteLine($"  SecurityFilter        avg {Avg(perf.SecurityFilterTotalElapsedTicks, totalCount),10}   total {PerfCounters.TicksToMilliseconds(perf.SecurityFilterTotalElapsedTicks):F2} ms");
            _output.WriteLine($"    Inspect             avg {Avg(perf.SecurityFilterInspectTotalElapsedTicks, totalCount),10}   total {PerfCounters.TicksToMilliseconds(perf.SecurityFilterInspectTotalElapsedTicks):F2} ms");
            _output.WriteLine($"  ProcessPacket total   avg {Avg(perf.ProcessPacketTotalElapsedTicks, ppCount),10}   total {PerfCounters.TicksToMilliseconds(perf.ProcessPacketTotalElapsedTicks):F2} ms");
            _output.WriteLine(string.Empty);
            _output.WriteLine($"── ProcessPacket breakdown ({ppCount:N0} calls) ───────────────────────────────────");
            _output.WriteLine($"  HeaderParse           avg {Avg(perf.HeaderParseTotalElapsedTicks, ppCount),10}   total {PerfCounters.TicksToMilliseconds(perf.HeaderParseTotalElapsedTicks):F2} ms");
            _output.WriteLine($"  PayloadCopy           avg {Avg(perf.PayloadCopyTotalElapsedTicks, perf.PayloadCopyCallCount),10}   total {PerfCounters.TicksToMilliseconds(perf.PayloadCopyTotalElapsedTicks):F2} ms   calls {perf.PayloadCopyCallCount:N0}");

            if (isReliable)
            {
                _output.WriteLine($"  EnqueueOrSendAck      avg {Avg(perf.EnqueueOrSendAckTotalElapsedTicks, perf.EnqueueOrSendAckCallCount),10}   total {PerfCounters.TicksToMilliseconds(perf.EnqueueOrSendAckTotalElapsedTicks):F2} ms   calls {perf.EnqueueOrSendAckCallCount:N0}");
                _output.WriteLine($"  DeliverOrdered (lock) avg {Avg(perf.DeliverOrderedTotalElapsedTicks, perf.DeliverOrderedCallCount),10}   total {PerfCounters.TicksToMilliseconds(perf.DeliverOrderedTotalElapsedTicks):F2} ms   calls {perf.DeliverOrderedCallCount:N0}");
                _output.WriteLine($"  DeliverOrdered (cbks) avg {Avg(perf.DeliverOrderedCallbacksTotalElapsedTicks, perf.DeliverOrderedCallbacksCallCount),10}   total {PerfCounters.TicksToMilliseconds(perf.DeliverOrderedCallbacksTotalElapsedTicks):F2} ms   calls {perf.DeliverOrderedCallbacksCallCount:N0}");
            }
            else
            {
                _output.WriteLine($"  PayloadDelivered cbk  avg {Avg(perf.PayloadDeliveredCallbackTotalElapsedTicks, perf.PayloadDeliveredCallbackCallCount),10}   total {PerfCounters.TicksToMilliseconds(perf.PayloadDeliveredCallbackTotalElapsedTicks):F2} ms   calls {perf.PayloadDeliveredCallbackCallCount:N0}");
            }
            
            _output.WriteLine("");
            _output.WriteLine("-- DeliverOrdered lock (server) --");
            _output.WriteLine($"  Calls : {server.Perf.DeliverOrderedCallCount:N0}");
            _output.WriteLine($"  Last  : {PerfCounters.TicksToMilliseconds(server.Perf.DeliverOrderedLastElapsedTicks):F4} ms");
            _output.WriteLine($"  Avg   : {PerfCounters.TicksToMilliseconds(server.Perf.DeliverOrderedTotalElapsedTicks) / server.Perf.DeliverOrderedCallCount:F4} ms");

            _output.WriteLine("");
            _output.WriteLine("-- Maintenance (server) --");
            _output.WriteLine($"  Ticks                   : {server.Perf.MaintenanceTickCallCount:N0}");
            _output.WriteLine($"  Avg tick                : {PerfCounters.TicksToMilliseconds(server.Perf.MaintenanceTickTotalElapsedTicks) / server.Perf.MaintenanceTickCallCount:F4} ms");
            _output.WriteLine($"  Avg keep-alive sweep    : {PerfCounters.TicksToMilliseconds(server.Perf.KeepAliveSweepTotalElapsedTicks) / server.Perf.KeepAliveSweepCallCount:F4} ms");
            _output.WriteLine($"  Avg retransmit sweep    : {PerfCounters.TicksToMilliseconds(server.Perf.ReliableRetransmitSweepTotalElapsedTicks) / server.Perf.ReliableRetransmitSweepCallCount:F4} ms");
            _output.WriteLine($"  Avg seg-timeout sweep   : {PerfCounters.TicksToMilliseconds(server.Perf.SegmentAssemblyTimeoutSweepTotalElapsedTicks) / server.Perf.SegmentAssemblyTimeoutSweepCallCount:F4} ms");
            _output.WriteLine($"  Avg ack-flush sweep     : {PerfCounters.TicksToMilliseconds(server.Perf.AckBatchFlushSweepTotalElapsedTicks) / server.Perf.AckBatchFlushSweepCallCount:F4} ms");
            _output.WriteLine($"  Avg rate-bucket cleanup : {PerfCounters.TicksToMilliseconds(server.Perf.RateBucketCleanupTotalElapsedTicks) / server.Perf.RateBucketCleanupCallCount:F4} ms");
            #endif

            //Assert.Equal(expectedMessageCount, receivedMessageCount);
            //Assert.Equal(expectedByteCount, Volatile.Read(ref receivedByteCount));
        }
        finally
        {
            await server.DisposeAsync();

            foreach (SynapseManager client in clients)
                await client.DisposeAsync();
        }
    }
}