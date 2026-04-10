using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Diagnostics;

/// <summary>
/// Optional middleware for testing network degradation.
/// Adds latency, jitter, out-of-order delivery, and packet loss to outgoing packets.
/// </summary>
public sealed class LatencySimulator
{
    /// <summary>
    /// True if the simulator is enabled.
    /// </summary>
    public bool Enabled => _config.Enabled;

    private readonly LatencySimulatorConfig _config;
    private readonly Random _random = new();
    private readonly object _gate = new();

    /// <summary>
    /// Creates a simulator from the provided configuration.
    /// </summary>
    public LatencySimulator(LatencySimulatorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Processes an outbound packet through the simulator.
    /// The sender function is invoked after the computed delay unless the packet is dropped.
    /// </summary>
    public Task ProcessAsync(byte[] buffer, int length, IPEndPoint target, Func<byte[], int, IPEndPoint, Task> sender, CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
            return sender(buffer, length, target);

        double lossRoll;
        int delayMilliseconds;
        lock (_gate)
        {
            lossRoll = _random.NextDouble();
            int jitter = _config.JitterMilliseconds > 0 ? _random.Next(0, (int)_config.JitterMilliseconds) : 0;
            delayMilliseconds = (int)_config.BaseLatencyMilliseconds + jitter;
            if (_random.NextDouble() < _config.ReorderChance)
                delayMilliseconds += _random.Next(0, 50);
        }

        if (lossRoll < _config.PacketLossChance)
            return Task.CompletedTask;

        return DelayedSendAsync(buffer, length, target, sender, delayMilliseconds, cancellationToken);
    }

    private static async Task DelayedSendAsync(byte[] buffer, int length, IPEndPoint target, Func<byte[], int, IPEndPoint, Task> sender, int delayMilliseconds, CancellationToken cancellationToken)
    {
        if (delayMilliseconds > 0)
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        await sender(buffer, length, target).ConfigureAwait(false);
    }
}
