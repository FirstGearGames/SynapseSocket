using System;
using CodeBoost.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Mathematics
{
 /// <summary>
	/// Various utility classes relating to Float.
	/// </summary>
	public static partial class MathCb
	{
	    /// <summary>
	    /// Divides a value by another and rounds the result.
	    /// </summary>
	    /// <param name="value">The number which is divided.</param>
	    /// <param name="divisor">The divisor.</param>
	    /// <param name="roundingType">Rounding type to use.</param>
	    public static ulong Divide(this ulong value, ulong divisor, RoundingType roundingType)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Pads an index a specified value. Preferred over typical padding so that pad values used with skins can be easily found in the code.
	    /// </summary>
	    public static string Pad(this ulong value, int padding)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns a clamped ulong within a specified range.
	    /// </summary>
	    /// <param name = "value"> Value to clamp. </param>
	    /// <param name = "minimum"> Minimum value. </param>
	    /// <param name = "maximum"> Maximum value. </param>
	    /// <returns> </returns>
	    public static ulong Clamp(ulong value, ulong minimum, ulong maximum)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns whichever value is lower.
	    /// </summary>
	    public static ulong Min(ulong a, ulong b) => default;
	    /// <summary>
	    /// Determines if all values passed in are the same.
	    /// </summary>
	    /// <param name = "values"> Values to check. </param>
	    /// <returns> True if all values are the same. </returns>
	    public static bool AreValuesMatching(ulong[] values)
	    {
	        return default !;
	    }
	}
}
