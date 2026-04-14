using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;

namespace SynapseSocket.Demo;

/// <summary>
/// Minimal end-to-end demo of the Synapse UDP transport engine.
/// Spins up a server on localhost:45000 and a client that connects,
/// exchanges a reliable and an unreliable message, then disconnects.
/// </summary>
internal static class Program
{
    private const int Port = 45000;

    private static async Task Main()
    {
        Console.WriteLine("=== Synapse Demo ===");

        // --- Build server config ---
        SynapseConfig serverConfig = new()
        {
            BindEndPoints = [new(IPAddress.Any, Port)],
            EnableTelemetry = true,
            MaximumSegments = 128,
            UnreliableSegmentMode = UnreliableSegmentMode.SegmentUnreliable,
        };

        await using SynapseManager server = new(serverConfig);
        WireServerEvents(server);
        await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"[server] bound on port {Port}");

        // --- Build client config ---
        SynapseConfig clientConfig = new()
        {
            BindEndPoints = [new(IPAddress.Any, 0)],
            EnableTelemetry = true,
            MaximumSegments = 128,
            UnreliableSegmentMode = UnreliableSegmentMode.SegmentUnreliable,
        };

        await using SynapseManager client = new(clientConfig);
        TaskCompletionSource<bool> clientConnected = new();
        TaskCompletionSource<string> clientReply = new();

        client.ConnectionEstablished += (connectionEventArgs) =>
        {
            Console.WriteLine($"[client] connected to {connectionEventArgs.Connection.RemoteEndPoint} (sig=0x{connectionEventArgs.Connection.Signature:X})");
            clientConnected.TrySetResult(true);
        };
        client.PacketReceived += (packetReceivedEventArgs) =>
        {
            string decodedText = Encoding.UTF8.GetString(packetReceivedEventArgs.Payload);
            Console.WriteLine($"[client] received ({(packetReceivedEventArgs.IsReliable ? "reliable" : "unreliable")}): {decodedText}");
            clientReply.TrySetResult(decodedText);
        };
        client.ConnectionClosed += (connectionEventArgs) => Console.WriteLine($"[client] connection closed: {connectionEventArgs.Connection.RemoteEndPoint}");
        client.ConnectionFailed += (connectionFailedEventArgs) => Console.WriteLine($"[client] failure: {connectionFailedEventArgs.Reason} {connectionFailedEventArgs.Message}");

        await client.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine("[client] started");

        IPEndPoint serverEndPoint = new(IPAddress.Loopback, Port);
        SynapseConnection synapseConnection = await client.ConnectAsync(serverEndPoint, CancellationToken.None).ConfigureAwait(false);

        // Wait for handshake to complete (server handshake-ack arrives back).
        await Task.WhenAny(clientConnected.Task, Task.Delay(2000)).ConfigureAwait(false);

        // Send a reliable hello.
        byte[] helloPayload = Encoding.UTF8.GetBytes("Hello from client (reliable)");
        await client.SendAsync(synapseConnection, helloPayload, isReliable: true, CancellationToken.None).ConfigureAwait(false);

        byte[] fragmentedPayload = new byte[2500];
        await client.SendAsync(synapseConnection, fragmentedPayload, isReliable: false, CancellationToken.None).ConfigureAwait(false);

        // Send an unreliable ping.
        byte[] pingPayload = Encoding.UTF8.GetBytes("Ping from client (unreliable)");
        await client.SendAsync(synapseConnection, pingPayload, isReliable: false, CancellationToken.None).ConfigureAwait(false);

        // Wait for an echo.
        await Task.WhenAny(clientReply.Task, Task.Delay(2000)).ConfigureAwait(false);

        await Task.Delay(300).ConfigureAwait(false);
        await client.DisconnectAsync(synapseConnection, CancellationToken.None).ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("=== Telemetry ===");
        Console.WriteLine($"[server] in={server.Telemetry.BytesIn}B/{server.Telemetry.PacketsIn}pkts  out={server.Telemetry.BytesOut}B/{server.Telemetry.PacketsOut}pkts  dropped_in={server.Telemetry.PacketsDroppedIn}");
        Console.WriteLine($"[client] in={client.Telemetry.BytesIn}B/{client.Telemetry.PacketsIn}pkts  out={client.Telemetry.BytesOut}B/{client.Telemetry.PacketsOut}pkts  dropped_in={client.Telemetry.PacketsDroppedIn}");
        Console.WriteLine("Demo finished.");
    }

    private static void WireServerEvents(SynapseManager server)
    {
        server.ConnectionEstablished += (connectionEventArgs) => Console.WriteLine($"[server] peer connected: {connectionEventArgs.Connection.RemoteEndPoint} (sig=0x{connectionEventArgs.Connection.Signature:X})");

        server.ConnectionClosed += (connectionEventArgs) => Console.WriteLine($"[server] peer disconnected: {connectionEventArgs.Connection.RemoteEndPoint}");

        server.ConnectionFailed += (connectionFailedEventArgs) => Console.WriteLine($"[server] failure: {connectionFailedEventArgs.Reason} ({connectionFailedEventArgs.EndPoint}) {connectionFailedEventArgs.Message}");

        server.PacketReceived += async (packetReceivedEventArgs) =>
        {
            string decodedText = Encoding.UTF8.GetString(packetReceivedEventArgs.Payload);
            Console.WriteLine($"[server] received ({(packetReceivedEventArgs.IsReliable ? "reliable" : "unreliable")}) from {packetReceivedEventArgs.Connection.RemoteEndPoint}: {decodedText}");

            // Echo back on the same channel.
            byte[] replyPayload = Encoding.UTF8.GetBytes($"echo: {decodedText}");
            try
            {
                await server.SendAsync(packetReceivedEventArgs.Connection, replyPayload, packetReceivedEventArgs.IsReliable, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[server] echo failed: {ex.Message}");
            }
        };
    }
}