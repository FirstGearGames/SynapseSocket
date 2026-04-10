using System;
using System.Collections.Generic;
using System.Text;
using CodeBoost.Performance;
using System.Threading.Tasks;
namespace CodeBoost.Extensions
{
 public static class IEnumerableExtensions
	{
	    /// <summary>
	    /// Cast each item in the collection ToString and returns all values.
	    /// </summary>
	    /// <returns> </returns>
	    public static string ToString<T0>(this IEnumerable<T0> thisValue, string delimiter = ", ")
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Calls Disposes on elements within a collection.
	    /// </summary>
	    public static void Dispose<T0>(this IEnumerable<T0> thisValue)
	        where T0 : IDisposable
	    {
	    }
	}
}
