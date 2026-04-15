using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SynapseBeacon.Wire;

namespace SynapseBeacon.Server;

/// <summary>
/// Lightweight UDP rendezvous server that matches SynapseBeacon peers for NAT hole-punching.
/// <para>
/// The host sends <see cref="BeaconPacketType.RequestSession"/> to obtain a server-assigned session ID.
/// The server responds with <see cref="BeaconPacketType.SessionCreated"/> containing the ID.
/// The host shares the ID out-of-band with any number of joiners.
/// Each joiner sends <see cref="BeaconPacketType.Register"/> with the shared ID; the server responds
/// to that joiner with the host's external endpoint and to the host with the joiner's endpoint,
/// after which hole-punching proceeds directly between each pair using the caller's
/// <c>SynapseSocket</c> engine.
/// The host closes the session with <see cref="BeaconPacketType.CloseSession"/> when done accepting.
/// Sessions that receive no heartbeat within <see cref="BeaconSessionRegistry.SessionTimeoutMilliseconds"/>
/// are evicted automatically.
/// </para>
/// </summary>
public sealed class BeaconServer : IDisposable
{
    /// <summary>Maximum UDP datagram size.</summary>
    private const int MaximumUdpDatagramSize = 65535;

    private readonly UdpClient _socket;
    private readonly BeaconSessionRegistry _registry;
    private readonly Timer _evictionTimer;
    private readonly Action<string>? _log;

    /// <summary>
    /// Immutable single-byte payload for <see cref="BeaconPacketType.HeartbeatAck"/>. Shared across all sends.
    /// </summary>
    private static readonly byte[] HeartbeatAckPacket = [(byte)BeaconPacketType.HeartbeatAck];
    /// <summary>
    /// Immutable single-byte payload for <see cref="BeaconPacketType.SessionFull"/>. Shared across all sends.
    /// </summary>
    private static readonly byte[] SessionFullPacket = [(byte)BeaconPacketType.SessionFull];
    /// <summary>
    /// Immutable single-byte payload for <see cref="BeaconPacketType.SessionUnavailable"/>. Shared across all sends.
    /// </summary>
    private static readonly byte[] SessionUnavailablePacket = [(byte)BeaconPacketType.SessionUnavailable];

    /// <summary>
    /// Initialises the server bound to <paramref name="port"/> on all interfaces.
    /// </summary>
    /// <param name="port">UDP port to listen on.</param>
    /// <param name="sessionTimeoutMilliseconds">Milliseconds of heartbeat silence before a session is evicted.</param>
    /// <param name="maximumConcurrentSessions">Maximum number of sessions open simultaneously. 0 = unlimited.</param>
    /// <param name="log">Optional log sink. When null, logging is silent.</param>
    public BeaconServer(int port, uint sessionTimeoutMilliseconds = 300_000, int maximumConcurrentSessions = 0, Action<string>? log = null)
    {
        _socket = new(port);
        _registry = new(sessionTimeoutMilliseconds, maximumConcurrentSessions);
        _evictionTimer = new(_ => _registry.EvictExpired(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _log = log;
    }

    /// <summary>
    /// Runs the receive loop until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _log?.Invoke($"[BeaconServer] listening (session timeout: {_registry.SessionTimeoutMilliseconds} ms)");

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;

            try
            {
                #if NET8_0_OR_GREATER
                result = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                #else
                result = await _socket.ReceiveAsync().ConfigureAwait(false);
                #endif
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _log?.Invoke($"[BeaconServer] socket error: {ex.Message}");
                continue;
            }

            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    /// <summary>
    /// Routes an inbound datagram to the appropriate handler based on its <see cref="BeaconPacketType"/> byte.
    /// </summary>
    private void HandleDatagram(byte[] data, IPEndPoint from)
    {
        // Layout: [BeaconPacketType (1 byte)] [payload]
        if (data.Length < 1)
            return;

        BeaconPacketType type = (BeaconPacketType)data[0];

        switch (type)
        {
            case BeaconPacketType.RequestSession:
                HandleRequestSession(from);
                break;

            case BeaconPacketType.Register:
                HandleRegister(data, from);
                break;

            case BeaconPacketType.Heartbeat:
                HandleHeartbeat(data, from);
                break;

            case BeaconPacketType.CloseSession:
                HandleCloseSession(data, from);
                break;
        }
    }

    /// <summary>
    /// Creates a new session for the requesting host and sends back a <see cref="BeaconPacketType.SessionCreated"/> response,
    /// or <see cref="BeaconPacketType.SessionUnavailable"/> if the concurrent session limit has been reached.
    /// </summary>
    private void HandleRequestSession(IPEndPoint from)
    {
        if (!_registry.TryCreateSession(from, out uint sessionId))
        {
            _log?.Invoke($"[BeaconServer] session limit reached — rejecting request from {from}.");
            SendSessionUnavailable(from);
            return;
        }

        _log?.Invoke($"[BeaconServer] created session '{sessionId}' for host {from}.");
        SendSessionCreated(from, sessionId);
    }

    /// <summary>
    /// Registers a joining peer against an existing session. Sends <see cref="BeaconPacketType.PeerReady"/> to both
    /// the joiner and the host on success, or <see cref="BeaconPacketType.SessionFull"/> if the session was not found.
    /// </summary>
    private void HandleRegister(byte[] data, IPEndPoint from)
    {
        if (!BeaconWireFormat.TryReadSessionId(data.AsSpan(1), out uint sessionId))
            return;

        (bool matched, bool notFound, IPEndPoint? host, IPEndPoint? joiner) = _registry.Register(sessionId, from);

        if (notFound)
        {
            _log?.Invoke($"[BeaconServer] session '{sessionId}' not found — rejecting {from}.");
            SendSessionFull(from);
            return;
        }

        if (!matched)
            return;

        _log?.Invoke($"[BeaconServer] matched session '{sessionId}': host {host} <-> joiner {joiner}");
        SendPeerReady(joiner!, host!);
        SendPeerReady(host!, joiner!);
    }

    /// <summary>
    /// Refreshes the heartbeat timestamp for an existing session and acknowledges with <see cref="BeaconPacketType.HeartbeatAck"/>.
    /// </summary>
    private void HandleHeartbeat(byte[] data, IPEndPoint from)
    {
        if (!BeaconWireFormat.TryReadSessionId(data.AsSpan(1), out uint sessionId))
            return;

        _registry.Heartbeat(sessionId, from);
        SendHeartbeatAck(from);
    }

    /// <summary>
    /// Closes an existing session, preventing any further joiners. Only honoured when the request comes from the session host.
    /// </summary>
    private void HandleCloseSession(byte[] data, IPEndPoint from)
    {
        if (!BeaconWireFormat.TryReadSessionId(data.AsSpan(1), out uint sessionId))
            return;

        if (_registry.CloseSession(sessionId, from))
            _log?.Invoke($"[BeaconServer] session '{sessionId}' closed by host {from}.");
    }

    // -------------------------------------------------------------------------
    // Send helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.SessionCreated"/> packet carrying the server-assigned session ID.
    /// </summary>
    private void SendSessionCreated(IPEndPoint to, uint sessionId)
    {
        const int Size = 1 + BeaconWireFormat.SessionIdBytes;
        byte[] packet = ArrayPool<byte>.Shared.Rent(Size);
        BeaconWireFormat.WriteTypeAndSessionId(packet.AsSpan(), BeaconPacketType.SessionCreated, sessionId);
        _ = SendAndReturnAsync(packet, Size, to);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.PeerReady"/> packet to <paramref name="to"/> carrying <paramref name="peer"/>'s external endpoint.
    /// </summary>
    private void SendPeerReady(IPEndPoint to, IPEndPoint peer)
    {
        int bufferSize = 1 + BeaconWireFormat.MaxPeerEndPointBytes;
        byte[] packet = ArrayPool<byte>.Shared.Rent(bufferSize);

        packet[0] = (byte)BeaconPacketType.PeerReady;
        int payloadLength = BeaconWireFormat.WritePeerEndPoint(packet.AsSpan(1), peer);

        if (payloadLength == 0)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        _ = SendAndReturnAsync(packet, 1 + payloadLength, to);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.HeartbeatAck"/> packet using the shared immutable buffer.
    /// </summary>
    private void SendHeartbeatAck(IPEndPoint to)
    {
        _ = _socket.SendAsync(HeartbeatAckPacket, HeartbeatAckPacket.Length, to);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.SessionFull"/> packet indicating the session was not found or is already full.
    /// </summary>
    private void SendSessionFull(IPEndPoint to)
    {
        _ = _socket.SendAsync(SessionFullPacket, SessionFullPacket.Length, to);
    }

    /// <summary>
    /// Sends a <see cref="BeaconPacketType.SessionUnavailable"/> packet indicating the server's concurrent session limit has been reached.
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

    /// <inheritdoc/>
    public void Dispose()
    {
        _evictionTimer.Dispose();
        _socket.Dispose();
    }
}
