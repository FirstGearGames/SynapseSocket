using CodeBoost.Types;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for a RingBuffer which is resettable.
	/// </summary>
	public static class ResettableRingBufferPool<T0>
	    where T0 : IPoolResettable, new()
	{
	    /// <summary>
	    /// Retrieves an instance of RingBuffer.
	    /// </summary>
	    public static RingBuffer<T0> Rent() => default;
	    /// <summary>
	    /// Stores an instance of RingBuffer and sets the original reference to null.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void ReturnAndNullifyReference(ref RingBuffer<T0>? value, PoolReturnType collectionReturnType)
	    {
	    }
	
	    /// <summary>
	    /// Stores an instance of RingBuffer.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void Return(RingBuffer<T0>? value, PoolReturnType collectionReturnType)
	    {
	    }
	}
}
