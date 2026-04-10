using System;
using System.Collections.Generic;
using CodeBoost.Types;
using System.Threading.Tasks;
namespace CodeBoost.Extensions
{
 public static class ListExtensions
	{
	    /// <summary>
	    /// Adds an element to a collection if it does not exist already.
	    /// </summary>
	    /// <returns> True if an item was added.</returns>
	    public static bool AddUnique<T0>(this List<T0> list, T0 value)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Removes the first entry from the collection and returns it.
	    /// </summary>
	    /// <returns>First entry in the collection. Default if the collection is null or empty.</returns>
	    public static T0? RemoveAndReturnFirst<T0>(this List<T0> list)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Removes the last entry from the collection and returns it.
	    /// </summary>
	    /// <returns>Last entry in the collection. Default if the collection is null or empty.</returns>
	    public static T0 RemoveAndReturnLast<T0>(this List<T0> list)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Shuffles a collection.
	    /// </summary>
	    public static void Shuffle<T0>(this List<T0> lst)
	    {
	    }
	
	    /// <summary>
	    /// Adds an orderable item to a collection.
	    /// </summary>
	    public static void AddOrderedAscending<T0>(this List<T0> collection, T0 item)
	        where T0 : IOrderable
	    {
	    }
	}
}
