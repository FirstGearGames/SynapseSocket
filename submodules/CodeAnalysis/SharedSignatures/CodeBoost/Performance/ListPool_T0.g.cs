using System.Collections.Generic;
using System.Threading;
using CodeBoost.Extensions;
using CodeBoost.Logging;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for List collections.
	/// </summary>
	public static class ListPool<T0>
	{
	    /// <summary>
	    /// Rents a List.
	    /// </summary>
	    /// <returns>A cleared List collection.</returns>
	    public static List<T0> Rent()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns a List and sets the provided reference to null;
	    /// This Method will not execute if the value is null.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void ReturnAndNullifyReference(ref List<T0>? value)
	    {
	    }
	
	    /// <summary>
	    /// Returns a List.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void Return(List<T0>? value)
	    {
	    }
	}
}
