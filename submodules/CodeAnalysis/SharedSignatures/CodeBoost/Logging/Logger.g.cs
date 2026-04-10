using System.Diagnostics;
using System.Runtime.CompilerServices;
using CodeBoost.Environment;
using CodeBoost.Extensions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Logging
{
 /// <summary>
	/// A static Logger which uses the currently registered ILogger.
	/// </summary>
	public static class Logger
	{
	    /// <summary>
	    /// Disables always including stacktrace in development environments.
	    /// </summary>
	    /// <returns>True to disable always including stacktrace in development environments.</returns>
	    /// <remarks>Even with unconditional inclusions disabled stacktrace will still be included for higher level log calls.</remarks>
	    public static bool DisableUnconditionalDevelopmentStacktrace() => default;
	    public static void LogInformation(string message, [CallerMemberName] string methodName = "")
	    {
	    }
	
	    public static void LogWarning(string message, [CallerMemberName] string methodName = "")
	    {
	    }
	
	    public static void LogError(string message, [CallerMemberName] string methodName = "")
	    {
	    }
	
	    public static void LogInformation(Type type, string message, [CallerMemberName] string methodName = "")
	    {
	    }
	
	    public static void LogWarning(Type type, string message, [CallerMemberName] string methodName = "")
	    {
	    }
	
	    public static void LogError(Type type, string message, [CallerMemberName] string methodName = "")
	    {
	    }
	
	    /// <summary>
	    /// Returns the prefix to use for a method under an invoking type.
	    /// </summary>
	    /// <param name="outerType">Type which contains the method logging the message.</param>
	    /// <param name="methodName">Name of the method logging the message.</param>
	    /// <returns></returns>
	    public static string GetLogMessagePrefix(Type outerType, Type innerType, string methodName) => default;
	    /// <summary>
	    /// Returns the prefix to use for a method under an invoking type.
	    /// </summary>
	    /// <param name="outerType">Type which contains the method logging the message.</param>
	    /// <param name="methodName">Name of the method logging the message.</param>
	    /// <returns></returns>
	    public static string GetLogMessagePrefix(Type outerType, string methodName) => default;
	    /// <summary>
	    /// Returns the prefix to use for a method under an invoking type.
	    /// </summary>
	    /// <param name="methodName">Name of the method logging the message.</param>
	    /// <returns></returns>
	    public static string GetLogMessagePrefix(string methodName) => default;
	    /// <summary>
	    /// Adds a StackTrace onto message if application is a development build.
	    /// </summary>
	    public static string AddStackTraceIfDevelopment(string message)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Adds a StackTrace to a message.
	    /// </summary>
	    public static string AddStackTrace(string message) => default;
	}
}
