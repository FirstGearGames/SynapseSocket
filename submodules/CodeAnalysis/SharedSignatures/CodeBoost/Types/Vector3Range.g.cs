using System;
using CodeBoost.Mathematics;
using SystemVector3 = System.Numerics.Vector3;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct Vector3Range
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
	    /// Z range.
	    /// </summary>
	    public FloatRange Z;
	    /// <summary>
	    /// Creates ranges using minimum and maximum values for each axis.
	    /// </summary>
	    public Vector3Range(SystemVector3 minimum, SystemVector3 maximum)
	    {
	    }
	
	    /// <summary>
	    /// Creates ranges using minimum and maximum values.
	    /// </summary>
	    public Vector3Range(float minimum, float maximum)
	    {
	    }
	
	    /// <summary>
	    /// Returns a random value between Minimum and Maximum.
	    /// </summary>
	    /// <returns> </returns>
	    public SystemVector3 RandomInclusive()
	    {
	        return default !;
	    }
	}
}
