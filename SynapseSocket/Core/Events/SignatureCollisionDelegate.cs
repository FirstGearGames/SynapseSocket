namespace SynapseSocket.Core.Events;

/// <summary>
/// Raised when two connections produce the same 64-bit signature.
/// The newer connection overwrites the reverse-lookup slot.
/// </summary>
public delegate void SignatureCollisionDelegate(ulong signature);
