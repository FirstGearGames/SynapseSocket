using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Packets;

namespace SynapseSocket.NatServer;

/// <summary>
/// Lightweight UDP rendezvous server that matches NAT-traversal peers.
/// <para>
/// The host sends <see cref="PacketType.NatRequestSession"/> to obtain a server-assigned session ID.
/// The server responds with <see cref="PacketType.NatSessionCreated"/> containing the ID.
/// The host shares the ID out-of-band with any number of joiners.
/// Each joiner sends <see cref="PacketType.NatRegister"/> with the shared ID; the server responds
/// to that joiner with the host's external endpoint and to the host with the joiner's endpoint,
/// after which hole-punching proceeds directly between each pair.
/// The host closes the session with <see cref="PacketType.NatCloseSession"/> when done accepting.
/// Sessions that receive no heartbeat within <see cref="NatSessionRegistry.SessionTimeoutMilliseconds"/> are evicted automatically.
/// </para>
/// </summary>
public sealed class NatServer : IDisposable
{
    private readonly UdpClient _socket;
    private readonly NatSessionRegistry _registry;
    private readonly Timer _evictionTimer;

    /// <summary>
    /// Immutable single-byte payload for <see cref="PacketType.NatHeartbeatAck"/>. Shared across all sends.
    /// </summary>
    private static readonly byte[] HeartbeatAckPacket = [(byte)PacketType.NatHeartbeatAck];
    /// <summary>
    /// Immutable single-byte payload for <see cref="PacketType.NatSessionFull"/>. Shared across all sends.
    /// </summary>
    private static readonly byte[] SessionFullPacket = [(byte)PacketType.NatSessionFull];
    /// <summary>
    /// Immutable single-byte payload for <see cref="PacketType.NatSessionUnavailable"/>. Shared across all sends.
    /// </summary>
    private static readonly byte[] SessionUnavailablePacket = [(byte)PacketType.NatSessionUnavailable];
    
    /// <summary>
    /// Initialises the server bound to <paramref name="port"/> on all interfaces.
    /// </summary>
    /// <param name="port">UDP port to listen on.</param>
    /// <param name="sessionTimeoutMilliseconds">Milliseconds of heartbeat silence before a session is evicted.</param>
    /// <param name="maxConcurrentSessions">Maximum number of sessions open simultaneously. 0 = unlimited.</param>
    public NatServer(int port, uint sessionTimeoutMilliseconds = 300_000, int maxConcurrentSessions = 0)
    {
        _socket = new(port);
        _registry = new(sessionTimeoutMilliseconds, maxConcurrentSessions);
        _evictionTimer = new(_ => _registry.EvictExpired(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Runs the receive loop until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"NAT rendezvous server listening (session timeout: {_registry.SessionTimeoutMilliseconds} ms)");

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;

            try
            {
                result = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"[NatServer] Socket error: {ex.Message}");
                continue;
            }

            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    /// <summary>
    /// Routes an inbound datagram to the appropriate handler based on its <see cref="PacketType"/> byte.
    /// </summary>
    private void HandleDatagram(byte[] data, IPEndPoint from)
    {
        // Layout: [PacketType (1 byte)] [payload]
        if (data.Length < 1)
            return;

        PacketType type = (PacketType)data[0];

        switch (type)
        {
            case PacketType.NatRequestSession:
                HandleRequestSession(from);
                break;

            case PacketType.NatRegister:
                HandleRegister(data, from);
                break;

            case PacketType.NatHeartbeat:
                HandleHeartbeat(data, from);
                break;

            case PacketType.NatCloseSession:
                HandleCloseSession(data, from);
                break;
        }
    }

    /// <summary>
    /// Creates a new session for the requesting host and sends back a <see cref="PacketType.NatSessionCreated"/> response,
    /// or <see cref="PacketType.NatSessionUnavailable"/> if the concurrent session limit has been reached.
    /// </summary>
    private void HandleRequestSession(IPEndPoint from)
    {
        if (!_registry.TryCreateSession(from, out uint sessionId))
        {
            Console.WriteLine($"[NatServer] Session limit reached — rejecting request from {from}.");
            SendSessionUnavailable(from);
            return;
        }

        Console.WriteLine($"[NatServer] Created session '{sessionId}' for host {from}.");
        SendNatSessionCreated(from, sessionId);
    }

    /// <summary>
    /// Registers a joining peer against an existing session. Sends <see cref="PacketType.NatPeerReady"/> to both
    /// the joiner and the host on success, or <see cref="PacketType.NatSessionFull"/> if the session was not found.
    /// </summary>
    private void HandleRegister(byte[] data, IPEndPoint from)
    {
        if (!NatSessionRegistry.TryParseSessionId(data, offset: 1, out uint sessionId))
            return;

        (bool matched, bool notFound, IPEndPoint? host, IPEndPoint? joiner) = _registry.Register(sessionId, from);

        if (notFound)
        {
            Console.WriteLine($"[NatServer] Session '{sessionId}' not found — rejecting {from}.");
            SendSessionFull(from);
            return;
        }

        if (!matched)
            return;

        Console.WriteLine($"[NatServer] Matched session '{sessionId}': host {host} <-> joiner {joiner}");
        SendPeerReady(joiner!, host!);
        SendPeerReady(host!, joiner!);
    }

    /// <summary>
    /// Refreshes the heartbeat timestamp for an existing session and acknowledges with <see cref="PacketType.NatHeartbeatAck"/>.
    /// </summary>
    private void HandleHeartbeat(byte[] data, IPEndPoint from)
    {
        if (!NatSessionRegistry.TryParseSessionId(data, offset: 1, out uint sessionId))
            return;

        _registry.Heartbeat(sessionId, from);
        SendHeartbeatAck(from);
    }

    /// <summary>
    /// Closes an existing session, preventing any further joiners. Only honoured when the request comes from the session host.
    /// </summary>
    private void HandleCloseSession(byte[] data, IPEndPoint from)
    {
        if (!NatSessionRegistry.TryParseSessionId(data, offset: 1, out uint sessionId))
            return;

        if (_registry.CloseSession(sessionId, from))
            Console.WriteLine($"[NatServer] Session '{sessionId}' closed by host {from}.");
    }

    // -------------------------------------------------------------------------
    // Send helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a <see cref="PacketType.NatSessionCreated"/> packet carrying the server-assigned session ID.
    /// </summary>
    private void SendNatSessionCreated(IPEndPoint to, uint sessionId)
    {
        int fullSize = 1 + NatWireFormat.SessionIdBytes;
        byte[] packet = ArrayPool<byte>.Shared.Rent(fullSize);
        packet[0] = (byte)PacketType.NatSessionCreated;

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1, NatWireFormat.SessionIdBytes), sessionId);

        _ = SendAndReturnAsync(packet, fullSize, to);
    }

    /// <summary>
    /// Sends a <see cref="PacketType.NatPeerReady"/> packet to <paramref name="to"/> carrying <paramref name="peer"/>'s external endpoint.
    /// </summary>
    private void SendPeerReady(IPEndPoint to, IPEndPoint peer)
    {
        // Maximum packet size: type (1) + address family (1) + IPv6 address (16) + port (2) = 20 bytes.
        byte[] packet = ArrayPool<byte>.Shared.Rent(20);

        int offset = 0;
        packet[offset++] = (byte)PacketType.NatPeerReady;
        int addressFamilyOffset = offset++;

        if (!peer.Address.TryWriteBytes(packet.AsSpan(offset), out int addressLength))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        packet[addressFamilyOffset] = addressLength == 4 ? (byte)4 : (byte)6;
        offset += addressLength;
        packet[offset++] = (byte)(peer.Port & 0xFF);
        packet[offset++] = (byte)((peer.Port >> 8) & 0xFF);

        _ = SendAndReturnAsync(packet, offset, to);
    }

    /// <summary>
    /// Sends a <see cref="PacketType.NatHeartbeatAck"/> packet using the shared immutable buffer.
    /// </summary>
    private void SendHeartbeatAck(IPEndPoint to)
    {
        _ = _socket.SendAsync(HeartbeatAckPacket, HeartbeatAckPacket.Length, to);
    }

    /// <summary>
    /// Sends a <see cref="PacketType.NatSessionFull"/> packet indicating the session was not found or is already full.
    /// </summary>
    private void SendSessionFull(IPEndPoint to)
    {
        _ = _socket.SendAsync(SessionFullPacket, SessionFullPacket.Length, to);
    }

    /// <summary>
    /// Sends a <see cref="PacketType.NatSessionUnavailable"/> packet indicating the server's concurrent session limit has been reached.
    /// </summary>
    private void SendSessionUnavailable(IPEndPoint to)
    {
        _ = _socket.SendAsync(SessionUnavailablePacket, SessionUnavailablePacket.Length, to);
    }

    /// <summary>
    /// Sends a rented buffer to the target and returns it to <see cref="ArrayPool{T}.Shared"/> once the send completes.
    /// </summary>
    private async Task SendAndReturnAsync(byte[] packet, int length, IPEndPoint to)
    {
        try
        {
            await _socket.SendAsync(packet, length, to).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
        _socket.Dispose();
    }
}