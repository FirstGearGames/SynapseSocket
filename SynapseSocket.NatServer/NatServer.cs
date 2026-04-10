using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Packets;

namespace SynapseSocket.NatServer;

/// <summary>
/// Lightweight UDP rendezvous server that matches pairs of NAT-traversal peers.
/// <para>
/// Clients send <see cref="NatPacketType.Register"/> packets with a shared
/// <see cref="ServerNatConfig.SessionIdLength"/>-character alphanumeric session ID.
/// When two clients share the same ID the server responds to each with a
/// <see cref="NatPacketType.PeerReady"/> packet containing the other peer''s external endpoint,
/// after which hole-punching proceeds directly between the two clients.
/// </para>
/// </summary>
public sealed class NatServer : IDisposable
{
    private static readonly int SessionIdBytes = ServerNatConfig.SessionIdLength;

    private readonly UdpClient _socket;
    private readonly NatSessionRegistry _registry;
    private readonly Timer _evictionTimer;

    /// <summary>
    /// Initialises the server bound to <paramref name="port"/> on all interfaces.
    /// </summary>
    public NatServer(int port, uint sessionTimeoutMilliseconds = 30_000)
    {
        _socket = new(port);
        _registry = new(sessionTimeoutMilliseconds);
        _evictionTimer = new(_ => _registry.EvictExpired(), null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
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
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"[NatServer] Socket error: {ex.Message}");
                continue;
            }
            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    private void HandleDatagram(byte[] data, IPEndPoint from)
    {
        // Layout: [PacketFlags.Extended (1)] [NatPacketType (1)] [session ID (6 ASCII bytes)]
        if (data.Length < 2) return;
        if ((PacketFlags)data[0] != PacketFlags.Extended) return;

        NatPacketType type = (NatPacketType)data[1];

        switch (type)
        {
            case NatPacketType.Register:
                HandleRegister(data, from);
                break;
            case NatPacketType.Heartbeat:
                HandleHeartbeat(data, from);
                break;
        }
    }

    private void HandleRegister(byte[] data, IPEndPoint from)
    {
        string? sessionId = NatSessionRegistry.ParseSessionId(data, offset: 2, length: SessionIdBytes);
        if (sessionId is null) return;

        (bool matched, bool full, IPEndPoint? first, IPEndPoint? second) = _registry.Register(sessionId, from);

        if (full)
        {
            Console.WriteLine($"[NatServer] Session {sessionId} is full — rejecting {from}.");
            SendSessionFull(from);
            return;
        }

        if (!matched)
        {
            Console.WriteLine($"[NatServer] Registered {from} for session '{sessionId}' — waiting for peer.");
            return;
        }

        Console.WriteLine($"[NatServer] Matched session '{sessionId}': {first} <-> {second}");
        SendPeerReady(first!, second!);
        SendPeerReady(second!, first!);
    }

    private void HandleHeartbeat(byte[] data, IPEndPoint from)
    {
        string? sessionId = NatSessionRegistry.ParseSessionId(data, offset: 2, length: SessionIdBytes);
        if (sessionId is null) return;
        _registry.Heartbeat(sessionId, from);
        SendHeartbeatAck(from);
    }

    // -------------------------------------------------------------------------
    // Send helpers
    // -------------------------------------------------------------------------

    private void SendPeerReady(IPEndPoint to, IPEndPoint peer)
    {
        byte[] peerBytes = peer.Address.GetAddressBytes();
        byte addrFamily = peerBytes.Length == 4 ? (byte)4 : (byte)6;
        byte[] packet = new byte[1 + 1 + 1 + peerBytes.Length + 2];
        int offset = 0;
        packet[offset++] = (byte)PacketFlags.Extended;
        packet[offset++] = (byte)NatPacketType.PeerReady;
        packet[offset++] = addrFamily;
        peerBytes.CopyTo(packet, offset);
        offset += peerBytes.Length;
        packet[offset++] = (byte)(peer.Port & 0xFF);
        packet[offset]   = (byte)((peer.Port >> 8) & 0xFF);
        _ = _socket.SendAsync(packet, packet.Length, to);
    }

    private void SendHeartbeatAck(IPEndPoint to)
    {
        byte[] packet = [(byte)PacketFlags.Extended, (byte)NatPacketType.HeartbeatAck];
        _ = _socket.SendAsync(packet, packet.Length, to);
    }

    private void SendSessionFull(IPEndPoint to)
    {
        byte[] packet = [(byte)PacketFlags.Extended, (byte)NatPacketType.SessionFull];
        _ = _socket.SendAsync(packet, packet.Length, to);
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
        _socket.Dispose();
    }
}