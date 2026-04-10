using System.Collections.Generic;
using CodeBoost.Performance;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Extensions
{
 public static class DictionaryExtensions
	{
	    /// <summary>
	    /// Returns values as a list.
	    /// </summary>
	    /// <remarks>The returned list is taken from a collection pool.</remarks>
	    public static List<TValue> ValuesToList<TKey, TValue>(this IDictionary<TKey, TValue> dict)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Clears a list and populates it with the values of a dictionary.
	    /// </summary>
	    public static void ValuesToList<TKey, TValue>(this IDictionary<TKey, TValue> dict, ref List<TValue> result)
	    {
	    }
	
	    /// <summary>
	    /// Returns keys as a list.
	    /// </summary>
	    /// <remarks>The returned list is taken from a collection pool.</remarks>
	    public static List<TValue> KeysToList<TKey, TValue>(this IDictionary<TKey, TValue> dict)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Clears a list and populates it with the keys of a dictionary.
	    /// </summary>
	    public static void KeysToList<TKey, TValue>(this IDictionary<TKey, TValue> dict, ref List<TKey> result)
	    {
	    }
	}
}
