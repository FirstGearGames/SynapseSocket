using System.Diagnostics;
using CodeBoost.Environment;
using CodeBoost.Extensions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Logging
{
 public static class LoggingService
	{
	    /// <summary>
	    /// Called when Logger is set.
	    /// </summary>
	    public static event LoggerSetEventHandler? LoggerSet;
	    public delegate void LoggerSetEventHandler(ILogger logger);
	    /// <summary>
	    /// ILogger to use.
	    /// </summary>
	    public static ILogger? Logger;
	    /// <summary>
	    /// Specifies which ILogger to use.
	    /// </summary>
	    public static void UseLogger(ILogger logger)
	    {
	    }
	
	    public static bool DisableUnconditionalDevelopmentStacktrace()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Logs a message as information.
	    /// </summary>
	    public static void LogInformation(string message)
	    {
	    }
	
	    /// <summary>
	    /// Logs a message as warning.
	    /// </summary>
	    public static void LogWarning(string message)
	    {
	    }
	
	    /// <summary>
	    /// Logs a message as error.
	    /// </summary>
	    public static void LogError(string message)
	    {
	    }
	}
}
