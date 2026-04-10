using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Performance
{
 public class ThreadLocalStackWrapper<TObject>
	{
	    /// <summary>
	    /// Stack for the ThreadLocal.
	    /// </summary>
	    public readonly Stack<TObject> LocalStack = [];
	    public ThreadLocalStackWrapper(Action<Stack<TObject>> onFinalize)
	    {
	    }
	}
}
