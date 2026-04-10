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
	public static class Logger<T0>
	{
	    public static void LogInformation(string message, [CallerMemberName] string methodName = "")
	    {
	    }
	
	    public static void LogWarning(string message, [CallerMemberName] string methodName = "")
	    {
	    }
	
	    public static void LogError(string message, [CallerMemberName] string methodName = "")
	    {
	    }
	}
}
