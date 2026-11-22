namespace SynapseSocket.Connections;

/// <summary>
/// Whether data packets on a connection carry the session token, which decides whether this side requires one on the data packets it
/// receives, leaves room for one in those it sends, and ends them with the peer's.
/// </summary>
internal enum DataSessionTokenState : byte
{
    /// <summary>
    /// Data packets carry no session token in either direction, exactly as before data session tokens existed: this side has
    /// <see cref="SynapseSocket.Core.Configuration.SecurityConfig.DataSessionTokenEnabled"/> off, or the handshake that established the
    /// session did not ask for them.
    /// </summary>
    Disabled,

    /// <summary>
    /// This side asked for data session tokens in a handshake whose answer has not established the session yet: an outbound connection
    /// still waiting on its handshake, or a session being replaced by a peer's proof. Inbound data packets must carry the token, and
    /// outbound ones leave room for it but carry none, because the peer's token for the session is not known yet. A reliable packet built
    /// in this state has the token written into that room once the session is established with data session tokens, so its resends carry it.
    /// </summary>
    Requested,

    /// <summary>
    /// Both sides asked for data session tokens, so every data packet carries the receiver's session token in both directions.
    /// </summary>
    Enabled
}
