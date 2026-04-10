using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SynapseSocket.Core.Configuration;

/// <summary>
/// Settings specific to <see cref="NatTraversalMode.Server"/> rendezvous-assisted hole punching.
/// Both peers register with the same <see cref="ServerEndPoint"/> using the same <see cref="SessionId"/>;
/// the server exchanges their external endpoints and hole-punching proceeds automatically.
/// </summary>
public sealed class ServerNatConfig
{
    private const string AlphaNumericChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>
    /// Length of a session ID in characters.
    /// </summary>
    public const int SessionIdLength = 6;

    /// <summary>
    /// Endpoint of the publicly reachable NAT rendezvous server.
    /// Must be set before calling <see cref="SynapseSocket.Core.SynapseManager.ConnectViaNatServerAsync"/>.
    /// </summary>
    public IPEndPoint? ServerEndPoint;

    /// <summary>
    /// Shared <see cref="SessionIdLength"/>-character alphanumeric session code.
    /// Both peers must use the same value to be matched together.
    /// Exchange this out-of-band (e.g., via a lobby or matchmaking service).
    /// Use <see cref="GenerateSessionId"/> to create a random code.
    /// </summary>
    public string SessionId = string.Empty;

    /// <summary>
    /// Milliseconds to wait for the peer to register before the attempt is abandoned.
    /// </summary>
    public uint RegistrationTimeoutMilliseconds = 15000;

    /// <summary>
    /// Milliseconds between heartbeat packets sent to the rendezvous server while waiting for a peer.
    /// </summary>
    public uint HeartbeatIntervalMilliseconds = 3000;

    /// <summary>
    /// Generates a random <see cref="SessionIdLength"/>-character uppercase alphanumeric session ID
    /// suitable for display and manual entry (e.g., <c>"A3X7KQ"</c>).
    /// </summary>
    public static string GenerateSessionId()
    {
        byte[] randomBytes = new byte[SessionIdLength];
        RandomNumberGenerator.Fill(randomBytes);
        StringBuilder sb = new(SessionIdLength);
        foreach (byte b in randomBytes)
            sb.Append(AlphaNumericChars[b % AlphaNumericChars.Length]);
        return sb.ToString();
    }
}