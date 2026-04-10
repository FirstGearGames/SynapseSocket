using System.Collections.Generic;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for a HashSet which is resettable.
	/// </summary>
	public static class ResettableHashSetPool<T0>
	    where T0 : IPoolResettable, new()
	{
	    /// <summary>
	    /// Retrieves an instance of HashSet.
	    /// </summary>
	    public static HashSet<T0> Rent() => default;
	    /// <summary>
	    /// Stores an instance of HashSet and sets the original reference to null.
	    /// </summary>
	    public static void ReturnAndNullifyReference(ref HashSet<T0>? value, PoolReturnType collectionReturnType)
	    {
	    }
	
	    /// <summary>
	    /// Stores an instance of HashSet.
	    /// </summary>
	    public static void Return(HashSet<T0>? value, PoolReturnType collectionReturnType)
	    {
	    }
	}
}
