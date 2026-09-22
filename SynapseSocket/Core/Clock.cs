using System;
using System.Diagnostics;

namespace SynapseSocket.Core;

/// <summary>
/// Monotonic tick source for every interval the engine measures: timeouts, keep-alive scheduling, reliable
/// retransmission, segment-assembly expiry, rate-limit windows, blacklist expiry and NAT token buckets.
/// </summary>
/// <remarks>
/// <para>
/// These were all read from <see cref="DateTime.UtcNow"/>, which is wall-clock and can jump. An NTP correction or a
/// user changing the system clock moves it in either direction: forward past the connection timeout disconnects
/// every peer at once, and backward makes elapsed time negative so timeouts stop firing until the clock catches up.
/// Clock steps after sleep/resume are routine on mobile and console targets, so this is ordinary operation rather
/// than an edge case.
/// </para>
/// <para>
/// Values are in the same units as <see cref="DateTime.Ticks"/> (10,000,000 per second), so every existing
/// <see cref="TimeSpan"/>-derived interval keeps working unchanged. Only differences between two readings are
/// meaningful; the origin is arbitrary.
/// </para>
/// </remarks>
internal static class Clock
{
    /// <summary>
    /// Scale converting <see cref="Stopwatch"/> timestamps to <see cref="TimeSpan"/> ticks. Exactly 1 on platforms
    /// whose stopwatch already runs at 10 MHz, which is the common case.
    /// </summary>
    private static readonly double TickScale = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

    /// <summary>
    /// Current monotonic tick count. Never decreases and is unaffected by wall-clock adjustments.
    /// </summary>
    /// <remarks>
    /// Deliberately unconditional. A fast path skipping the multiply where the stopwatch already runs at 10 MHz
    /// measured 2.5 ns cheaper per call. Against a full receive path of ~1420 ns per datagram, on a call that now
    /// happens once per poll rather than once per datagram, that saving does not justify a second static field
    /// and a branch.
    /// </remarks>
    internal static long Ticks => (long)(Stopwatch.GetTimestamp() * TickScale);
}
