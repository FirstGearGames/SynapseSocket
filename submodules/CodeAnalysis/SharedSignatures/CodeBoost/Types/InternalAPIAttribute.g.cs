using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 /// <summary>
	/// Indicates feature is exposed for convenience but is primarily for internal use, and that usage may change without warning.
	/// </summary>
	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Struct)]
	public sealed class InternalAPIAttribute : Attribute
	{
	    public string Details;
	    public InternalAPIAttribute(string details = "") => Details = details;
	}
}
