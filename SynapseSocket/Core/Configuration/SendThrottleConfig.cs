namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Configuration for the per-connection send throttle, which spreads a large burst of sends to one peer over several
/// releases so that no single release overflows the peer's socket receive buffer. Disabled by default.
/// </summary>
/// <remarks>
/// <para>
/// While enabled, each connection sends at most <see cref="MaximumBytesPerRelease"/> in one release, and opens a new release
/// no sooner than <see cref="MinimumReleaseIntervalMilliseconds"/> after the last one opened. A send the open release cannot
/// cover waits in the connection's send queue, in order, and the queue is released from <see cref="SynapseManager.Poll"/>.
/// The defaults therefore cap one connection at about 12.5 MB a second, roughly 100 Mbps.
/// </para>
/// <para>
/// The queue holds nothing until a send first has to wait, grows as datagrams are queued, and returns its memory once it
/// drains. It holds at most <see cref="MaximumQueuedBytes"/>, and <see cref="QueueFullAction"/> decides what a send that would
/// pass that does. Handshakes, acknowledgements, keep-alives and disconnects are never throttled.
/// </para>
/// </remarks>
public sealed class SendThrottleConfig
{
    // ReSharper disable FieldCanBeMadeReadOnly.Global

    /// <summary>
    /// When true, sends to each connection are throttled. Defaults to false, which sends every datagram immediately.
    /// </summary>
    public bool Enabled;

    /// <summary>
    /// The most bytes one connection sends in a single release. Defaults to 200 KiB, which fits a stock Linux socket receive
    /// buffer, the smallest a peer is likely to have. The first datagram of a release is always sent, so a value below one
    /// datagram still makes progress.
    /// </summary>
    public uint MaximumBytesPerRelease = 200 * 1024;

    /// <summary>
    /// The least time, in milliseconds, between one release to a connection opening and the next. Defaults to 16.
    /// </summary>
    public uint MinimumReleaseIntervalMilliseconds = 16;

    /// <summary>
    /// The most bytes one connection's send queue may hold. Defaults to 1 MiB. A value below
    /// <see cref="SynapseConfig.MaximumTransmissionUnit"/> is raised to it, so the queue can always hold one datagram.
    /// </summary>
    public uint MaximumQueuedBytes = 1024 * 1024;

    /// <summary>
    /// What a send does when its datagram would take the queue past <see cref="MaximumQueuedBytes"/>. Defaults to
    /// <see cref="SendQueueFullAction.SendOldest"/>.
    /// </summary>
    public SendQueueFullAction QueueFullAction = SendQueueFullAction.SendOldest;
}
