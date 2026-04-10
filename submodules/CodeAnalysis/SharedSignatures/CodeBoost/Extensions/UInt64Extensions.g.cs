using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Extensions
{
 public static class UInt64Extensions
	{
	    /// <summary>
	    /// Returns how many packed bytes a number of bits require.
	    /// </summary>
	    /// <returns>Bytes required to pack bitCount.</returns>
	    /// <remarks>When a 0 <see cref="bitCount"/> is provided the returned value will also be 0.</remarks>
	    public static uint ToPackedByteCount(this ulong bitCount) => default;
	    /// <summary>
	    /// Converts a UInt64 to an Int64 using ZigZag encoding.
	    /// </summary>
	    /// <param name="value">Value to convert.</param>
	    public static long ConvertToInt64(this ulong value) => default;
	    /// <summary>
	    /// Returns if a flags whole value has part within it.
	    /// </summary>
	    public static bool FastContains(this ulong whole, ulong part) => default;
	}
}
