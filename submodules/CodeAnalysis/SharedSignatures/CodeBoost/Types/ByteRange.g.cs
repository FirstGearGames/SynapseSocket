using System;
using CodeBoost.Mathematics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct ByteRange
	{
	    public ByteRange(byte minimum, byte maximum)
	    {
	    }
	
	    /// <summary>
	    /// Minimum range.
	    /// </summary>
	    public byte Minimum;
	    /// <summary>
	    /// Maximum range.
	    /// </summary>
	    public byte Maximum;
	    /// <summary>
	    /// Returns an exclusive random value between Minimum and Maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public byte RandomExclusive() => default;
	    /// <summary>
	    /// Returns an inclusive random value between Minimum and Maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public byte RandomInclusive() => default;
	    /// <summary>
	    /// Clamps value between Minimum and Maximum.
	    /// </summary>
	    public byte Clamp(byte value) => default;
	    /// <summary>
	    /// True if value is within range of Minimum and Maximum.
	    /// </summary>
	    public bool InRange(byte value) => default;
	}
}
