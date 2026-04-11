namespace SynapseSocket.Core.Events;

/// <summary>
/// Delegate for <see cref="SynapseManager.ViolationDetected"/>.
/// </summary>
/// <param name="violationEventArgs">Details about the violation and the action the engine will take.</param>
public delegate void ViolationDelegate(ViolationEventArgs violationEventArgs);
