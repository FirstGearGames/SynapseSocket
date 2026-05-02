using System;
using System.Collections.Generic;

namespace SynapseSocket.Core;

/// <summary>
/// Walks a read-only list in wrap-around batches, remembering the last returned index across calls.
/// Each call to <see cref="Enumerate"/> yields the next slice; once the end of the collection is reached the cursor wraps to zero.
/// Sized so that, when callers invoke at the matching cadence, the entire collection is visited once per sweep window.
/// </summary>
/// <typeparam name="T0">The element type held by the underlying collection.</typeparam>
public sealed class RoundRobinCursor<T0>
{
    /// <summary>
    /// The collection being walked. Held by reference; additions and removals are observed on the next <see cref="Enumerate"/> call.
    /// </summary>
    private readonly IReadOnlyList<T0> _collection;
    /// <summary>
    /// The total window, in milliseconds, over which one full pass of the collection should occur. Divides the collection count to produce the per-call batch size.
    /// </summary>
    private readonly int _sweepWindowMilliseconds;
    /// <summary>
    /// The index of the next element to yield. Wraps to zero when it reaches the collection count.
    /// </summary>
    private int _lastIndex;

    /// <summary>
    /// Creates a new cursor over <paramref name="collection"/>.
    /// </summary>
    /// <param name="collection">The collection to walk. The reference is retained; subsequent additions and removals are observed on the next call to <see cref="Enumerate"/>.</param>
    /// <param name="sweepWindowMilliseconds">The total window, in milliseconds, over which one full pass of the collection should occur. Used to derive the per-call batch size.</param>
    public RoundRobinCursor(IReadOnlyList<T0> collection, int sweepWindowMilliseconds)
    {
        if (collection is null)
            throw new ArgumentNullException(nameof(collection));

        if (sweepWindowMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sweepWindowMilliseconds), "Sweep window must be greater than zero.");

        _collection = collection;
        _sweepWindowMilliseconds = sweepWindowMilliseconds;
    }

    /// <summary>
    /// Yields the next wrap-around batch from the underlying collection.
    /// The batch size is the collection count divided by the sweep window, clamped to at least one element and at most the full collection count.
    /// </summary>
    /// <returns>The next batch of elements. Empty when the collection is empty.</returns>
    public IEnumerable<T0> Enumerate()
    {
        int collectionCount = _collection.Count;
        if (collectionCount == 0)
            yield break;

        int batchSize = Math.Max(1, collectionCount / _sweepWindowMilliseconds);
        if (batchSize > collectionCount)
            batchSize = collectionCount;

        for (int i = 0; i < batchSize; i++)
        {
            if (_lastIndex >= collectionCount)
                _lastIndex = 0;

            yield return _collection[_lastIndex++];
        }
    }
}
