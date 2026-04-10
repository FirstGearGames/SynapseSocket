using CodeBoost.Extensions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Environment
{
 /// <summary>
	/// A static ApplicationState which uses the currently registered IApplicationState.
	/// </summary>
	public static class ApplicationState
	{
	    /// <summary>
	    /// Called when the application focus state changes.
	    /// </summary>
	    public static event IApplicationState.FocusChangeEventHandler FocusChanged;
	    /// <summary>
	    /// True if the application is quitting.
	    /// </summary>
	    public static bool IsQuitting() => default;
	    /// <summary>
	    /// True if the application is playing.
	    /// </summary>
	    public static bool IsPlaying() => default;
	    /// <summary>
	    /// Quits the application for editor or builds.
	    /// </summary>
	    public static void Quit()
	    {
	    }
	
	    /// <summary>
	    /// True if the application is being run within an editor.
	    /// </summary>
	    public static bool IsEditor() => default;
	    /// <summary>
	    /// True if the application is a build with development or debugging enabled.
	    /// </summary>
	    /// >
	    public static bool IsDevelopmentBuild() => default;
	    /// <summary>
	    /// True if a GUI build, such as a client build.
	    /// </summary>
	    public static bool IsGUIBuild() => default;
	    /// <summary>
	    /// True if a headless build, such as a server build.
	    /// </summary>
	    public static bool IsHeadlessBuild() => default;
	}
}
