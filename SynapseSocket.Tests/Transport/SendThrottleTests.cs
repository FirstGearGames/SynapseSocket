using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;
using SynapseSocket.Packets;
using Xunit;

namespace SynapseSocket.Tests.Transport;

/// <summary>
/// Live coverage for the per-connection send throttle: a client and a server <see cref="SynapseManager"/> over UDP loopback, the
/// client throttled, every result read through the public API.
/// </summary>
public sealed class SendThrottleTests
{
    /// <summary>
    /// The length of each test payload, so a datagram is this plus its header.
    /// </summary>
    private const int PayloadLength = 1000;
    /// <summary>
    /// A release limit that covers nine test datagrams on either channel and not ten.
    /// </summary>
    private const uint NineDatagramRelease = 9500;
    /// <summary>
    /// A release limit below one datagram, so each release sends exactly one.
    /// </summary>
    private const uint OneDatagramRelease = 1;
    /// <summary>
    /// A queue limit that holds five unreliable test datagrams and not six.
    /// </summary>
    private const uint FiveDatagramQueue = 5 * (PayloadLength + 1);
    /// <summary>
    /// How long a test waits for delivery before failing.
    /// </summary>
    private const int DeliveryTimeoutMilliseconds = 5000;

    /// <summary>
    /// The throttle is off unless enabled, and every send then reports <see cref="SendResult.Sent"/> and arrives.
    /// </summary>
    [Fact]
    public void Throttle_Is_Disabled_By_Default_And_Every_Send_Goes_Out()
    {
        Assert.False(new SynapseConfig().SendThrottle.Enabled);

        using ConnectedPair connectedPair = ConnectedPair.Create(configureThrottle: null);

        for (int i = 0; i < 30; i++)
            Assert.Equal(SendResult.Sent, connectedPair.Client.Send(connectedPair.Connection, CreatePayload(i), isReliable: false));

        Assert.True(connectedPair.PumpUntilReceived(30), $"The server received [{connectedPair.ReceivedPayloads.Count}] of [30].");
    }

    /// <summary>
    /// A burst past one release sends what the release covers, queues the rest, and releases it over later polls no sooner than the
    /// interval apart, with every payload arriving intact.
    /// </summary>
    [Fact]
    public void Burst_Past_One_Release_Is_Queued_And_Released_Over_Later_Polls()
    {
        const int SendCount = 45;
        const uint ReleaseIntervalMilliseconds = 50;

        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = NineDatagramRelease;
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = ReleaseIntervalMilliseconds;
        });

        List<SendResult> sendResults = [];

        for (int i = 0; i < SendCount; i++)
            sendResults.Add(connectedPair.Client.Send(connectedPair.Connection, CreatePayload(i), isReliable: false));

        for (int i = 0; i < SendCount; i++)
            Assert.Equal(i < 9 ? SendResult.Sent : SendResult.Queued, sendResults[i]);

        // Only the server polls, so nothing the client queued has been released yet.
        TestHarness.PumpFor(durationMs: 100, connectedPair.Server);
        Assert.Equal(9, connectedPair.ReceivedPayloads.Count);

        Stopwatch stopwatch = Stopwatch.StartNew();
        Assert.True(connectedPair.PumpUntilReceived(SendCount), $"The server received [{connectedPair.ReceivedPayloads.Count}] of [{SendCount}].");

        // The 36 queued go in four releases of nine. The first is already due; each of the other three opens at least the interval after the last.
        Assert.True(stopwatch.ElapsedMilliseconds >= 3 * ReleaseIntervalMilliseconds, $"The queued releases all arrived within [{stopwatch.ElapsedMilliseconds}] ms.");
        connectedPair.AssertReceivedEachOnce(SendCount);
    }

    /// <summary>
    /// Under <see cref="SendQueueFullAction.DropNewest"/>, a send the full queue cannot take is dropped, reported, and never arrives.
    /// </summary>
    [Fact]
    public void Full_Queue_With_DropNewest_Drops_The_New_Payload()
    {
        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = OneDatagramRelease;
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = 20;
            sendThrottleConfig.MaximumQueuedBytes = FiveDatagramQueue;
            sendThrottleConfig.QueueFullAction = SendQueueFullAction.DropNewest;
        });

        List<SendResult> sendResults = [];

        for (int i = 0; i < 10; i++)
            sendResults.Add(connectedPair.Client.Send(connectedPair.Connection, CreatePayload(i), isReliable: false));

        Assert.Equal([SendResult.Sent, SendResult.Queued, SendResult.Queued, SendResult.Queued, SendResult.Queued, SendResult.Queued, SendResult.Dropped, SendResult.Dropped, SendResult.Dropped, SendResult.Dropped], sendResults);
        Assert.Equal(4, connectedPair.Client.Telemetry.PacketsDroppedOut);

        Assert.True(connectedPair.PumpUntilReceived(6), $"The server received [{connectedPair.ReceivedPayloads.Count}] of [6].");
        TestHarness.PumpFor(durationMs: 200, connectedPair.Server, connectedPair.Client);
        connectedPair.AssertReceivedEachOnce(6);
    }

    /// <summary>
    /// Under <see cref="SendQueueFullAction.SendOldest"/>, a full queue sends its oldest datagrams at once to make room, and nothing
    /// is lost.
    /// </summary>
    [Fact]
    public void Full_Queue_With_SendOldest_Sends_The_Oldest_At_Once_And_Loses_Nothing()
    {
        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = OneDatagramRelease;
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = 20;
            sendThrottleConfig.MaximumQueuedBytes = FiveDatagramQueue;
        });

        List<SendResult> sendResults = [];

        for (int i = 0; i < 10; i++)
            sendResults.Add(connectedPair.Client.Send(connectedPair.Connection, CreatePayload(i), isReliable: false));

        Assert.Equal(SendResult.Sent, sendResults[0]);

        for (int i = 1; i < 10; i++)
            Assert.Equal(SendResult.Queued, sendResults[i]);

        // The first went out directly and the four sends past a full queue each pushed the oldest out, all before the client polled.
        TestHarness.PumpFor(durationMs: 100, connectedPair.Server);
        Assert.Equal(5, connectedPair.ReceivedPayloads.Count);

        Assert.True(connectedPair.PumpUntilReceived(10), $"The server received [{connectedPair.ReceivedPayloads.Count}] of [10].");
        Assert.Equal(0, connectedPair.Client.Telemetry.PacketsDroppedOut);
        connectedPair.AssertReceivedEachOnce(10);
    }

    /// <summary>
    /// Reliable payloads held in the queue for longer than the resend interval arrive in order and are never resent, because a
    /// queued packet's resend timer starts only when it leaves.
    /// </summary>
    [Fact]
    public void Queued_Reliable_Payloads_Arrive_In_Order_Without_Early_Resends()
    {
        const int SendCount = 30;

        // One datagram per 20 ms release holds the last for about 580 ms, past the 250 ms resend interval.
        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = OneDatagramRelease;
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = 20;
        });

        for (int i = 0; i < SendCount; i++)
            connectedPair.Client.Send(connectedPair.Connection, CreatePayload(i), isReliable: true);

        Assert.True(connectedPair.PumpUntilReceived(SendCount), $"The server received [{connectedPair.ReceivedPayloads.Count}] of [{SendCount}].");

        for (int i = 0; i < SendCount; i++)
            Assert.Equal(CreatePayload(i), connectedPair.ReceivedPayloads[i]);

        Assert.Equal(0, connectedPair.Client.Telemetry.ReliableResends);
    }

    /// <summary>
    /// An acknowledgement of a reliable packet the throttle still holds cannot be genuine, so it does not release the packet, and
    /// every payload still arrives intact and in order.
    /// </summary>
    [Fact]
    public void Forged_Ack_For_A_Queued_Reliable_Payload_Does_Not_Release_It()
    {
        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = OneDatagramRelease;
            // Long enough that nothing queued is released before the forged acknowledgement is read.
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = 200;
        });

        // Sequence 0 goes out; 1 and 2 wait in the client's queue.
        Assert.Equal(SendResult.Sent, connectedPair.Client.Send(connectedPair.Connection, CreatePayload(0), isReliable: true));
        Assert.Equal(SendResult.Queued, connectedPair.Client.Send(connectedPair.Connection, CreatePayload(1), isReliable: true));
        Assert.Equal(SendResult.Queued, connectedPair.Client.Send(connectedPair.Connection, CreatePayload(2), isReliable: true));

        // The server acknowledges 1 and 2 before either has been sent, as a misbehaving peer could.
        byte[] forgedAck = [(byte)PacketType.Ack, 1, 0, 2, 0];
        connectedPair.Server.SendRaw(connectedPair.ClientEndPoint, forgedAck);
        TestHarness.PumpFor(durationMs: 20, connectedPair.Client);

        // A further send rents the same size of buffer, so a released packet's bytes would be overwritten here.
        Assert.Equal(SendResult.Queued, connectedPair.Client.Send(connectedPair.Connection, CreatePayload(3), isReliable: true));

        Assert.True(connectedPair.PumpUntilReceived(4), $"The server received [{connectedPair.ReceivedPayloads.Count}] of [4].");

        for (int i = 0; i < 4; i++)
            Assert.Equal(CreatePayload(i), connectedPair.ReceivedPayloads[i]);
    }

    /// <summary>
    /// Segmented payloads on both channels are queued by reference to their split and reassemble intact.
    /// </summary>
    [Fact]
    public void Queued_Segmented_Payloads_Reassemble_Intact()
    {
        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = NineDatagramRelease;
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = 20;
        });

        byte[] unreliablePayload = CreateLargePayload(length: 40_000, seed: 1);
        byte[] reliablePayload = CreateLargePayload(length: 40_000, seed: 2);

        Assert.Equal(SendResult.Queued, connectedPair.Client.Send(connectedPair.Connection, unreliablePayload, isReliable: false));
        Assert.Equal(SendResult.Queued, connectedPair.Client.Send(connectedPair.Connection, reliablePayload, isReliable: true));

        Assert.True(connectedPair.PumpUntilReceived(2), $"The server received [{connectedPair.ReceivedPayloads.Count}] of [2].");
        Assert.Contains(connectedPair.ReceivedPayloads, receivedPayload => receivedPayload.AsSpan().SequenceEqual(unreliablePayload));
        Assert.Contains(connectedPair.ReceivedPayloads, receivedPayload => receivedPayload.AsSpan().SequenceEqual(reliablePayload));
    }

    /// <summary>
    /// Under <see cref="SendQueueFullAction.DropNewest"/>, a reliable payload whose segments the queue cannot all take is dropped whole
    /// before it takes a sequence number, so the reliable channel carries on with the next payload.
    /// </summary>
    [Fact]
    public void Segmented_Payload_Past_The_Queue_Is_Dropped_Whole_And_The_Reliable_Channel_Carries_On()
    {
        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = NineDatagramRelease;
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = 20;
            sendThrottleConfig.MaximumQueuedBytes = 10_000;
            sendThrottleConfig.QueueFullAction = SendQueueFullAction.DropNewest;
        });

        Assert.Equal(SendResult.Dropped, connectedPair.Client.Send(connectedPair.Connection, CreateLargePayload(length: 40_000, seed: 3), isReliable: true));
        Assert.True(connectedPair.Client.Telemetry.PacketsDroppedOut > 0);

        Assert.Equal(SendResult.Sent, connectedPair.Client.Send(connectedPair.Connection, CreatePayload(7), isReliable: true));
        Assert.True(connectedPair.PumpUntilReceived(1), "The reliable payload after the dropped one never arrived.");

        TestHarness.PumpFor(durationMs: 200, connectedPair.Server, connectedPair.Client);
        Assert.Single(connectedPair.ReceivedPayloads);
        Assert.Equal(CreatePayload(7), connectedPair.ReceivedPayloads[0]);
    }

    /// <summary>
    /// With data session tokens on at both ends, payloads the throttle queues on both channels, whole and split, arrive intact, each
    /// datagram carrying the token, which the server refuses any data packet without.
    /// </summary>
    [Fact]
    public void Queued_Payloads_With_Data_Session_Tokens_Arrive_Intact()
    {
        const int SendCount = 24;

        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = NineDatagramRelease;
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = 20;
        }, isDataSessionTokenEnabled: true);

        int queuedCount = 0;

        for (int i = 0; i < SendCount; i++)
        {
            if (connectedPair.Client.Send(connectedPair.Connection, CreatePayload(i), isReliable: i % 2 == 1) is SendResult.Queued)
                queuedCount++;
        }

        Assert.True(queuedCount > 0, "nothing was queued, so the test proved nothing");
        Assert.True(connectedPair.PumpUntilReceived(SendCount), $"The server received [{connectedPair.ReceivedPayloads.Count}] of [{SendCount}].");
        connectedPair.AssertReceivedEachOnce(SendCount);

        byte[] unreliablePayload = CreateLargePayload(length: 40_000, seed: 5);
        byte[] reliablePayload = CreateLargePayload(length: 40_000, seed: 6);

        Assert.Equal(SendResult.Queued, connectedPair.Client.Send(connectedPair.Connection, unreliablePayload, isReliable: false));
        Assert.Equal(SendResult.Queued, connectedPair.Client.Send(connectedPair.Connection, reliablePayload, isReliable: true));

        Assert.True(connectedPair.PumpUntilReceived(SendCount + 2), $"The server received [{connectedPair.ReceivedPayloads.Count}] of [{SendCount + 2}].");
        Assert.Contains(connectedPair.ReceivedPayloads, receivedPayload => receivedPayload.AsSpan().SequenceEqual(unreliablePayload));
        Assert.Contains(connectedPair.ReceivedPayloads, receivedPayload => receivedPayload.AsSpan().SequenceEqual(reliablePayload));
    }

    /// <summary>
    /// With data session tokens on at both ends, under <see cref="SendQueueFullAction.DropNewest"/>, a split is admitted only when the queue
    /// holds every segment with its token: ten full reliable segments are 12,000 bytes at the default transmission unit, which a queue of
    /// exactly that takes and one a byte smaller drops whole.
    /// </summary>
    /// <param name="queueSlack">The queue limit's difference from the split's length on the wire.</param>
    /// <param name="expectedSendResult">What the send of the split reports.</param>
    [Theory]
    [InlineData(0, SendResult.Queued)]
    [InlineData(-1, SendResult.Dropped)]
    public void Split_With_Data_Session_Tokens_Is_Admitted_Only_When_The_Queue_Holds_Every_Segment_With_Its_Token(int queueSlack, SendResult expectedSendResult)
    {
        const int SegmentCount = 10;

        int transmissionUnit = (int)new SynapseConfig().MaximumTransmissionUnit;
        int segmentPayloadLength = transmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented) - PacketHeader.SessionTokenSize;

        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = OneDatagramRelease;
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = 20;
            sendThrottleConfig.MaximumQueuedBytes = (uint)(SegmentCount * transmissionUnit + queueSlack);
            sendThrottleConfig.QueueFullAction = SendQueueFullAction.DropNewest;
        }, isDataSessionTokenEnabled: true);

        byte[] payload = CreateLargePayload(length: SegmentCount * segmentPayloadLength, seed: 7);

        Assert.Equal(expectedSendResult, connectedPair.Client.Send(connectedPair.Connection, payload, isReliable: true));

        if (expectedSendResult is SendResult.Dropped)
        {
            Assert.Equal(SegmentCount, connectedPair.Client.Telemetry.PacketsDroppedOut);
        }
        else
        {
            Assert.True(connectedPair.PumpUntilReceived(1), "The admitted split never arrived.");
            Assert.Equal(payload, connectedPair.ReceivedPayloads[0]);
        }
    }

    /// <summary>
    /// Once the pools are warm, queueing and releasing a burst allocates nothing on the thread that sends and polls.
    /// </summary>
    [Fact]
    public void Warm_Throttled_Burst_Allocates_Nothing()
    {
        const int SendCount = 400;

        using ConnectedPair connectedPair = ConnectedPair.Create(sendThrottleConfig =>
        {
            sendThrottleConfig.MaximumBytesPerRelease = 64 * 1024;
            sendThrottleConfig.MinimumReleaseIntervalMilliseconds = 5;
        });

        ArraySegment<byte> payload = new(CreatePayload(0));
        SynapseManager[] engines = [connectedPair.Server, connectedPair.Client];

        // The first burst grows the queue and fills the pools; the second is measured.
        SendBurst(connectedPair, payload, SendCount, engines);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        SendBurst(connectedPair, payload, SendCount, engines);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(2 * SendCount, connectedPair.ReceivedCount);
        Assert.Equal(0, allocatedBytes);
    }

    /// <summary>
    /// Sends a burst and polls both engines until the server holds every payload, allocating nothing of its own.
    /// </summary>
    /// <param name="connectedPair">The connected engines.</param>
    /// <param name="payload">The payload to send.</param>
    /// <param name="sendCount">How many times to send it.</param>
    /// <param name="engines">Both engines, polled in turn.</param>
    private static void SendBurst(ConnectedPair connectedPair, ArraySegment<byte> payload, int sendCount, SynapseManager[] engines)
    {
        int expectedCount = connectedPair.ReceivedCount + sendCount;

        for (int i = 0; i < sendCount; i++)
            connectedPair.Client.Send(connectedPair.Connection, payload, isReliable: false);

        long deadline = Environment.TickCount64 + DeliveryTimeoutMilliseconds;

        while (connectedPair.ReceivedCount < expectedCount && Environment.TickCount64 < deadline)
        {
            for (int i = 0; i < engines.Length; i++)
                engines[i].Poll();

            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Creates a test payload whose bytes all hold its index, so each one is recognisable on arrival.
    /// </summary>
    /// <param name="index">The payload's index.</param>
    /// <returns>The payload.</returns>
    private static byte[] CreatePayload(int index)
    {
        byte[] payload = new byte[PayloadLength];
        Array.Fill(payload, (byte)index);

        return payload;
    }

    /// <summary>
    /// Creates a payload wider than one datagram, filled from a seeded generator.
    /// </summary>
    /// <param name="length">The payload's length.</param>
    /// <param name="seed">The generator's seed.</param>
    /// <returns>The payload.</returns>
    private static byte[] CreateLargePayload(int length, int seed)
    {
        byte[] payload = new byte[length];
        new Random(seed).NextBytes(payload);

        return payload;
    }

    /// <summary>
    /// A server and a client connected over UDP loopback, the client's sends throttled as a test asks.
    /// </summary>
    private sealed class ConnectedPair : IDisposable
    {
        /// <summary>
        /// The server, whose received payloads are counted and recorded.
        /// </summary>
        public SynapseManager Server { get; }
        /// <summary>
        /// The client, whose sends are throttled.
        /// </summary>
        public SynapseManager Client { get; }
        /// <summary>
        /// The client's connection to the server.
        /// </summary>
        public SynapseConnection Connection { get; }
        /// <summary>
        /// The client's endpoint as the server sees it.
        /// </summary>
        public IPEndPoint ClientEndPoint { get; }
        /// <summary>
        /// The first <see cref="MaximumRecordedPayloads"/> payloads the server received, in arrival order.
        /// </summary>
        public List<byte[]> ReceivedPayloads { get; } = [];
        /// <summary>
        /// How many payloads the server received, counted without allocating.
        /// </summary>
        public int ReceivedCount { get; private set; }
        /// <summary>
        /// The most payloads copied into <see cref="ReceivedPayloads"/>. Later ones are only counted, so a measured burst past this
        /// allocates nothing in the handler.
        /// </summary>
        private const int MaximumRecordedPayloads = 64;

        /// <summary>
        /// Wraps an already connected pair and starts counting what the server receives.
        /// </summary>
        /// <param name="server">The server.</param>
        /// <param name="client">The client.</param>
        /// <param name="connection">The client's connection.</param>
        /// <param name="clientEndPoint">The client's endpoint.</param>
        private ConnectedPair(SynapseManager server, SynapseManager client, SynapseConnection connection, IPEndPoint clientEndPoint)
        {
            Server = server;
            Client = client;
            Connection = connection;
            ClientEndPoint = clientEndPoint;
            Server.PacketReceived += OnServerPacketReceived;
        }

        /// <summary>
        /// Polls both engines until the server has received <paramref name="count"/> payloads.
        /// </summary>
        /// <param name="count">The payloads to wait for.</param>
        /// <returns>True when they arrived in time.</returns>
        public bool PumpUntilReceived(int count) => TestHarness.PumpUntil(() => ReceivedCount >= count, DeliveryTimeoutMilliseconds, Server, Client);

        /// <summary>
        /// Asserts the server received exactly payloads 0 to <paramref name="count"/> minus one, each once.
        /// </summary>
        /// <param name="count">The payloads expected.</param>
        public void AssertReceivedEachOnce(int count)
        {
            Assert.Equal(count, ReceivedPayloads.Count);

            HashSet<byte> indices = [];

            foreach (byte[] receivedPayload in ReceivedPayloads)
            {
                Assert.Equal(CreatePayload(receivedPayload[0]), receivedPayload);
                Assert.True(indices.Add(receivedPayload[0]), $"Payload [{receivedPayload[0]}] arrived twice.");
            }

            for (int i = 0; i < count; i++)
                Assert.Contains((byte)i, indices);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }

        /// <summary>
        /// Starts a server and a client over UDP loopback and connects them.
        /// </summary>
        /// <param name="configureThrottle">Configures and enables the client's throttle, or null to leave it at its defaults.</param>
        /// <param name="isDataSessionTokenEnabled">True to turn <see cref="SecurityConfig.DataSessionTokenEnabled"/> on at both ends.</param>
        /// <returns>The connected pair.</returns>
        public static ConnectedPair Create(Action<SendThrottleConfig>? configureThrottle, bool isDataSessionTokenEnabled = false)
        {
            int port = TestHarness.GetFreePort();
            SynapseManager server = new(TestHarness.ServerConfig(port, synapseConfig => synapseConfig.Security.DataSessionTokenEnabled = isDataSessionTokenEnabled));
            SynapseManager client = new(TestHarness.ClientConfig(synapseConfig =>
            {
                synapseConfig.Security.DataSessionTokenEnabled = isDataSessionTokenEnabled;

                if (configureThrottle is null)
                    return;

                synapseConfig.SendThrottle.Enabled = true;
                configureThrottle(synapseConfig.SendThrottle);
            }));

            server.Start();
            client.Start();

            IPEndPoint? clientEndPoint = null;
            server.ConnectionEstablished += connectionEventArgs => clientEndPoint = connectionEventArgs.Connection.RemoteEndPoint;
            SynapseConnection connection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));

            Assert.True(TestHarness.PumpUntil(() => connection.State == ConnectionState.Connected && clientEndPoint is not null, DeliveryTimeoutMilliseconds, server, client), "The client never connected.");

            return new(server, client, connection, clientEndPoint!);
        }

        /// <summary>
        /// Counts each payload the server receives, and copies it while fewer than <see cref="MaximumRecordedPayloads"/> are held.
        /// </summary>
        /// <param name="packetReceivedEventArgs">The received payload.</param>
        private void OnServerPacketReceived(PacketReceivedEventArgs packetReceivedEventArgs)
        {
            ReceivedCount++;

            if (ReceivedPayloads.Count < MaximumRecordedPayloads)
                ReceivedPayloads.Add(packetReceivedEventArgs.Payload.ToArray());
        }
    }
}
