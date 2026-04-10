using System.Collections.Generic;
using System.Threading;
using CodeBoost.Extensions;
using CodeBoost.Logging;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for SortedList collections.
	/// </summary>
	public static class SortedListPool<T0, T1>
	{
	    /// <summary>
	    /// Rents a SortedList.
	    /// </summary>
	    /// <returns>A cleared SortedList collection.</returns>
	    public static SortedList<T0, T1> Rent()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns a SortedList and sets the provided reference to null;
	    /// This Method will not execute if the value is null.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void ReturnAndNullifyReference(ref SortedList<T0, T1>? value)
	    {
	    }
	
	    /// <summary>
	    /// Returns a SortedList.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void Return(SortedList<T0, T1>? value)
	    {
	    }
	}
}
