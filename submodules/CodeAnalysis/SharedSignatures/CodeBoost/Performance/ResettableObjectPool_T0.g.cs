using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for generic objects which are resettable.
	/// </summary>
	public static class ResettableObjectPool<T0>
	    where T0 : IPoolResettable, new()
	{
	    /// <summary>
	    /// Retrieves an instance of T0.
	    /// </summary>
	    public static T0 Rent()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Stores an instance of T0 and sets the original reference to default.
	    /// Method will not execute if value is null.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void ReturnAndNullifyReference(ref T0? value)
	    {
	    }
	
	    /// <summary>
	    /// Stores an instance of T0.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void Return(T0? value)
	    {
	    }
	}
}
