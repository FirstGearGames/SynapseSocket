using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 /// <summary>
	/// An array which belongs to an ArrayPool.
	/// </summary>
	public readonly struct RentedArray<T0> : IDisposable
	{
	    /// <summary>
	    /// Rented array.
	    /// </summary>
	    public readonly T0[] Array;
	    /// <summary>
	    /// Rents an array using a specified pool.
	    /// </summary>
	    /// <param name="pool">Pool to use.</param>
	    /// <param name="minimumLength">Minimum length the array must be.</param>
	    public RentedArray(ArrayPool<T0> pool, int minimumLength)
	    {
	    }
	
	    /// <summary>
	    /// Rents an array using the Shared pool.
	    /// </summary>
	    /// <param name="minimumLength">Minimum length the array must be.</param>
	    public RentedArray(int minimumLength) : this(ArrayPool<T0>.Shared, minimumLength)
	    {
	    }
	
	    public void Dispose()
	    {
	    }
	}
}
