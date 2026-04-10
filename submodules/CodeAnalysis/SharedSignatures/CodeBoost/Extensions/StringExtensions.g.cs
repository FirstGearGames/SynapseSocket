using System.Text;
using CodeBoost.Performance;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Extensions
{
 public static partial class StringExtensions
	{
	    /// <summary>
	    /// Value representing when an index is not found or specified.
	    /// </summary>
	    public const int UnsetIndex = -1;
	    /// <summary>
	    /// Converts a member string text to PascalCase
	    /// </summary>
	    /// <remarks>Leading non-alpha characters are removed and the first alpha character is capitalized.</remarks>
	    public static string MemberToPascalCase(this string value)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Converts a pascal case string to member case with an optional prefix.
	    /// </summary>
	    /// <example>With a prefix of '_' value 'HelloWorld' is returned as '_helloWorld'.</example>
	    /// <remarks>Prefix is only added if missing.</remarks>
	    public static string PascalCaseToMember(this string value, string prefix = "_")
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Converts a string into a byte array.
	    /// </summary>
	    /// <returns>Number of bytes written to buffer.</returns>
	    /// <remarks>Buffer is instantiated as a new array if it is not large enough.</remarks>
	    public static byte[] ToBytesNonAllocated(this string value, int bytesWritten)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns index of the first letter or number in a string.
	    /// </summary>
	    public static int GetFirstLetterOrDigitIndex(this string value)
	    {
	        return default !;
	    }
	}
}
