namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for the virtual latency simulator.
/// </summary>
public sealed class LatencySimulatorConfig
{
    /// <summary>
    /// Enables the simulator.
    /// When false all other settings are ignored and packets are sent immediately.
    /// </summary>
    public bool Enabled;

    /// <summary>
    /// Base latency added to every outbound packet, in milliseconds.
    /// </summary>
    public uint BaseLatencyMilliseconds = 0;

    /// <summary>
    /// Random jitter added on top of <see cref="BaseLatencyMilliseconds"/>, in milliseconds.
    /// A random value in [0, JitterMilliseconds) is chosen per packet.
    /// </summary>
    public uint JitterMilliseconds = 0;

    /// <summary>
    /// Probability in [0, 1] that a given outbound packet is dropped.
    /// </summary>
    public double PacketLossChance = 0.0;

    /// <summary>
    /// Probability in [0, 1] that a packet receives an extra random reorder delay.
    /// When triggered, a random value in [0, <see cref="OutOfOrderExtraDelayMilliseconds"/>) is added
    /// on top of <see cref="BaseLatencyMilliseconds"/> and jitter.
    /// </summary>
    public double ReorderChance = 0.0;

    /// <summary>
    /// Maximum extra delay, in milliseconds, added to packets selected for reordering.
    /// A random value in [0, OutOfOrderExtraDelayMilliseconds) is chosen per affected packet.
    /// Only used when <see cref="ReorderChance"/> is greater than zero.
    /// </summary>
    public uint OutOfOrderExtraDelayMilliseconds = 100;

    /// <summary>
    /// The seed for the random choices behind loss, jitter and reordering, so a run can be repeated with the same choices for the
    /// same packets. <see cref="UnsetSeed"/> (0, the default) leaves them unseeded, different on every run.
    /// </summary>
    /// <remarks>
    /// Read once, when the engine is constructed. The same seed repeats the choices, but a networked run can still differ, since how
    /// many packets are sent and when depends on timing.
    /// </remarks>
    public int Seed = UnsetSeed;

    /// <summary>
    /// Sentinel value: pass as <see cref="Seed"/> to leave the simulator unseeded.
    /// </summary>
    public const int UnsetSeed = 0;
}
