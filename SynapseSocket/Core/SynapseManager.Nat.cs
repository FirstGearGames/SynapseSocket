using System;
using System.Collections.Generic;
using System.Net;
using SynapseSocket.Connections;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Core;

/// <summary>
/// NAT traversal support for <see cref="SynapseManager"/>.
/// <para>
/// <b>FullCone mode:</b> both peers already know each other's external endpoint — typically
/// because they discovered each other through an out-of-band rendezvous service (see the
/// <c>SynapseBeacon</c> sister project). <see cref="Connect"/> first waits
/// <see cref="FullConeNatConfig.DirectAttemptMilliseconds"/> for a direct connection; if the
/// connection is still pending it falls back to timed probe bursts that open NAT mappings on
/// both sides simultaneously. The state machine is advanced by <see cref="Poll"/> — there is no
/// background task.
/// </para>
/// <para>
/// Rendezvous/relay signaling itself is intentionally NOT implemented in SynapseSocket —
/// external protocols should piggyback on the engine's UDP socket using the
/// <see cref="SynapseManager.SendRaw"/> and <see cref="SynapseManager.UnknownPacketReceived"/>
/// extension API so the NAT mapping opened by talking to the rendezvous service is the same
/// mapping used for peer-to-peer traffic.
/// </para>
/// </summary>
public sealed partial class SynapseManager
{
    /// <summary>
    /// In-flight FullCone hole-punch attempts, advanced once per <see cref="Poll"/>.
    /// </summary>
    private readonly List<NatPunch> _natPunches = [];

    /// <summary>
    /// State for one pending FullCone hole-punch: the connection being established, the target endpoint,
    /// when the next probe burst is due, how many bursts have been sent, and whether the initial direct-attempt
    /// grace period has elapsed.
    /// </summary>
    private struct NatPunch
    {
        public SynapseConnection Connection;
        public IPEndPoint EndPoint;
        public long NextActionTicks;
        public uint AttemptsDone;
        public bool GraceElapsed;
    }

    /// <summary>
    /// Registers a FullCone hole-punch for a freshly-initiated connection. The first probe burst fires after the
    /// configured direct-attempt grace period, then repeats at the configured interval until the connection
    /// establishes or the attempt cap is reached.
    /// </summary>
    private void RegisterNatPunch(SynapseConnection synapseConnection, IPEndPoint endPoint)
    {
        long nowTicks = DateTime.UtcNow.Ticks;

        _natPunches.Add(new NatPunch
        {
            Connection = synapseConnection,
            EndPoint = endPoint,
            NextActionTicks = nowTicks + (long)Config.NatTraversal.FullCone.DirectAttemptMilliseconds * TimeSpan.TicksPerMillisecond,
            AttemptsDone = 0,
            GraceElapsed = false
        });
    }

    /// <summary>
    /// Advances every pending hole-punch: sends due probe bursts, retires connections that have established or
    /// otherwise left the pending state, and raises <see cref="ConnectionFailed"/> with
    /// <see cref="ConnectionRejectedReason.NatTraversalFailed"/> for attempts that exhaust their retries.
    /// </summary>
    /// <param name="nowTicks">Current time in <see cref="DateTime.Ticks"/>.</param>
    private void AdvanceNatPunches(long nowTicks)
    {
        if (_natPunches.Count == 0 || _transmissionEngine is null)
            return;

        for (int i = _natPunches.Count - 1; i >= 0; i--)
        {
            NatPunch punch = _natPunches[i];

            // Retire once the connection leaves the pending state (established, disconnected, or removed).
            if (punch.Connection.State != ConnectionState.Pending)
            {
                _natPunches.RemoveAt(i);
                continue;
            }

            if (nowTicks < punch.NextActionTicks)
                continue;

            // The direct-handshake grace has elapsed; begin probing on this tick.
            punch.GraceElapsed = true;

            if (punch.AttemptsDone >= Config.NatTraversal.MaximumAttempts)
            {
                RaiseConnectionFailed(punch.EndPoint, ConnectionRejectedReason.NatTraversalFailed, null);
                _natPunches.RemoveAt(i);
                continue;
            }

            try
            {
                for (uint probe = 0; probe < Config.NatTraversal.ProbeCount; probe++)
                    _transmissionEngine.SendNatProbe(punch.EndPoint);

                _transmissionEngine.SendHandshake(punch.EndPoint);
            }
            catch (Exception unexpectedException)
            {
                UnhandledException?.Invoke(unexpectedException);
            }

            punch.AttemptsDone++;
            punch.NextActionTicks = nowTicks + (long)Config.NatTraversal.IntervalMilliseconds * TimeSpan.TicksPerMillisecond;
            _natPunches[i] = punch;
        }
    }
}
