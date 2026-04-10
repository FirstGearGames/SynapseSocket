using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Environment
{
 /// <summary>
	/// Provides application states for development within an IDE.
	/// </summary>
	public class IDEApplicationState : IApplicationState
	{
	    /// <summary>
	    /// Called when the application focus state changes.
	    /// </summary>
	    /// <remarks>Event never invokes for this type.</remarks>
	    public event IApplicationState.FocusChangeEventHandler FocusChanged;
	    /// <summary>
	    /// Returns the value of System.Environment.HasShutdownStarted. 
	    /// </summary>
	    /// <returns>System.Environment.HasShutdownStarted.</returns>
	    public bool IsQuitting() => default;
	    /// <summary>
	    /// Returns if IsQuitting is false.
	    /// </summary>
	    /// <returns>True if IsQuitting is false.</returns>
	    public bool IsPlaying() => default;
	    /// <summary>
	    /// Exits System.Environment.
	    /// </summary>
	    public void Quit()
	    {
	    }
	
	    /// <summary>
	    /// Unconditionally returns true.
	    /// </summary>
	    /// <returns>True.</returns>
	    public bool IsEditor() => default;
	    /// <summary>
	    /// Returns if the DEBUG preprocessor is active.
	    /// </summary>
	    /// <returns>Active state of Preprocessor DEBUG.</returns>
	    public bool IsDevelopment()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Unconditionally returns false.
	    /// </summary>
	    /// <returns>False.</returns>
	    public bool IsGUIBuild() => default;
	    /// <summary>
	    /// Unconditionally returns false.
	    /// </summary>
	    /// <returns>False.</returns>
	    public bool IsHeadlessBuild() => default;
	}
}
