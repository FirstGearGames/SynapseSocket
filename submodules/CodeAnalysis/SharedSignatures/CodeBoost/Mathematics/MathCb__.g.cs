using System.Globalization;
using CodeBoost.Types;
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
	    /// Pads the whole value of a double by a max number of indexes.
	    /// </summary>
	    /// <returns> </returns>
	    public static string PadLeft(double value, int padding)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// A random value between a minimum and inclusive maximum.
	    /// </summary>
	    /// <returns> </returns>
	    /// <remarks>Maximum value is padded with double.Epsilon to achieve the closest possibility to inclusive.</remarks>
	    public static double RandomInclusive(double minimum, double maximum)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// A random value between a minimum and exclusive maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public static double RandomExclusive(double minimum, double maximum)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Clamps a value between 0 and 1.
	    /// </summary>
	    public static double Clamp01(double value)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns a random value between 0f and 1f.
	    /// </summary>
	    /// <returns> </returns>
	    public static double Random01() => default;
	    /// <summary>
	    /// Interpolates between start and end using a percentage.
	    /// </summary>
	    public static double Lerp(double start, double end, double percentage)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns if values are within tolerance of each other.
	    /// </summary>
	    public static bool IsApproximately(this double a, double b, double tolerance = 0.00001d) => default;
	    /// <summary>
	    /// Returns the sign of a value as -1 or 1.
	    /// </summary>
	    /// >
	    /// <remarks>A value of 0 will return 1.</remarks>
	    public static double NonZeroSign(double value) => default;
	    /// <summary>
	    /// True if a value is within inclusive range of minimum and maximum.
	    /// </summary>
	    public static bool IsBetweenInclusive(double value, double minimum, double maximum) => default;
	    /// <summary>
	    /// Randomly inverts a value between positive and negative sign.
	    /// </summary>
	    public static double RandomSign(this float value)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// True if all values are within tolerance of each other.
	    /// </summary>
	    /// <remarks>True is returned if values are null or empty.</remarks>
	    public static bool AreValuesMatching(double[] values)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Converts a double to an Int64.
	    /// </summary>
	    /// <param name="value">Value to convert.</param>
	    /// <param name="accuracy">Accuracy to use for decimals. This value is typically less than 1f.</param>
	    public static long DoubleToInt64Unsafe(double value, float accuracy)
	    {
	        return default !;
	    }
	}
}
