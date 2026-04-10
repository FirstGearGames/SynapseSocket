using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// Implement to reset values when returning to a pool, as well to initialize when renting from a pool.
	/// </summary>
	public interface IPoolResettable
	{
		public void OnReturn();
		public void OnRent();
	}
}
