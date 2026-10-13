using System;
using CodeBoost.CodeAnalysis;
using CodeBoost.Performance;

namespace SynapseSocket.Packets;

/// <summary>
/// Abstract base for packet segmentation helpers.
/// Holds shared configuration (MTU and segment limit) set via <see cref="Initialize"/> after renting from a pool.
/// <para>
/// <see cref="PacketSplitter"/> is the send side, splitting outbound payloads into wire-ready segment packets. The receive side is
/// CodeBoost's <see cref="CodeBoost.Networking.PacketReassembler"/>, shared with every other layer that splits payloads.
/// </para>
/// </summary>
public abstract class PacketSegmenter : IPoolResettable
{
    /// <summary>
    /// The maximum number of segments a single message may be split into.
    /// Set by <see cref="Initialize"/>; reset to 0 on return to pool.
    /// </summary>
    [PoolResettableMember]
    public uint MaximumSegments { get; private set; }

    /// <summary>
    /// The effective MTU (max bytes per wire packet) used for segmentation.
    /// Set by <see cref="Initialize"/>; reset to 0 on return to pool.
    /// </summary>
    [PoolResettableMember]
    public uint MaximumTransmissionUnit { get; private set; }

    /// <summary>
    /// Configures the segmenter after renting from the pool.
    /// </summary>
    /// <param name="maximumTransmissionUnit">Maximum wire packet size in bytes; must be greater than <see cref="PacketHeader.MaxHeaderSize"/>.</param>
    /// <param name="maximumSegments">Maximum number of segments a single message may be split into; must be between 1 and 255 inclusive.</param>
    [PoolResettableMethod]
    public void Initialize(uint maximumTransmissionUnit, uint maximumSegments)
    {
        if (maximumTransmissionUnit is 0 or <= PacketHeader.MaxHeaderSize)
            throw new ArgumentOutOfRangeException(nameof(maximumTransmissionUnit));

        if (maximumSegments is 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(maximumSegments));

        MaximumTransmissionUnit = maximumTransmissionUnit;
        MaximumSegments = maximumSegments;
    }

    /// <inheritdoc/>
    public virtual void OnRent() { }

    /// <inheritdoc/>
    public virtual void OnReturn()
    {
        MaximumTransmissionUnit = 0;
        MaximumSegments = 0;
    }
}
