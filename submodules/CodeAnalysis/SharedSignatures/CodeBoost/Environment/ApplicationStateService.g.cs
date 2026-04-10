using CodeBoost.Extensions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Environment
{
 public static class ApplicationStateService
	{
	    /// <summary>
	    /// Specifies which ILogger to use.
	    /// </summary>
	    public static void UseApplicationState(IApplicationState applicationState)
	    {
	    }
	
	    /// <summary>
	    /// True if the application is being run within an editor.
	    /// </summary>
	    public static bool IsEditor()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// True if the application is a build with development or debugging enabled.
	    /// </summary>
	    /// >
	    public static bool IsDevelopmentBuild()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// True if a GUI build, such as a client build.
	    /// </summary>
	    public static bool IsGUIBuild()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// True if a headless build, such as a server build.
	    /// </summary>
	    public static bool IsHeadlessBuild()
	    {
	        return default !;
	    }
	}
}
