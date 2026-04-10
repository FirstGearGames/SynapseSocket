using System;

namespace SynapseSocket.Core.Events;

/// <summary>
/// Delegate for <see cref="SynapseManager.UnhandledException"/>.
/// </summary>
public delegate void UnhandledExceptionDelegate(Exception exception);
