using System;
using System.Numerics;
using CodeBoost.Mathematics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct Vector2Range
	{
	    /// <summary>
	    /// X range.
	    /// </summary>
	    public FloatRange X;
	    /// <summary>
	    /// Y range.
	    /// </summary>
	    public FloatRange Y;
	    /// <summary>
	    /// Creates ranges using minimum and maximum values for each axis.
	    /// </summary>
	    public Vector2Range(Vector2 minimum, Vector2 maximum)
	    {
	    }
	
	    /// <summary>
	    /// Creates ranges using minimum and maximum values.
	    /// </summary>
	    public Vector2Range(float minimum, float maximum)
	    {
	    }
	
	    /// <summary>
	    /// Returns a random value between Minimum and Maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public Vector2 RandomInclusive()
	    {
	        return default !;
	    }
	}
}
