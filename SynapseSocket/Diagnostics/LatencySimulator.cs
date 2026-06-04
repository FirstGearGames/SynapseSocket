using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Packets;

namespace SynapseSocket.Diagnostics;

/// <summary>
/// Optional middleware for testing network degradation.
/// Adds latency, jitter, out-of-order delivery, and packet loss to outgoing packets.
/// <para>
/// In the synchronous/poll-driven engine this never blocks the sender: a packet that should be
/// delayed is copied into a private buffer and parked on an internal queue, then released by
/// <see cref="Flush"/> when its due time elapses. <see cref="Flush"/> is driven from the engine's
/// poll, so the simulator runs entirely on the engine's single thread — no timers, no Tasks.
/// </para>
/// </summary>
public sealed class LatencySimulator
{
    /// <summary>
    /// True if the simulator is enabled.
    /// </summary>
    public bool IsEnabled => _config.Enabled;

    /// <summary>
    /// Configuration driving all simulator behavior.
    /// </summary>
    private readonly LatencySimulatorConfig _config;

    /// <summary>
    /// Packets parked for delayed release, in insertion order. Drained by <see cref="Flush"/> on the
    /// engine's poll thread. The engine is single-threaded, so no synchronization is required.
    /// </summary>
    private readonly List<Deferred> _deferred = [];

    /// <summary>
    /// A single outbound packet awaiting its release time. <see cref="Buffer"/> is a private rental
    /// (the caller's backing array may be recycled before the delay elapses), returned to the pool
    /// once the packet is released.
    /// </summary>
    private struct Deferred
    {
        public byte[] Buffer;
        public int Count;
        public IPEndPoint Target;
        public long DueTicks;
    }

    /// <summary>
    /// Random source for loss/jitter/reorder rolls. The engine drives the simulator from one thread,
    /// so a single instance is sufficient.
    /// </summary>
    private readonly Random _random = new();

    /// <summary>
    /// Creates a simulator from the provided configuration.
    /// </summary>
    /// <param name="config">The latency simulator configuration to apply.</param>
    public LatencySimulator(LatencySimulatorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Routes an outbound packet through the simulator. Connection-setup packets bypass the simulator
    /// and are sent immediately; in-session packets may be dropped, sent immediately, or parked for
    /// later release by <see cref="Flush"/>.
    /// </summary>
    /// <param name="segment">The packet data to send, including offset and length.</param>
    /// <param name="target">The remote endpoint the packet is addressed to.</param>
    /// <param name="nowTicks">Current time in <see cref="DateTime.Ticks"/>, used to compute the release time.</param>
    /// <param name="sender">The underlying send function, invoked now for non-delayed packets.</param>
    public void Process(ArraySegment<byte> segment, IPEndPoint target, long nowTicks, Action<ArraySegment<byte>, IPEndPoint> sender)
    {
        // Handshake and NAT setup packets bypass the sim — they represent connection
        // establishment, not in-session traffic, so loss/delay here would prevent
        // connection from ever succeeding rather than simulating realistic degradation.
        if (segment.Count > 0 && segment.Array != null)
        {
            PacketType type = (PacketType)segment.Array[segment.Offset];
            if (type is PacketType.Handshake or PacketType.NatProbe or PacketType.NatChallenge)
            {
                sender(segment, target);
                return;
            }
        }

        double lossRoll = _random.NextDouble();
        int jitter = _config.JitterMilliseconds > 0 ? _random.Next(0, (int)_config.JitterMilliseconds) : 0;
        int delayMilliseconds = (int)_config.BaseLatencyMilliseconds + jitter;

        if (_config.ReorderChance > 0 && _random.NextDouble() < _config.ReorderChance)
            delayMilliseconds += _config.OutOfOrderExtraDelayMilliseconds > 0 ? _random.Next(0, (int)_config.OutOfOrderExtraDelayMilliseconds) : 0;

        if (lossRoll < _config.PacketLossChance)
            return;

        if (delayMilliseconds <= 0)
        {
            sender(segment, target);
            return;
        }

        // Copy into a private buffer: the caller's backing array may be returned to the pool
        // (e.g. on reliable-packet ACK) before the delay elapses, which would corrupt in-flight data.
        byte[] owned = ArrayPool<byte>.Shared.Rent(segment.Count);
        segment.AsSpan().CopyTo(owned);

        _deferred.Add(new Deferred
        {
            Buffer = owned,
            Count = segment.Count,
            Target = target,
            DueTicks = nowTicks + delayMilliseconds * TimeSpan.TicksPerMillisecond
        });
    }

    /// <summary>
    /// Releases every parked packet whose due time has elapsed, sending it via <paramref name="sender"/>
    /// and returning its private buffer to the pool. Called once per engine poll.
    /// </summary>
    /// <param name="nowTicks">Current time in <see cref="DateTime.Ticks"/>.</param>
    /// <param name="sender">The underlying send function.</param>
    public void Flush(long nowTicks, Action<ArraySegment<byte>, IPEndPoint> sender)
    {
        int writeIndex = 0;

        for (int readIndex = 0; readIndex < _deferred.Count; readIndex++)
        {
            Deferred deferred = _deferred[readIndex];

            if (deferred.DueTicks <= nowTicks)
            {
                sender(new ArraySegment<byte>(deferred.Buffer, 0, deferred.Count), deferred.Target);
                ArrayPool<byte>.Shared.Return(deferred.Buffer);
            }
            else
            {
                // Keep the not-yet-due entry, compacting toward the front to preserve insertion order.
                _deferred[writeIndex++] = deferred;
            }
        }

        if (writeIndex < _deferred.Count)
            _deferred.RemoveRange(writeIndex, _deferred.Count - writeIndex);
    }

    /// <summary>
    /// Returns all parked buffers to the pool and clears the queue. Called on engine shutdown so no
    /// rented buffers are leaked when packets are still awaiting release.
    /// </summary>
    public void Clear()
    {
        foreach (Deferred deferred in _deferred)
            ArrayPool<byte>.Shared.Return(deferred.Buffer);

        _deferred.Clear();
    }
}
