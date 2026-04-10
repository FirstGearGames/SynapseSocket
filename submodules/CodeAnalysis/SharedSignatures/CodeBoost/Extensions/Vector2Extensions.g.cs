using System.Numerics;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Extensions
{
 public static class Vector2Extensions
	{
	    /// <summary>
	    /// Adds Vector3.X/Y to a Vector2.X/Y.
	    /// </summary>
	    public static System.Numerics.Vector2 AddVector3(this System.Numerics.Vector2 a, System.Numerics.Vector3 b) => default;
	    /// <summary>
	    /// Subtracts Vector3.X/Y from a Vector2.X/Y.
	    /// </summary>
	    public static System.Numerics.Vector2 SubtractVector2(this System.Numerics.Vector2 a, System.Numerics.Vector2 b) => default;
	    /// <summary>
	    /// True if the distance of two values are equal to or less than the tolerance.
	    /// </summary>
	    public static bool IsWithinDistance(this System.Numerics.Vector2 a, System.Numerics.Vector2 b, float tolerance = 0.01f) => default;
	    /// <summary>
	    /// True if the distance of two values are equal to or less than the tolerance.
	    /// </summary>
	    public static bool IsWithinSquaredDistance(this System.Numerics.Vector2 a, System.Numerics.Vector2 b, float tolerance = 0.01f) => default;
	    /// <summary>
	    /// True if any values within a Vector2 are NaN.
	    /// </summary>
	    public static bool IsNan(this System.Numerics.Vector2 value)
	    {
	        return default !;
	    }
	}
}
