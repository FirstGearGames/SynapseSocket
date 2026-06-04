using System;
using System.Net;
using System.Text;
using System.Threading;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Demo;

/// <summary>
/// Minimal end-to-end demo of the Synapse UDP transport engine.
/// Spins up a server on localhost:45000 and a client that connects,
/// exchanges a reliable and an unreliable message, then disconnects.
/// <para>
/// The engine is poll-driven: the demo pumps both engines with <see cref="Pump"/> from this single thread.
/// </para>
/// </summary>
internal static class Program
{
    private const int Port = 45000;

    private static void Main()
    {
        Console.WriteLine("=== Synapse Demo ===");

        // --- Build server config ---
        SynapseConfig serverConfig = new()
        {
            BindEndPoints = [new(IPAddress.Any, Port)],
            EnableTelemetry = true,
            Segment = { MaximumSegments = 128, UnreliableMode = UnreliableSegmentMode.SegmentUnreliable },
        };

        using SynapseManager server = new(serverConfig);
        WireServerEvents(server);
        server.Start();
        Console.WriteLine($"[server] bound on port {Port}");

        // --- Build client config ---
        SynapseConfig clientConfig = new()
        {
            BindEndPoints = [new(IPAddress.Any, 0)],
            EnableTelemetry = true,
            Segment = { MaximumSegments = 128, UnreliableMode = UnreliableSegmentMode.SegmentUnreliable },
        };

        using SynapseManager client = new(clientConfig);
        bool clientConnected = false;
        bool clientReplyReceived = false;

        client.ConnectionEstablished += (connectionEventArgs) =>
        {
            Console.WriteLine($"[client] connected to {connectionEventArgs.Connection.RemoteEndPoint} (sig=0x{connectionEventArgs.Connection.Signature:X})");
            clientConnected = true;
        };
        client.PacketReceived += (packetReceivedEventArgs) =>
        {
            string decodedText = Encoding.UTF8.GetString(packetReceivedEventArgs.Payload);
            Console.WriteLine($"[client] received ({(packetReceivedEventArgs.IsReliable ? "reliable" : "unreliable")}): {decodedText}");
            clientReplyReceived = true;
        };
        client.ConnectionClosed += (connectionEventArgs) => Console.WriteLine($"[client] connection closed: {connectionEventArgs.Connection.RemoteEndPoint}");
        client.ConnectionFailed += (connectionFailedEventArgs) => Console.WriteLine($"[client] failure: {connectionFailedEventArgs.Reason} {connectionFailedEventArgs.Message}");

        client.Start();
        Console.WriteLine("[client] started");

        IPEndPoint serverEndPoint = new(IPAddress.Loopback, Port);
        SynapseConnection synapseConnection = client.Connect(serverEndPoint);

        // Pump until the handshake completes (server handshake-ack arrives back).
        Pump(server, client, () => clientConnected, 2000);

        // Send a reliable hello.
        byte[] helloPayload = Encoding.UTF8.GetBytes("Hello from client (reliable)");
        client.Send(synapseConnection, helloPayload, isReliable: true);

        byte[] fragmentedPayload = new byte[2500];
        client.Send(synapseConnection, fragmentedPayload, isReliable: false);

        // Send an unreliable ping.
        byte[] pingPayload = Encoding.UTF8.GetBytes("Ping from client (unreliable)");
        client.Send(synapseConnection, pingPayload, isReliable: false);

        // Pump until an echo arrives.
        Pump(server, client, () => clientReplyReceived, 2000);

        Pump(server, client, () => false, 300);
        client.Disconnect(synapseConnection);
        Pump(server, client, () => false, 200);

        Console.WriteLine();
        Console.WriteLine("=== Telemetry ===");
        Console.WriteLine($"[server] in={server.Telemetry.BytesIn}B/{server.Telemetry.PacketsIn}pkts  out={server.Telemetry.BytesOut}B/{server.Telemetry.PacketsOut}pkts  dropped_in={server.Telemetry.PacketsDroppedIn}");
        Console.WriteLine($"[client] in={client.Telemetry.BytesIn}B/{client.Telemetry.PacketsIn}pkts  out={client.Telemetry.BytesOut}B/{client.Telemetry.PacketsOut}pkts  dropped_in={client.Telemetry.PacketsDroppedIn}");
        Console.WriteLine("Demo finished.");
    }

    /// <summary>
    /// Pumps both engines until <paramref name="until"/> is true or <paramref name="timeoutMs"/> elapses.
    /// </summary>
    private static void Pump(SynapseManager server, SynapseManager client, Func<bool> until, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            server.Poll();
            client.Poll();
            if (until())
                return;
            Thread.Sleep(1);
        }
    }

    private static void WireServerEvents(SynapseManager server)
    {
        server.ConnectionEstablished += (connectionEventArgs) => Console.WriteLine($"[server] peer connected: {connectionEventArgs.Connection.RemoteEndPoint} (sig=0x{connectionEventArgs.Connection.Signature:X})");

        server.ConnectionClosed += (connectionEventArgs) => Console.WriteLine($"[server] peer disconnected: {connectionEventArgs.Connection.RemoteEndPoint}");

        server.ConnectionFailed += (connectionFailedEventArgs) => Console.WriteLine($"[server] failure: {connectionFailedEventArgs.Reason} ({connectionFailedEventArgs.EndPoint}) {connectionFailedEventArgs.Message}");

        server.PacketReceived += (packetReceivedEventArgs) =>
        {
            string decodedText = Encoding.UTF8.GetString(packetReceivedEventArgs.Payload);
            Console.WriteLine($"[server] received ({(packetReceivedEventArgs.IsReliable ? "reliable" : "unreliable")}) from {packetReceivedEventArgs.Connection.RemoteEndPoint}: {decodedText}");

            // Echo back on the same channel (re-entrant send from the receive callback is safe — single-threaded).
            byte[] replyPayload = Encoding.UTF8.GetBytes($"echo: {decodedText}");
            try
            {
                server.Send(packetReceivedEventArgs.Connection, replyPayload, packetReceivedEventArgs.IsReliable);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[server] echo failed: {ex.Message}");
            }
        };
    }
}
