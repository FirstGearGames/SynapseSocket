using System.Collections.Generic;
using CodeBoost.Logging;
using CodeBoost.Mathematics;
using CodeBoost.Performance;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 public static class Weighted
	{
	    /// <summary>
	    /// Random values by weight.
	    /// </summary>
	    /// <param name = "source"> values to pick from. </param>
	    /// <param name = "count"> Number of entries to get. </param>
	    /// <param name = "results"> Results of entries. Key is the entry, Value is the number of times the entry was picked. </param>
	    /// <param name = "allowRepeatingEntries"> True to allow the same entry to be picked more than once. </param>
	    public static void GetValues<T0>(List<T0> source, uint count, ref Dictionary<T0, uint> results, bool allowRepeatingEntries = false)
	        where T0 : IWeighted
	    {
	    }
	
	    /// <summary>
	    /// Random values by weight.
	    /// </summary>
	    /// <param name = "source"> values to pick from. </param>
	    /// <param name = "countRange"> Number of entries to get. </param>
	    /// <param name = "results"> Results of entries. Key is the entry, Value is the number of times the entry was picked. </param>
	    /// <param name = "allowRepeatingEntries"> True to allow the same entry to be picked more than once. </param>
	    public static void GetValues<T0>(List<T0> source, UIntRange countRange, ref Dictionary<T0, uint> results, bool allowRepeatingEntries = false)
	        where T0 : IWeighted
	    {
	    }
	}
}
