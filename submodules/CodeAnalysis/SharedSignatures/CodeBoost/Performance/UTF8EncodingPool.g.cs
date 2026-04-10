using System.Collections.Generic;
using System.Text;
using CodeBoost.Extensions;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 /// <summary>
	/// A pool for a type which is not resettable.
	/// </summary>
	public static class UTF8EncodingPool
	{
	    /// <summary>
	    /// Returns a value from the stack or creates an instance when the stack is empty.
	    /// </summary>
	    /// <returns> </returns>
	    public static UTF8Encoding Rent()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Stores an instance of T0 and sets the original reference to default.
	    /// Method will not execute if value is null.
	    /// </summary>
	    /// <param name = "value"> Value to return. </param>
	    public static void ReturnAndNullifyReference(ref UTF8Encoding value)
	    {
	    }
	
	    /// <summary>
	    /// Stores a value to the stack.
	    /// </summary>
	    /// <param name = "value"> </param>
	    public static void Return(UTF8Encoding value)
	    {
	    }
	}
}
