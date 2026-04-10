using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Mathematics
{
 public enum RoundingType
	{
	    /// <summary>
	    /// To the nearest even number.
	    /// </summary>
	    /// <remarks>When the floating value is 0.5 the number will be rounded down.</remarks>
	    ToEven = 0,
	    /// <summary>
	    /// To the nearest even number, rounding up if the floating value is 0.5.
	    /// </summary>
	    AwayFromZero = 1,
	    /// <summary>
	    /// Rounds down regardless of the floating value.
	    /// </summary>
	    Down,
	    /// <summary>
	    /// Rounds up regardless of the floating value.
	    /// </summary>
	    Up,
	    /// <summary>
	    /// Rounds up regardless of the floating value, or to 1 when the number is 0.
	    /// </summary>
	    UpNonZero,
	}
}
