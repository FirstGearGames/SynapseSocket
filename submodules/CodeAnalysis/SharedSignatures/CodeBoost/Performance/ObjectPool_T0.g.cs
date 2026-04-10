using System.Collections.Generic;
using System.Threading;
using CodeBoost.Extensions;
using CodeBoost.Logging;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for generic objects.
	/// </summary>
	public static class ObjectPool<T0>
	    where T0 : new()
	{
	    /// <summary>
	    /// Rents a generic object.
	    /// </summary>
	    /// <returns>A new or pooled instance of T0.</returns>
	    public static T0 Rent()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns a generic object and sets the provided reference to null;
	    /// This Method will not execute if the value is null.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void ReturnAndNullifyReference(ref T0? value)
	    {
	    }
	
	    /// <summary>
	    /// Returns a generic object.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void Return(T0? value)
	    {
	    }
	}
}
