using System;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Delegate for <see cref="SynapseManager.UnhandledException"/>.
/// </summary>
/// <param name="exception">The exception that escaped the engine during <see cref="SynapseManager.Poll"/>.</param>
public delegate void UnhandledExceptionHandler(Exception exception);
