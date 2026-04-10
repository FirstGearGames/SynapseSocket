using System;
using CodeBoost.Mathematics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct FloatRange
	{
	    public FloatRange(float minimum, float maximum)
	    {
	    }
	
	    /// <summary>
	    /// Minimum range.
	    /// </summary>
	    public float Minimum;
	    /// <summary>
	    /// Maximum range.
	    /// </summary>
	    public float Maximum;
	    /// <summary>
	    /// Returns a random value between Minimum and Maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public float RandomInclusive() => default;
	    /// <summary>
	    /// Interpolates between minimum and maximum using a percentage.
	    /// </summary>
	    public float Lerp(float percentage) => default;
	}
}
