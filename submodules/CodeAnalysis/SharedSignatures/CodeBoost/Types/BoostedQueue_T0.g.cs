using CodeBoost.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 /// <summary>
	/// Unity 2022 has a bug where codegen will not compile when referencing a Queue type,
	/// while also targeting .Net as the framework API.
	/// As a work around this class is used for queues instead.
	/// </summary>
	public class BoostedQueue<T0>
	{
	    /// <summary>
	    /// Maximum size of the collection.
	    /// </summary>
	    public int Capacity;
	    /// <summary>
	    /// Number of elements in the queue.
	    /// </summary>
	    public int Count;
	    /// <summary>
	    /// Current write index of the collection.
	    /// </summary>
	    public int WriteIndex;
	    /// <summary>
	    /// Enqueues an entry.
	    /// </summary>
	    /// <param name = "data"> </param>
	    public void Enqueue(T0 data)
	    {
	    }
	
	    /// <summary>
	    /// Tries to dequeue the next entry.
	    /// </summary>
	    /// <param name = "result"> Dequeued entry. </param>
	    /// <param name = "defaultArrayEntry"> True to set the array entry as default. </param>
	    /// <returns> True if an entry existed to dequeue. </returns>
	    public bool TryDequeue(T0 result, bool defaultArrayEntry = true)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Dequeues the next entry.
	    /// </summary>
	    /// <param name = "defaultArrayEntry"> True to set the array entry as default. </param>
	    public T0 Dequeue(bool defaultArrayEntry = true)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Tries to peek the next entry.
	    /// </summary>
	    /// <param name = "result"> Peeked entry. </param>
	    /// <returns> True if an entry existed to peek. </returns>
	    public bool TryPeek(T0 result)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Peeks the next queue entry.
	    /// </summary>
	    /// <returns> </returns>
	    public T0 Peek()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns an entry at index or default if index is invalid.
	    /// </summary>
	    public T0 GetIndexOrDefault(int simulatedIndex)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Clears the queue.
	    /// </summary>
	    public void Clear()
	    {
	    }
	}
}
