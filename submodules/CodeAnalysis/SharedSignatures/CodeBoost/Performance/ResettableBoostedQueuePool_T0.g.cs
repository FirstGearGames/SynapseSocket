using CodeBoost.Types;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for a BoostedQueue which is resettable.
	/// </summary>
	public static class ResettableBoostedQueuePool<T0>
	    where T0 : IPoolResettable, new()
	{
	    /// <summary>
	    /// Retrieves an instance of BoostedQueue.
	    /// </summary>
	    public static BoostedQueue<T0> Rent() => default;
	    /// <summary>
	    /// Stores an instance of BoostedQueue and sets the original reference to null.
	    /// </summary>
	    public static void ReturnAndNullifyReference(ref BoostedQueue<T0>? value, PoolReturnType collectionReturnType)
	    {
	    }
	
	    /// <summary>
	    /// Stores an instance of BoostedQueue.
	    /// </summary>
	    public static void Return(BoostedQueue<T0>? value, PoolReturnType collectionReturnType)
	    {
	    }
	}
}
