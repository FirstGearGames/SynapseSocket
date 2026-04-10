using System.Collections.Generic;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for a List which is resettable.
	/// </summary>
	public static class ResettableListPool<T0>
	    where T0 : IPoolResettable, new()
	{
	    /// <summary>
	    /// Retrieves an instance of T.
	    /// </summary>
	    public static List<T0> Rent() => default;
	    /// <summary>
	    /// Stores an instance of T0 and sets the original reference to default.
	    /// Method will not execute if value is null.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void ReturnAndNullifyReference(ref List<T0> value, PoolReturnType collectionReturnType)
	    {
	    }
	
	    /// <summary>
	    /// Stores an instance of T.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void Return(List<T0>? value, PoolReturnType collectionReturnType)
	    {
	    }
	}
}
