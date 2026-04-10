using System;
using System.Runtime.InteropServices;
using CodeBoost.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// Returns the memory size of a managed type.
	/// </summary>
	public static class ManagedSizeOf<T0>
	    where T0 : struct
	{
	    /// <summary>
	    /// Cached size value.
	    /// </summary>
	    // ReSharper disable once StaticMemberInGenericType
	    public static int Value;
	}
}
