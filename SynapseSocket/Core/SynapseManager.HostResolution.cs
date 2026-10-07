using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Core;

/// <summary>
/// Host name resolution for <see cref="SynapseManager.Connect(string, int?)"/>. The DNS lookup runs on the runtime's own lookup
/// machinery; <see cref="Poll"/> picks the result up, connects to the address it found, and abandons a lookup that outlasts
/// <see cref="Configuration.ConnectionConfig.HostResolveTimeoutSeconds"/>. No thread ever blocks on DNS.
/// </summary>
public sealed partial class SynapseManager
{
    /// <summary>
    /// Host name lookups still running, advanced once per <see cref="Poll"/>.
    /// </summary>
    private readonly List<HostResolution> _hostResolutions = [];

    /// <summary>
    /// Starts resolving <paramref name="hostName"/>, to connect on <paramref name="port"/> once <see cref="Poll"/> sees it resolve.
    /// </summary>
    /// <param name="hostName">The host name to resolve.</param>
    /// <param name="port">The port to connect on.</param>
    /// <remarks>An empty or over-long name is refused by the runtime before any lookup starts, and that refusal is raised and rethrown here.</remarks>
    private void BeginHostResolution(string hostName, int port)
    {
        Task<IPAddress[]> lookup;

        try
        {
            lookup = Dns.GetHostAddressesAsync(hostName);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            RaiseConnectionFailed(null, ConnectionRejectedReason.HostResolutionFailed, $"DNS lookup for '{hostName}' failed: {exception.Message}");

            throw;
        }

        long deadlineTicks = Clock.Ticks + Config.Connection.HostResolveTimeoutSeconds * TimeSpan.TicksPerSecond;
        _hostResolutions.Add(new(hostName, port, lookup, deadlineTicks));
    }

    /// <summary>
    /// Connects every lookup that has resolved, and raises <see cref="ConnectionFailed"/> with
    /// <see cref="ConnectionRejectedReason.HostResolutionFailed"/> for each that failed, found no address, or ran out of time.
    /// </summary>
    /// <param name="nowTicks">The current <see cref="Clock"/> tick.</param>
    private void AdvanceHostResolutions(long nowTicks)
    {
        if (_hostResolutions.Count == 0)
            return;

        for (int i = _hostResolutions.Count - 1; i >= 0; i--)
        {
            HostResolution hostResolution = _hostResolutions[i];
            string? failureMessage;

            if (hostResolution.Lookup.IsCompleted)
                failureMessage = hostResolution.Lookup.IsFaulted ? $"DNS lookup for '{hostResolution.HostName}' failed: {hostResolution.Lookup.Exception!.GetBaseException().Message}"
                    : hostResolution.Lookup.IsCanceled || hostResolution.Lookup.Result.Length == 0 ? $"DNS lookup for '{hostResolution.HostName}' returned no addresses."
                    : null;
            else if (nowTicks > hostResolution.DeadlineTicks)
                failureMessage = $"DNS lookup for '{hostResolution.HostName}' took longer than {Config.Connection.HostResolveTimeoutSeconds} seconds.";
            else
                continue;

            // Removed before any handler or connect runs, either of which may stop the engine or start another lookup.
            _hostResolutions.RemoveAt(i);

            if (failureMessage is not null)
                RaiseConnectionFailed(null, ConnectionRejectedReason.HostResolutionFailed, failureMessage);
            else
                ConnectResolvedHost(hostResolution, nowTicks);

            // A handler or the connect can have stopped the engine, which empties the list.
            if (!_isStarted || _isDisposed)
                return;

            i = Math.Min(i, _hostResolutions.Count);
        }
    }

    /// <summary>
    /// Caches the address a lookup found and connects to it.
    /// </summary>
    /// <param name="hostResolution">The lookup that resolved.</param>
    /// <param name="nowTicks">The current <see cref="Clock"/> tick.</param>
    /// <remarks>The connection reaches the caller through <see cref="ConnectionEstablished"/> once its handshake completes, as any other does.</remarks>
    private void ConnectResolvedHost(HostResolution hostResolution, long nowTicks)
    {
        IPAddress address = ChooseBoundFamilyAddress(hostResolution.Lookup.Result);

        if (Config.Connection.ResolvedAddressCacheSeconds > 0)
            Config.HostAddressCache.Set(hostResolution.HostName, address, nowTicks + Config.Connection.ResolvedAddressCacheSeconds * TimeSpan.TicksPerSecond);

        try
        {
            Connect(new IPEndPoint(address, hostResolution.Port));
        }
        catch (Exception exception)
        {
            // Connect has already raised ConnectionFailed for anything it refuses, so only the unexpected is passed on.
            if (exception is not InvalidOperationException)
                UnhandledException?.Invoke(exception);
        }
    }

    /// <summary>
    /// Picks the first address whose family has a bound socket, falling back to the first address.
    /// </summary>
    /// <param name="addresses">The resolved addresses, at least one.</param>
    /// <returns>The address to connect to.</returns>
    private IPAddress ChooseBoundFamilyAddress(IPAddress[] addresses)
    {
        foreach (IPAddress address in addresses)
        {
            if (IsFamilyBound(address.AddressFamily))
                return address;
        }

        return addresses[0];
    }

    /// <summary>
    /// One host name being resolved for a connect: the name, the port to connect on once it resolves, the lookup itself, and
    /// the tick past which it is abandoned.
    /// </summary>
    private readonly struct HostResolution
    {
        /// <summary>
        /// The host name being resolved.
        /// </summary>
        public readonly string HostName;
        /// <summary>
        /// The port to connect on once the name resolves.
        /// </summary>
        public readonly int Port;
        /// <summary>
        /// The DNS lookup.
        /// </summary>
        public readonly Task<IPAddress[]> Lookup;
        /// <summary>
        /// The <see cref="Clock"/> tick past which the lookup is abandoned.
        /// </summary>
        public readonly long DeadlineTicks;

        /// <summary>
        /// Creates a pending lookup.
        /// </summary>
        /// <param name="hostName">The host name being resolved.</param>
        /// <param name="port">The port to connect on once it resolves.</param>
        /// <param name="lookup">The DNS lookup.</param>
        /// <param name="deadlineTicks">The tick past which the lookup is abandoned.</param>
        public HostResolution(string hostName, int port, Task<IPAddress[]> lookup, long deadlineTicks)
        {
            HostName = hostName;
            Port = port;
            Lookup = lookup;
            DeadlineTicks = deadlineTicks;
        }
    }
}
