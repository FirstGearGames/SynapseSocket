using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Extensions
{
 public static partial class StringExtensions
	{
	    /// <summary>
	    /// non cryptographic stable hash code,
	    /// it will always return the same hash for the same
	    /// string.
	    /// This is simply an implementation of FNV-1 32 bit xor folded to 16 bit
	    /// https://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function
	    /// </summary>
	    /// <returns> The stable hash32. </returns>
	    /// <param name = "value"> Text. </param>
	    public static ushort GetStableHashUI16(this string value)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// non cryptographic stable hash code,
	    /// it will always return the same hash for the same
	    /// string.
	    /// This is simply an implementation of FNV-1 32 bit
	    /// https://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function
	    /// </summary>
	    /// <returns> The stable hash32. </returns>
	    /// <param name = "value"> Text. </param>
	    public static uint GetStableHashUI32(this string value)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// non cryptographic stable hash code,
	    /// it will always return the same hash for the same
	    /// string.
	    /// This is simply an implementation of FNV-1  64 bit
	    /// https://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function
	    /// </summary>
	    /// <returns> The stable hash32. </returns>
	    /// <param name = "value"> Text. </param>
	    public static ulong GetStableHashUInt64(this string value)
	    {
	        return default !;
	    }
	}
}
