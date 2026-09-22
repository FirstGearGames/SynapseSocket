using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SynapseSocket.Transport;

/// <summary>
/// Direct <c>recvfrom</c>/<c>sendto</c> bindings, used where the managed socket API cannot receive from an
/// unspecified sender without allocating.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Socket.ReceiveFrom(byte[], int, int, SocketFlags, ref EndPoint)"/> serialises a
/// <see cref="SocketAddress"/> and then materialises a fresh <see cref="IPEndPoint"/> and <see cref="IPAddress"/>
/// for the sender on every datagram. Measured on Unity's Mono 6.13 that is <b>6 managed allocations per receive</b>
/// and 2 per send. .NET 8 has <c>ReceiveFrom(Span, SocketFlags, SocketAddress)</c>, which fills a caller-owned
/// address and allocates nothing; netstandard2.1 (every Unity target) has no equivalent.
/// </para>
/// <para>
/// Calling the syscall directly writes the sender into a caller-owned <c>sockaddr</c> buffer, so the receive costs
/// nothing and the caller decides whether it needs a managed endpoint at all. An established peer never does: it is
/// resolved by hashing the raw address bytes. Measured on the same runtime this path is <b>0.009 allocations per
/// receive</b>, i.e. nothing per datagram.
/// </para>
/// <para>
/// Blittable <see cref="byte"/> arrays are pinned by the interop marshaller rather than copied, so the calls
/// themselves allocate nothing.
/// </para>
/// </remarks>
internal static unsafe class NativeSocket
{
    /// <summary>
    /// Size of a <c>sockaddr_in6</c>, the larger of the two address shapes handled here.
    /// </summary>
    internal const int SockAddrSize = 28;

    /// <summary>
    /// Offset of the port within both <c>sockaddr_in</c> and <c>sockaddr_in6</c>. Network byte order.
    /// </summary>
    private const int PortOffset = 2;

    /// <summary>
    /// Offset of the address bytes within <c>sockaddr_in</c>.
    /// </summary>
    private const int IPv4AddressOffset = 4;

    /// <summary>
    /// Offset of the address bytes within <c>sockaddr_in6</c>.
    /// </summary>
    private const int IPv6AddressOffset = 8;

    /// <summary>
    /// <c>AF_INET</c> as it appears in the family field on Windows and Linux.
    /// </summary>
    private const byte AddressFamilyInterNetwork = 2;

    /// <summary>
    /// Size of a <c>sockaddr_in</c>.
    /// </summary>
    private const int SockAddrInSize = 16;

    /// <summary>
    /// <c>AF_INET6</c>, which unlike <c>AF_INET</c> differs per platform: 23 on Windows, 10 on Linux, 30 on macOS.
    /// Only needed when writing an address; when reading, anything that is not <c>AF_INET</c> is treated as v6.
    /// </summary>
    private static readonly byte AddressFamilyInterNetworkV6 =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? (byte)23
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? (byte)30
        : (byte)10;

    /// <summary>
    /// True on Windows, where the Winsock export is used.
    /// </summary>
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Whether a native receive is available on this platform. False leaves callers on the managed path.
    /// </summary>
    internal static bool IsSupported { get; } = ProbeSupport();

    [DllImport("ws2_32.dll", EntryPoint = "recvfrom", SetLastError = true)]
    private static extern int RecvFromWindows(IntPtr socket, byte[] buffer, int length, int flags, byte[] from, ref int fromLength);

    [DllImport("libc", EntryPoint = "recvfrom", SetLastError = true)]
    private static extern IntPtr RecvFromUnix(IntPtr socket, byte[] buffer, IntPtr length, int flags, byte[] from, ref uint fromLength);

    [DllImport("ws2_32.dll", EntryPoint = "sendto", SetLastError = true)]
    private static extern int SendToWindows(IntPtr socket, byte* buffer, int length, int flags, byte* to, int toLength);

    [DllImport("libc", EntryPoint = "sendto", SetLastError = true)]
    private static extern IntPtr SendToUnix(IntPtr socket, byte* buffer, IntPtr length, int flags, byte* to, uint toLength);

    /// <summary>
    /// Receives one datagram, writing the sender's raw address into <paramref name="sockAddr"/> without allocating.
    /// </summary>
    /// <param name="socket">Socket to receive on. Must have data pending, so the call cannot block.</param>
    /// <param name="buffer">Destination for the payload.</param>
    /// <param name="length">Maximum bytes to read into <paramref name="buffer"/>.</param>
    /// <param name="sockAddr">Caller-owned buffer of at least <see cref="SockAddrSize"/> bytes.</param>
    /// <param name="sockAddrLength">On return, bytes written into <paramref name="sockAddr"/>.</param>
    /// <returns>Bytes received, or a negative value on error.</returns>
    internal static int ReceiveFrom(Socket socket, byte[] buffer, int length, byte[] sockAddr, out int sockAddrLength)
    {
        IntPtr handle = socket.Handle;

        if (IsWindows)
        {
            int windowsLength = sockAddr.Length;
            int windowsReceived = RecvFromWindows(handle, buffer, length, 0, sockAddr, ref windowsLength);
            sockAddrLength = windowsLength;

            return windowsReceived;
        }

        uint unixLength = (uint)sockAddr.Length;
        IntPtr unixReceived = RecvFromUnix(handle, buffer, (IntPtr)length, 0, sockAddr, ref unixLength);
        sockAddrLength = (int)unixLength;

        return unixReceived.ToInt32();
    }

    /// <summary>
    /// Sends one datagram to a prebuilt raw address, without allocating.
    /// </summary>
    /// <param name="socket">Socket to send on.</param>
    /// <param name="buffer">Payload source.</param>
    /// <param name="offset">Start of the payload within <paramref name="buffer"/>.</param>
    /// <param name="length">Payload length.</param>
    /// <param name="sockAddr">Destination address, as built by <see cref="TryBuildSockAddr"/>.</param>
    /// <param name="sockAddrLength">Valid bytes within <paramref name="sockAddr"/>.</param>
    /// <returns>Bytes sent, or a negative value on error.</returns>
    /// <remarks>
    /// The managed SendTo re-serialises the target endpoint into a fresh <see cref="SocketAddress"/> on every call,
    /// measured at 2 managed allocations per datagram on Unity's Mono. Handing the syscall an address the caller
    /// already holds costs nothing.
    /// </remarks>
    internal static int SendTo(Socket socket, byte[] buffer, int offset, int length, byte[] sockAddr, int sockAddrLength)
    {
        if (length <= 0 || offset < 0 || offset + length > buffer.Length)
            return -1;

        IntPtr handle = socket.Handle;

        fixed (byte* bufferPointer = &buffer[offset])
        fixed (byte* addressPointer = sockAddr)
        {
            if (IsWindows)
                return SendToWindows(handle, bufferPointer, length, 0, addressPointer, sockAddrLength);

            return SendToUnix(handle, bufferPointer, (IntPtr)length, 0, addressPointer, (uint)sockAddrLength).ToInt32();
        }
    }

    /// <summary>
    /// Writes <paramref name="endPoint"/> into <paramref name="destination"/> as a raw <c>sockaddr</c>, so the
    /// address can be built once per peer and reused for every send.
    /// </summary>
    /// <param name="endPoint">Target endpoint.</param>
    /// <param name="destination">Buffer of at least <see cref="SockAddrSize"/> bytes.</param>
    /// <param name="length">On success, bytes written.</param>
    /// <returns>True when the address was written.</returns>
    internal static bool TryBuildSockAddr(IPEndPoint endPoint, byte[] destination, out int length)
    {
        length = 0;

        if (destination.Length < SockAddrSize)
            return false;

        Span<byte> addressBytes = stackalloc byte[16];

        if (!endPoint.Address.TryWriteBytes(addressBytes, out int addressLength))
            return false;

        Array.Clear(destination, 0, destination.Length);

        // Port is network byte order in both address shapes.
        destination[PortOffset] = (byte)((endPoint.Port >> 8) & 0xFF);
        destination[PortOffset + 1] = (byte)(endPoint.Port & 0xFF);

        if (addressLength == 4)
        {
            destination[0] = AddressFamilyInterNetwork;
            addressBytes[..4].CopyTo(destination.AsSpan(IPv4AddressOffset, 4));
            length = SockAddrInSize;

            return true;
        }

        destination[0] = AddressFamilyInterNetworkV6;
        addressBytes[..16].CopyTo(destination.AsSpan(IPv6AddressOffset, 16));
        length = SockAddrSize;

        return true;
    }

    /// <summary>
    /// Produces a 64-bit key identifying the sender, folding the port and address bytes of a raw
    /// <c>sockaddr</c>. Used to resolve an established peer without materialising anything.
    /// </summary>
    /// <param name="sockAddr">Raw address as filled by <see cref="ReceiveFrom"/>.</param>
    /// <param name="sockAddrLength">Valid bytes within <paramref name="sockAddr"/>.</param>
    /// <returns>A key stable for a given address and port.</returns>
    internal static ulong ComputeAddressKey(byte[] sockAddr, int sockAddrLength)
    {
        bool isIPv4 = sockAddr[0] == AddressFamilyInterNetwork;
        int addressOffset = isIPv4 ? IPv4AddressOffset : IPv6AddressOffset;
        int addressLength = isIPv4 ? 4 : 16;

        if (addressOffset + addressLength > sockAddrLength)
            addressLength = Math.Max(0, sockAddrLength - addressOffset);

        return FoldAddressKey(sockAddr[PortOffset], sockAddr[PortOffset + 1], sockAddr.AsSpan(addressOffset, addressLength));
    }

    /// <summary>
    /// Produces the same key as <see cref="ComputeAddressKey(byte[], int)"/> for a managed endpoint, so connections can be
    /// registered under it when they are created.
    /// </summary>
    /// <param name="endPoint">The peer endpoint.</param>
    /// <returns>A key stable for the endpoint's address and port.</returns>
    internal static ulong ComputeAddressKey(IPEndPoint endPoint)
    {
        Span<byte> addressBytes = stackalloc byte[16];
        endPoint.Address.TryWriteBytes(addressBytes, out int addressLength);

        // Port in network byte order, matching the raw sockaddr layout.
        return FoldAddressKey((byte)((endPoint.Port >> 8) & 0xFF), (byte)(endPoint.Port & 0xFF), addressBytes[..addressLength]);
    }

    /// <summary>
    /// Folds a port and address into the 64-bit key both <c>ComputeAddressKey</c> overloads return.
    /// <para>
    /// Both overloads route through here rather than each running their own FNV-1a loop. The raw-sockaddr key and
    /// the managed-endpoint key must be byte-identical, because connections are registered under one and resolved
    /// under the other; sharing the fold makes that agreement structural instead of a property two hand-copied
    /// loops happen to preserve.
    /// </para>
    /// </summary>
    /// <param name="portHighByte">High-order port byte, as it appears in a <c>sockaddr</c>.</param>
    /// <param name="portLowByte">Low-order port byte, as it appears in a <c>sockaddr</c>.</param>
    /// <param name="addressBytes">The address, 4 bytes for IPv4 or 16 for IPv6.</param>
    /// <returns>A key stable for the given address and port.</returns>
    private static ulong FoldAddressKey(byte portHighByte, byte portLowByte, ReadOnlySpan<byte> addressBytes)
    {
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        ulong hash = FnvOffset;

        hash ^= portHighByte;
        hash *= FnvPrime;
        hash ^= portLowByte;
        hash *= FnvPrime;

        for (int i = 0; i < addressBytes.Length; i++)
        {
            hash ^= addressBytes[i];
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// Materialises a managed endpoint from a raw <c>sockaddr</c>. Only used for senders with no connection:
    /// a handshake, a probe, or a violation, never in the steady state.
    /// </summary>
    /// <param name="sockAddr">Raw address as filled by <see cref="ReceiveFrom"/>.</param>
    /// <returns>The sender's endpoint, or null when the address shape is not recognised.</returns>
    internal static IPEndPoint? ToEndPoint(byte[] sockAddr)
    {
        int port = (sockAddr[PortOffset] << 8) | sockAddr[PortOffset + 1];

        if (sockAddr[0] == AddressFamilyInterNetwork)
        {
            Span<byte> ipv4 = stackalloc byte[4];
            sockAddr.AsSpan(IPv4AddressOffset, 4).CopyTo(ipv4);

            return new(new IPAddress(ipv4), port);
        }

        Span<byte> ipv6 = stackalloc byte[16];
        sockAddr.AsSpan(IPv6AddressOffset, 16).CopyTo(ipv6);

        return new(new IPAddress(ipv6), port);
    }

    /// <summary>
    /// Verifies the native entry point actually resolves on this platform, so an unsupported target degrades to the
    /// managed path instead of throwing on the first datagram.
    /// </summary>
    private static bool ProbeSupport()
    {
        try
        {
            using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            byte[] buffer = new byte[1];
            byte[] sockAddr = new byte[SockAddrSize];

            // Nothing is queued, so a non-blocking probe returns an error rather than data. Reaching the call at
            // all is what is being tested: a missing export throws EntryPointNotFoundException or DllNotFoundException.
            probe.Blocking = false;
            ReceiveFrom(probe, buffer, buffer.Length, sockAddr, out _);

            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            // Any other failure means the call itself was reachable.
            return true;
        }
    }
}
