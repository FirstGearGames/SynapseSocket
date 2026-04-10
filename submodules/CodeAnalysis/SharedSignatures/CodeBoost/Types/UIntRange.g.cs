using System;
using CodeBoost.Mathematics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct UIntRange
	{
	    public UIntRange(uint minimum, uint maximum)
	    {
	    }
	
	    /// <summary>
	    /// Minimum range.
	    /// </summary>
	    public uint Minimum;
	    /// <summary>
	    /// Maximum range.
	    /// </summary>
	    public uint Maximum;
	    /// <summary>
	    /// Returns an exclusive random value between Minimum and Maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public uint RandomExclusive() => default;
	    /// <summary>
	    /// Returns an inclusive random value between Minimum and Maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public uint RandomInclusive() => default;
	    /// <summary>
	    /// Clamps a value between Minimum and Maximum.
	    /// </summary>
	    public uint Clamp(uint value) => default;
	    /// <summary>
	    /// True if value is within range of Minimum and Maximum.
	    /// </summary>
	    public bool InRange(uint value) => default;
	}
}
