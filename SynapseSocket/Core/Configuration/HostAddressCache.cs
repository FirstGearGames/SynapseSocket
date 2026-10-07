using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Addresses resolved for host names, each kept until its own expiry, so a connect to a recently resolved name skips DNS.
/// </summary>
/// <remarks>Not thread-safe: used only on the thread that polls and connects the engines it is handed to.</remarks>
public sealed class HostAddressCache
{
    /// <summary>
    /// Each cached host name, compared without case, with its address and the <see cref="Clock"/> tick after which it is stale.
    /// </summary>
    private readonly Dictionary<string, (IPAddress Address, long ExpiryTicks)> _entriesByHostName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Removes every cached address.
    /// </summary>
    public void Clear() => _entriesByHostName.Clear();

    /// <summary>
    /// Returns the address cached for <paramref name="hostName"/> while it has not expired.
    /// </summary>
    /// <param name="hostName">The host name to look up.</param>
    /// <param name="nowTicks">The current <see cref="Clock"/> tick.</param>
    /// <param name="address">The cached address, or null when there is none or it has expired.</param>
    /// <returns>True when an unexpired address was found.</returns>
    internal bool TryGet(string hostName, long nowTicks, [NotNullWhen(true)] out IPAddress? address)
    {
        if (_entriesByHostName.TryGetValue(hostName, out (IPAddress Address, long ExpiryTicks) entry) && nowTicks < entry.ExpiryTicks)
        {
            address = entry.Address;

            return true;
        }

        address = null;

        return false;
    }

    /// <summary>
    /// Caches <paramref name="address"/> for <paramref name="hostName"/> until <paramref name="expiryTicks"/>.
    /// </summary>
    /// <param name="hostName">The host name that was resolved.</param>
    /// <param name="address">The address it resolved to.</param>
    /// <param name="expiryTicks">The <see cref="Clock"/> tick after which the address is stale.</param>
    internal void Set(string hostName, IPAddress address, long expiryTicks) => _entriesByHostName[hostName] = (address, expiryTicks);
}
