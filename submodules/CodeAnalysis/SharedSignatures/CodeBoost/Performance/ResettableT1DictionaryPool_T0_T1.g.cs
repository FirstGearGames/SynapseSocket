using System.Collections.Generic;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for a Dictionary which is resettable.
	/// </summary>
	public static class ResettableT1DictionaryPool<T0, T1>
	    where T1 : IPoolResettable, new()
	{
	    /// <summary>
	    /// Retrieves an instance of Dictionary.
	    /// </summary>
	    public static Dictionary<T0, T1> Rent() => default;
	    /// <summary>
	    /// Stores an instance of Dictionary and sets the original reference to null.
	    /// </summary>
	    public static void ReturnAndNullifyReference(ref Dictionary<T0, T1> value, PoolReturnType collectionReturnType)
	    {
	    }
	
	    /// <summary>
	    /// Stores an instance of Dictionary.
	    /// </summary>
	    public static void Return(Dictionary<T0, T1> value, PoolReturnType collectionReturnType)
	    {
	    }
	}
}
