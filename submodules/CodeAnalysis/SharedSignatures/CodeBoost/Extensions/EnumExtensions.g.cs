using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CodeBoost.CodeAnalysis;
using CodeBoost.Logging;
using System.Threading.Tasks;

namespace CodeBoost.Extensions
{
 public static class EnumExtensions
	{
	    /// <summary>
	    /// Returns the enum name and value as a string.
	    /// </summary>
	    /// <example>MyEnum.Two</example>
	    public static string ToTypeAndValueString<T0>(this T0 enumValue, bool useFullName) where T0 : Enum
	    {
		    Type type = typeof(T0);
		    
		    string name = useFullName ? type.FullName : type.Name;
		    return $"{name}.{enumValue}";
	    }
	    
	    /// <summary>
	    /// Gets the lowest and highest values for an enum of underlying type.
	    /// </summary>
	    /// <remarks>True if was able to retrieve the values.</remarks>
	    public static bool TryGetLowestAndHighestSignedValues<T0>(long lowestValue, long highestValue)
	        where T0 : Enum
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Gets the lowest and highest values for an enum of underlying type.
	    /// </summary>
	    /// <remarks>True if was able to retrieve the values.</remarks>
	    public static bool TryGetLowestAndHighestUnsignedValues<T0>(ulong lowestValue, ulong highestValue)
	        where T0 : Enum
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Gets all values for an enum.
	    /// </summary>
	    public static T0[] GetValuesAllocated<T0>()
	        where T0 : Enum
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Gets all values for an enum in ascending numeric order.
	    /// </summary>
	    public static T0[] GetValuesAscendingAllocated<T0>()
	        where T0 : Enum
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Gets all values for an enum in descending numeric order.
	    /// </summary>
	    public static T0[] GetValuesDescendingAllocated<T0>()
	        where T0 : Enum
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns if the value contains any of the provided flags without safety checks.
	    /// </summary>
	    /// <remarks>Type comparison is not checked.</remarks>
	    public static int GetValuesCount<T0>()
	        where T0 : Enum => default;
	    /// <summary>
	    /// Gets the underlying type for an Enum.
	    /// </summary>
	    public static Type GetUnderlyingType<T0>()
	        where T0 : Enum => default;
	    /// <summary>
	    /// Checks if the underlying type is of expected value.
	    /// </summary>
	    public static bool TryValidateUnderlyingType<T0>(Type expectedValue)
	        where T0 : Enum
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns if the value contains any of the provided flags without safety checks.
	    /// </summary>
	    /// <remarks>Type comparison is not checked.</remarks>
	    public static bool HasAnyFlagAllocated<T0>(this T0 thisValue, T0 flagsToCheck)
	        where T0 : Enum
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns if the value contains any of the provided flags without safety checks.
	    /// </summary>
	    /// <remarks>Type comparison is not checked.</remarks>
	    public static bool HasAnyFlagUnsafe<T0>(this T0 thisValue, T0 flagsToCheck)
	        where T0 : Enum
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Converts a value to a UInt64 using an approach optimized over Convert.
	    /// </summary>
	    public static bool ToUInt64Unsafe<T0>(this T0 thisValue, ulong result)
	        where T0 : Enum
	    {
	        return default !;
	    }
	}
}
