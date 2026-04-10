using System.Collections.Generic;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for a Queue which is resettable.
	/// </summary>
	public static class ResettableQueuePool<T0>
	    where T0 : IPoolResettable, new()
	{
	    /// <summary>
	    /// Retrieves an instance of Queue.
	    /// </summary>
	    public static Queue<T0> Rent() => default;
	    /// <summary>
	    /// Stores an instance of Queue and sets the original reference to null.
	    /// </summary>
	    public static void ReturnAndNullifyReference(ref Queue<T0>? value, PoolReturnType collectionReturnType)
	    {
	    }
	
	    /// <summary>
	    /// Stores an instance of Queue.
	    /// </summary>
	    public static void Return(Queue<T0>? value, PoolReturnType collectionReturnType)
	    {
	    }
	}
}
