using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using CodeBoost.Logging;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 /// <summary>
	/// Writes values to a collection of a set size, overwriting old values as needed.
	/// </summary>
	public class RingBuffer<T0>
	{
	    /// <summary>
	    /// Current write index of the collection.
	    /// </summary>
	    public int WriteIndex;
	    /// <summary>
	    /// Number of entries currently written.
	    /// </summary>
	    public int Count;
	    /// <summary>
	    /// Maximum size of the collection.
	    /// </summary>
	    public int Capacity;
	    /// <summary>
	    /// Collection being used.
	    /// </summary>
	    public T0[] Collection = new T0[0];
	    /// <summary>
	    /// True if initialized.
	    /// </summary>
	    public bool Initialized;
	    /// <summary>
	    /// Default capacity when none is psecified.
	    /// </summary>
	    public const int DEFAULT_CAPACITY = 60;
	    /// <summary>
	    /// Initializes with default capacity.
	    /// </summary>
	    public RingBuffer()
	    {
	    }
	
	    /// <summary>
	    /// Initializes with a set capacity.
	    /// </summary>
	    /// <param name = "capacity"> Size to initialize the collection as. This cannot be changed after initialized. </param>
	    public RingBuffer(int capacity)
	    {
	    }
	
	    /// <summary>
	    /// Initializes the collection at length.
	    /// </summary>
	    /// <param name = "capacity"> Size to initialize the collection as. This cannot be changed after initialized. </param>
	    public void Initialize(int capacity)
	    {
	    }
	
	    /// <summary>
	    /// Initializes with default capacity.
	    /// </summary>
	    /// <param name = "log"> True to log automatic initialization. </param>
	    public void Initialize()
	    {
	    }
	
	    /// <summary>
	    /// Clears the collection to default values and resets indexing.
	    /// </summary>
	    public void Clear()
	    {
	    }
	
	    /// <summary>
	    /// Inserts an entry into the collection.
	    /// This is can be an expensive operation on larger buffers.
	    /// </summary>
	    /// <param name = "simulatedIndex"> Simulated index to return. A value of 0 would return the first simulated index in the collection. </param>
	    /// <param name = "data"> Data to insert. </param>
	    public T0 Insert(int simulatedIndex, T0 data)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Adds an entry to the collection, returning a replaced entry.
	    /// </summary>
	    /// <param name = "data"> Data to add. </param>
	    /// <returns> Replaced entry. Value will be default if no entry was replaced. </returns>
	    public T0 Add(T0 data)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns the first entry and removes it from the buffer.
	    /// </summary>
	    /// <returns> </returns>
	    public T0 Dequeue()
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns if able to dequeue an entry and removes it from the buffer if so.
	    /// </summary>
	    /// <returns> </returns>
	    public bool TryDequeue(T0 result)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Adds an entry to the collection, returning a replaced entry.
	    /// This method internally redirects to add.
	    /// </summary>
	    public T0 Enqueue(T0 data) => default;
	    /// <summary>
	    /// Removes values from the simulated start of the collection.
	    /// </summary>
	    /// <param name = "fromStart"> True to remove from the start, false to remove from the end. </param>
	    /// <param name = "length"> Number of entries to remove. </param>
	    public void RemoveRange(bool fromStart, int length)
	    {
	    }
	}
}
