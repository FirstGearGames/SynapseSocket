namespace SynapseSocket.Connections;

/// <summary>
/// Whether the peer on a connection takes part in session tokens, which decides whether its disconnects, keep-alives, acknowledgements
/// and segment acknowledgements must carry one and whether this side's carry one.
/// </summary>
internal enum SessionTokenState : byte
{
    /// <summary>
    /// No handshake has been accepted from the peer yet, so it is not known whether it supports session tokens. Only an outbound
    /// connection still waiting on its handshake is in this state. Inbound control packets must carry the token, and outbound ones carry
    /// none, because the peer's token is not known yet.
    /// </summary>
    Unknown,

    /// <summary>
    /// Both sides advertised session tokens, so control packets carry them in both directions.
    /// </summary>
    Supported,

    /// <summary>
    /// The peer's handshake did not advertise session tokens, so control packets carry none in either direction and the connection
    /// behaves exactly as it did before session tokens existed.
    /// </summary>
    Unsupported
}
