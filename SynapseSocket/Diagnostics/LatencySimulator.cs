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
    public bool IsEnabled => _config.IsEnabled;

    /// <summary>
    /// Configuration driving all simulator behavior.
    /// </summary>
    private readonly LatencySimulatorConfig _config;
    /// <summary>
    /// Pseudo-random number generator used for loss rolls, jitter, and reorder decisions.
    /// </summary>
    private readonly Random _random = new();
    /// <summary>
    /// Guards shared access to <see cref="_random"/>.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Creates a simulator from the provided configuration.
    /// </summary>
    /// <param name="config">The latency simulator configuration to apply.</param>
    public LatencySimulator(LatencySimulatorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Processes an outbound packet through the simulator.
    /// The sender function is invoked after the computed delay unless the packet is dropped.
    /// </summary>
    /// <param name="segment">The packet data to send, including offset and length.</param>
    /// <param name="target">The remote endpoint the packet is addressed to.</param>
    /// <param name="sender">The underlying send function to invoke after any simulated delay.</param>
    /// <param name="cancellationToken">Token used to cancel the delayed send.</param>
    /// <returns>A task that completes when the packet has been passed to <paramref name="sender"/> or dropped.</returns>
    public Task ProcessAsync(ArraySegment<byte> segment, IPEndPoint target, Func<ArraySegment<byte>, IPEndPoint, Task> sender, CancellationToken cancellationToken)
    {
        if (!_config.IsEnabled)
            return sender(segment, target);

        double lossRoll;
        int delayMilliseconds;

        lock (_lock)
        {
            lossRoll = _random.NextDouble();
            int jitter = _config.JitterMilliseconds > 0 ? _random.Next(0, (int)_config.JitterMilliseconds) : 0;
            delayMilliseconds = (int)_config.BaseLatencyMilliseconds + jitter;

            if (_random.NextDouble() < _config.ReorderChance)
                delayMilliseconds += _random.Next(0, 50);
        }

        if (lossRoll < _config.PacketLossChance)
            return Task.CompletedTask;

        return DelayedSendAsync(segment, target, sender, delayMilliseconds, cancellationToken);
    }

    /// <summary>
    /// Waits for the specified delay then invokes the sender function.
    /// </summary>
    /// <param name="segment">The packet data to send, including offset and length.</param>
    /// <param name="target">The remote endpoint the packet is addressed to.</param>
    /// <param name="sender">The underlying send function to invoke after the delay.</param>
    /// <param name="delayMilliseconds">Milliseconds to wait before sending.</param>
    /// <param name="cancellationToken">Token used to cancel the delay.</param>
    /// <returns>A task that completes when the delayed send has finished.</returns>
    private static async Task DelayedSendAsync(ArraySegment<byte> segment, IPEndPoint target, Func<ArraySegment<byte>, IPEndPoint, Task> sender, int delayMilliseconds, CancellationToken cancellationToken)
    {
        if (delayMilliseconds > 0)
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);

        await sender(segment, target).ConfigureAwait(false);
    }
}
