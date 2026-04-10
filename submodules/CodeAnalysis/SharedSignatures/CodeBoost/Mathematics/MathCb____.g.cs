using System;
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
	    /// <param name="valueA">The number which is divided.</param>
	    /// <param name="valueB">The divisor.</param>
	    /// <param name="midpointRounding">Rounding type to use.</param>
	    public static long Divide(long valueA, long valueB, MidpointRounding midpointRounding = MidpointRounding.ToEven)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Pads an index a specified value. Preferred over typical padding so that pad values used with skins can be easily found in the code.
	    /// </summary>
	    public static string Pad(this long value, int padding)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns a clamped long within a specified range.
	    /// </summary>
	    /// <param name = "value"> Value to clamp. </param>
	    /// <param name = "minimum"> Minimum value. </param>
	    /// <param name = "maximum"> Maximum value. </param>
	    /// <returns> </returns>
	    public static long Clamp(long value, long minimum, long maximum)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns whichever value is lower.
	    /// </summary>
	    public static long Min(long a, long b) => default;
	    /// <summary>
	    /// Determines if all values passed in are the same.
	    /// </summary>
	    /// <param name = "values"> Values to check. </param>
	    /// <returns> True if all values are the same. </returns>
	    public static bool AreValuesMatching(long[] values)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Converts an Int64 to a Single.
	    /// </summary>
	    /// <param name="value">Value to convert.</param>
	    /// <param name="accuracy">Accuracy to use for decimals. This value is typically less than 1f.</param>
	    public static double Int64ToSingleUnsafe(double value, float accuracy)
	    {
	        return default !;
	    }
	}
}
