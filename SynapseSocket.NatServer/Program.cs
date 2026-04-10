using System;
using System.Threading;
using SynapseSocket.NatServer;

int port = 7776;
if (args.Length > 0 && int.TryParse(args[0], out int parsedPort))
    port = parsedPort;

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using NatServer server = new(port);
await server.RunAsync(cts.Token);
Console.WriteLine("Shutdown complete.");