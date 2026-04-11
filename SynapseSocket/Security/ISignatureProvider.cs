using System.Net;

namespace SynapseSocket.Security;

/// <summary>
/// Intermediate interface defining how connection signatures are calculated.
/// Implementations should derive a stable identifier for a remote peer from properties such as its <see cref="IPEndPoint"/>, device info, or handshake payload.
/// Signatures are used for identity, blacklisting, and spoofing mitigation.
/// </summary>
public interface ISignatureProvider
{
    /// <summary>
    /// Computes a signature for the remote endpoint. The same endpoint should produce the same signature across calls.
    /// </summary>
    /// <param name="endPoint">The remote peer's endpoint.</param>
    /// <param name="handshakePayload">
    /// The handshake payload bytes, or empty if not yet available.
    /// The payload contains a unique nonce per handshake.
    /// Implementations MUST incorporate <paramref name="handshakePayload"/> into the computed signature when it is non-empty; implementations that hash only the endpoint bypass the engine's handshake replay cache and provide no replay protection.
    /// </param>
    /// <param name="signature">A produced 64-bit signature.</param>
    /// <returns>True if a signature was produced without error.</returns>
    bool TryCompute(IPEndPoint endPoint, System.ReadOnlySpan<byte> handshakePayload, out ulong signature);
}