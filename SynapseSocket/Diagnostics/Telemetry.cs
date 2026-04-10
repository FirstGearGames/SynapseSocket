using System.Threading;

namespace SynapseSocket.Diagnostics;

/// <summary>
/// Optional high-performance counters.
/// Uses <see cref="Interlocked"/> to avoid locks on hot paths.
/// All counters are 64-bit.
/// </summary>
public sealed class Telemetry
{
    /// <summary>
    /// True when telemetry is enabled.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Total bytes received.
    /// </summary>
    public long BytesIn => Interlocked.Read(ref _bytesIn);
    private long _bytesIn;

    /// <summary>
    /// Total bytes sent.
    /// </summary>
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    private long _bytesOut;

    /// <summary>
    /// Total packets received.
    /// </summary>
    public long PacketsIn => Interlocked.Read(ref _packetsIn);
    private long _packetsIn;

    /// <summary>
    /// Total packets sent.
    /// </summary>
    public long PacketsOut => Interlocked.Read(ref _packetsOut);
    private long _packetsOut;

    /// <summary>
    /// Total incoming packets dropped by filtering.
    /// </summary>
    public long PacketsDroppedIn => Interlocked.Read(ref _packetsDroppedIn);
    private long _packetsDroppedIn;

    /// <summary>
    /// Total outgoing packets dropped (e.g., simulated loss).
    /// </summary>
    public long PacketsDroppedOut => Interlocked.Read(ref _packetsDroppedOut);
    private long _packetsDroppedOut;

    /// <summary>
    /// Total reliable retransmissions.
    /// </summary>
    public long ReliableResends => Interlocked.Read(ref _reliableResends);
    private long _reliableResends;

    /// <summary>
    /// Estimated lost packets (reliable retry exhaustion).
    /// </summary>
    public long PacketsLost => Interlocked.Read(ref _packetsLost);
    private long _packetsLost;

    /// <summary>
    /// Creates a telemetry instance.
    /// </summary>
    public Telemetry(bool enabled)
    {
        Enabled = enabled;
    }

    internal void OnReceived(int bytes)
    {
        if (!Enabled) return;
        Interlocked.Add(ref _bytesIn, bytes);
        Interlocked.Increment(ref _packetsIn);
    }

    internal void OnSent(int bytes)
    {
        if (!Enabled) return;
        Interlocked.Add(ref _bytesOut, bytes);
        Interlocked.Increment(ref _packetsOut);
    }

    internal void OnDroppedIn()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _packetsDroppedIn);
    }

    internal void OnDroppedOut()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _packetsDroppedOut);
    }

    internal void OnReliableResend()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _reliableResends);
    }

    internal void OnLost()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _packetsLost);
    }
}
