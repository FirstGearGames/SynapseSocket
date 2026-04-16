using System;
using System.Threading;
using System.Threading.Tasks;
using SynapseBeacon.Server;

namespace SynapseBeacon.Host;

/// <summary>
/// Console entry-point for running a <see cref="BeaconServer"/> as a standalone rendezvous service.
/// <para>
/// Accepts configuration through command-line flags and blocks on the server's receive loop until
/// Ctrl+C is pressed. See <see cref="PrintUsage"/> for the full flag list.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// Default UDP port the rendezvous server binds to when <c>--port</c> is not supplied.
    /// </summary>
    private const int DefaultPort = 47776;

    /// <summary>
    /// Default heartbeat timeout before a session is evicted from the registry, in milliseconds.
    /// </summary>
    private const uint DefaultSessionTimeoutMilliseconds = BeaconServer.DefaultSessionTimeoutMilliseconds;

    /// <summary>
    /// Default concurrent session cap passed to <see cref="BeaconServer"/>.
    /// <see cref="BeaconServer.UnlimitedConcurrentSessions"/> means no cap is enforced.
    /// </summary>
    private const int DefaultMaximumConcurrentSessions = BeaconServer.UnlimitedConcurrentSessions;

    private static async Task<int> Main(string[] args)
    {
        int port = DefaultPort;
        uint sessionTimeoutMilliseconds = DefaultSessionTimeoutMilliseconds;
        int maximumConcurrentSessions = DefaultMaximumConcurrentSessions;

        /* Parse command-line flags, overriding defaults for any values that are explicitly supplied. */
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p":
                case "--port":
                    if (++i >= args.Length || !int.TryParse(args[i], out port) || port is < 1 or > 65535)
                        return Fail("invalid --port value (expected 1-65535)");

                    break;

                case "--session-timeout-ms":
                    if (++i >= args.Length || !uint.TryParse(args[i], out sessionTimeoutMilliseconds))
                        return Fail("invalid --session-timeout-ms value");

                    break;

                case "--max-sessions":
                    if (++i >= args.Length || !int.TryParse(args[i], out maximumConcurrentSessions) || maximumConcurrentSessions < 0)
                        return Fail("invalid --max-sessions value (expected 0 or positive integer)");

                    break;

                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;

                default:
                    return Fail($"unknown argument: {args[i]}");
            }
        }

        /* Echo the resolved configuration so operators can confirm which values are in effect. */
        Console.WriteLine("=== SynapseBeacon Rendezvous Server ===");
        Console.WriteLine($"Port                    : {port}");
        Console.WriteLine($"Session timeout (ms)    : {sessionTimeoutMilliseconds}");
        Console.WriteLine($"Max concurrent sessions : {(maximumConcurrentSessions == 0 ? "unlimited" : maximumConcurrentSessions.ToString())}");
        Console.WriteLine();

        using BeaconServer beaconServer = new(port, sessionTimeoutMilliseconds, maximumConcurrentSessions, log: Log);
        using CancellationTokenSource cancellationTokenSource = new();

        /* Routed through a helper so the handler closure doesn't capture the `using` local in
         * this scope (which would trip the "captured variable is disposed in outer scope" analyser warning). */
        RegisterCancelKeyPressHandler(cancellationTokenSource);

        try
        {
            await beaconServer.RunAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* expected on shutdown */
        }

        Console.WriteLine("Server stopped.");
        return 0;
    }

    /// <summary>
    /// Pipes server log lines directly to stdout so all server activity is visible in the console.
    /// </summary>
    private static void Log(string message)
    {
        Console.WriteLine(message);
    }

    /// <summary>
    /// Wires <see cref="Console.CancelKeyPress"/> to cancel <paramref name="cancellationTokenSource"/>.
    /// Taking the token source as a parameter breaks the closure capture of the caller's <c>using</c>
    /// local, avoiding the "captured variable is disposed in outer scope" analyser warning.
    /// </summary>
    private static void RegisterCancelKeyPressHandler(CancellationTokenSource cancellationTokenSource)
    {
        Console.CancelKeyPress += (_, consoleCancelEventArgs) =>
        {
            consoleCancelEventArgs.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("[signal] shutdown requested (Ctrl+C).");
            cancellationTokenSource.Cancel();
        };
    }

    /// <summary>
    /// Writes <paramref name="errorMessage"/> to stderr, prints the usage block, and returns a
    /// non-zero exit code so the shell can detect the failure.
    /// </summary>
    private static int Fail(string errorMessage)
    {
        Console.Error.WriteLine($"error: {errorMessage}");
        Console.Error.WriteLine();
        PrintUsage(Console.Error);
        return 1;
    }

    /// <summary>
    /// Prints the help and usage block to <paramref name="writer"/>, defaulting to stdout when
    /// no writer is supplied.
    /// </summary>
    private static void PrintUsage(System.IO.TextWriter? writer = null)
    {
        writer ??= Console.Out;

        writer.WriteLine("Usage: SynapseBeacon.Host [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine($"  -p, --port <int>              UDP port to bind (default: {DefaultPort})");
        writer.WriteLine($"  --session-timeout-ms <uint>   Heartbeat timeout before session eviction (default: {DefaultSessionTimeoutMilliseconds})");
        writer.WriteLine($"  --max-sessions <int>          Concurrent session cap; 0 = unlimited (default: {DefaultMaximumConcurrentSessions})");
        writer.WriteLine("  -h, --help                    Show this help and exit");
    }
}
