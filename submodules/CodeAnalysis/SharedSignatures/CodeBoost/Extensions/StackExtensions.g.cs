using System.Collections.Generic;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Extensions
{
 public static class StackExtensions
	{
	#if NETSTANDARD2_0 || NETSTANDARD1_6
	        public static bool TryPop<T0>(this Stack<T0> stack, out T0 result)
	        {
	            bool isEmpty = stack.Count == 0;
	            result = isEmpty ? default : stack.Pop();
	
	            return !isEmpty;
	        }
	#endif
	}
}
