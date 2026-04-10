using System;
using CodeBoost.Mathematics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct IntRange
	{
	    public IntRange(int minimum, int maximum)
	    {
	    }
	
	    /// <summary>
	    /// Minimum range.
	    /// </summary>
	    public int Minimum;
	    /// <summary>
	    /// Maximum range.
	    /// </summary>
	    public int Maximum;
	    /// <summary>
	    /// Returns an exclusive random value between Minimum and Maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public int RandomExclusive() => default;
	    /// <summary>
	    /// Returns an inclusive random value between Minimum and Maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public int RandomInclusive() => default;
	    /// <summary>
	    /// Clamps a value between minimum and maximum.
	    /// </summary>
	    /// <returns>Clamped value.</returns>
	    public int Clamp(int value) => default;
	}
}
