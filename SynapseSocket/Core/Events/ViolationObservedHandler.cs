namespace SynapseSocket.Core.Events;

/// <summary>
/// Delegate for <see cref="SynapseManager.ViolationObserved"/>.
/// </summary>
/// <param name="violationEventArgs">
/// Details about the violation, with <see cref="ViolationEventArgs.Action"/> holding the action the engine is about to take.
/// </param>
public delegate void ViolationObservedHandler(in ViolationEventArgs violationEventArgs);
