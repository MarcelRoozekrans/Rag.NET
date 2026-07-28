namespace Rag.NET.Evaluation.Internal;

/// <summary>
/// Reservoir sampling (Algorithm R) over a whole source that is already in memory.
/// </summary>
/// <remarks>
/// Convenience over <see cref="ReservoirSampler{T}"/> for callers that already hold the source —
/// tests, mostly. The dataset builder uses the streaming form, because materialising the corpus
/// to sample from it is the memory defect this replaces.
/// </remarks>
internal static class ReservoirSampler
{
    /// <summary>Selects up to <paramref name="count"/> items uniformly at random.</summary>
    /// <param name="source">The items to sample from.</param>
    /// <param name="count">How many to keep. Clamped to what <paramref name="source"/> holds.</param>
    /// <param name="random">
    /// The randomness. Seed it (<c>new Random(seed)</c>) and the same source yields the same
    /// selection; that reproducibility is the whole point of taking it as a parameter.
    /// </param>
    public static List<T> Sample<T>(IReadOnlyList<T> source, int count, Random random)
    {
        ArgumentNullException.ThrowIfNull(source);

        var reservoir = new ReservoirSampler<T>(count, random);

        // Indexed rather than foreach: no enumerator to allocate over the interface, and nothing
        // in the loop that ZA0601 would object to.
        for (var i = 0; i < source.Count; i++)
            reservoir.Offer(source[i]);

        return reservoir.ToList();
    }
}

/// <summary>
/// Keeps a uniform random sample of a stream whose length is not known in advance, holding at most
/// <c>capacity</c> items regardless of how long the stream is (Algorithm R).
/// </summary>
/// <remarks>
/// <para>
/// Offer items one at a time and the reservoir stays proportional to the sample rather than to the
/// corpus: sampling five chunks from a 100k-document corpus holds five chunks, not 100k documents.
/// </para>
/// <para>
/// Given a seeded <see cref="Random"/> the selection is reproducible, but only for a fixed
/// enumeration: the algorithm's decision at item <c>i</c> depends on every item before it, so a
/// source that enumerates in a different order selects differently even with the same seed.
/// </para>
/// </remarks>
/// <typeparam name="T">The item being sampled.</typeparam>
internal sealed class ReservoirSampler<T>
{
    private readonly int _capacity;
    private readonly Random _random;
    private readonly List<T> _reservoir;
    private int _seen;

    /// <summary>Creates an empty reservoir.</summary>
    /// <param name="capacity">The most items to keep. Zero keeps nothing.</param>
    /// <param name="random">The randomness; seed it for a reproducible selection.</param>
    public ReservoirSampler(int capacity, Random random)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentNullException.ThrowIfNull(random);

        _capacity = capacity;
        _random = random;
        _reservoir = new List<T>(capacity);
    }

    /// <summary>How many items are currently held. Never exceeds the capacity.</summary>
    public int Count => _reservoir.Count;

    /// <summary>Offers one item to the reservoir, which keeps or discards it.</summary>
    /// <param name="item">The candidate.</param>
    public void Offer(T item)
    {
        if (_capacity == 0)
        {
            _seen++;
            return;
        }

        if (_reservoir.Count < _capacity)
        {
            _reservoir.Add(item);
        }
        else
        {
            // Item _seen (0-based) survives with probability capacity/(_seen + 1), evicting a
            // uniformly chosen incumbent. That is what keeps every item equally likely without
            // knowing the stream's length.
            var slot = _random.Next(_seen + 1);
            if (slot < _capacity)
                _reservoir[slot] = item;
        }

        _seen++;
    }

    /// <summary>Copies out what was kept.</summary>
    public List<T> ToList() => [.. _reservoir];
}
