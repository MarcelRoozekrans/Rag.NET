using System.Runtime.InteropServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PostRetrieval;

namespace Rag.NET.Storage;

/// <summary>
/// Thread-safe in-memory <see cref="IVectorStore"/> with learned sparse-vector support
/// (<see cref="ISparseSearchable"/>). Dense search scores by cosine similarity over a linear
/// scan; sparse search scores by dot product over an inverted postings index
/// (<c>term id → (slot, weight)</c>, mirroring <c>InMemoryBm25Index</c>).
/// Both sides are keyed by <c>(DocumentId, ChunkIndex)</c>: re-storing a chunk replaces its
/// vectors instead of duplicating them.
/// Intended for tests, samples, and small corpora — nothing is persisted.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore, ISparseSearchable, IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();

    // Dense side: (docId, chunkIndex) → chunk + embedding.
    private readonly Dictionary<(string DocId, int ChunkIndex), EmbeddedChunk> _dense = [];

    // Sparse side: slot registry + inverted postings (InMemoryBm25Index structure).
    private readonly Dictionary<(string DocId, int ChunkIndex), int> _sparseSlots = [];
    private readonly Dictionary<int, (EmbeddedChunk Chunk, SparseVector Sparse)> _slotEntries = [];
    private readonly Dictionary<int, List<(int Slot, float Weight)>> _postings = [];
    private int _nextSlot;

    /// <inheritdoc />
    public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();

        _lock.EnterWriteLock();
        try
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                _dense[(chunk.Chunk.DocumentId.Value, chunk.Chunk.ChunkIndex)] = chunk;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.TopK <= 0)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        _lock.EnterReadLock();
        try
        {
            var scored = new List<(double Score, EmbeddedChunk Entry)>(_dense.Count);
            foreach (var entry in _dense.Values)
            {
                if (!MatchesFilter(entry.Chunk, options.MetadataFilter))
                    continue;

                // Shared cosine helper: a dimension mismatch scores 0 (excluded whenever
                // MinScore > 0; included with score 0 under the default MinScore of 0).
                double score = EmbeddingMath.CosineSimilarity(queryEmbedding, entry.Embedding);
                if (score < options.MinScore)
                    continue;

                scored.Add((score, entry));
            }

            return Task.FromResult(TakeTop(scored, options.TopK));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        _lock.EnterWriteLock();
        try
        {
            RemoveKeysForDocument(_dense, documentId);

            var sparseKeys = new List<(string DocId, int ChunkIndex)>();
            foreach (var key in _sparseSlots.Keys)
            {
                if (string.Equals(key.DocId, documentId, StringComparison.Ordinal))
                    sparseKeys.Add(key);
            }

            foreach (ref readonly var key in CollectionsMarshal.AsSpan(sparseKeys))
            {
                var slot = _sparseSlots[key];
                RemoveSlotPostings(slot);
                _slotEntries.Remove(slot);
                _sparseSlots.Remove(key);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoreSparseAsync(
        IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();

        _lock.EnterWriteLock();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var (chunk, sparse) = items[i];
                if (sparse.Count == 0)
                    continue; // empty vector == no terms — nothing to index

                var key = (chunk.Chunk.DocumentId.Value, chunk.Chunk.ChunkIndex);
                if (_sparseSlots.TryGetValue(key, out var slot))
                {
                    RemoveSlotPostings(slot); // replace, don't duplicate
                }
                else
                {
                    slot = _nextSlot++;
                    _sparseSlots[key] = slot;
                }

                _slotEntries[slot] = (chunk, sparse);
                AddSlotPostings(slot, sparse);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> SearchSparseAsync(
        SparseVector query,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Count == 0 || options.TopK <= 0)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        _lock.EnterReadLock();
        try
        {
            var scores = AccumulateSparseScores(query);

            var scored = new List<(double Score, EmbeddedChunk Entry)>(scores.Count);
            foreach (var (slot, score) in scores)
            {
                if (score < options.MinScore)
                    continue;

                var entry = _slotEntries[slot];
                if (!MatchesFilter(entry.Chunk.Chunk, options.MetadataFilter))
                    continue;

                scored.Add((score, entry.Chunk));
            }

            return Task.FromResult(TakeTop(scored, options.TopK));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose() => _lock.Dispose();

    /// <summary>Dot product of the query against every slot sharing at least one term.</summary>
    private Dictionary<int, double> AccumulateSparseScores(SparseVector query)
    {
        var scores = new Dictionary<int, double>();
        var queryIndices = query.Indices.Span;
        var queryValues = query.Values.Span;
        for (var t = 0; t < queryIndices.Length; t++)
        {
            if (!_postings.TryGetValue(queryIndices[t], out var postingList))
                continue;

            var queryWeight = (double)queryValues[t];
            foreach (var (slot, weight) in CollectionsMarshal.AsSpan(postingList))
            {
                var contribution = queryWeight * weight;
                scores[slot] = scores.TryGetValue(slot, out var s) ? s + contribution : contribution;
            }
        }

        return scores;
    }

    private void AddSlotPostings(int slot, SparseVector sparse)
    {
        var indices = sparse.Indices.Span;
        var values = sparse.Values.Span;
        for (var t = 0; t < indices.Length; t++)
        {
            if (!_postings.TryGetValue(indices[t], out var list))
            {
                list = [];
                _postings[indices[t]] = list;
            }

            list.Add((slot, values[t]));
        }
    }

    /// <summary>Removes every posting for <paramref name="slot"/>, pruning emptied term lists.</summary>
    private void RemoveSlotPostings(int slot)
    {
        foreach (ref readonly var termId in _slotEntries[slot].Sparse.Indices.Span)
        {
            if (!_postings.TryGetValue(termId, out var list))
                continue;

            list.RemoveAll(entry => entry.Slot == slot);
            if (list.Count == 0)
                _postings.Remove(termId);
        }
    }

    private static void RemoveKeysForDocument(
        Dictionary<(string DocId, int ChunkIndex), EmbeddedChunk> map, string documentId)
    {
        var toRemove = new List<(string DocId, int ChunkIndex)>();
        foreach (var key in map.Keys)
        {
            if (string.Equals(key.DocId, documentId, StringComparison.Ordinal))
                toRemove.Add(key);
        }

        foreach (ref readonly var key in CollectionsMarshal.AsSpan(toRemove))
            map.Remove(key);
    }

    private static bool MatchesFilter(TextChunk chunk, IDictionary<string, MetadataValue>? filter)
    {
        if (filter is null || filter.Count == 0)
            return true;

        foreach (var (key, value) in filter)
        {
            // Typed equality: a Number 3 filter does not match a String "3" value.
            if (!chunk.Metadata.TryGetValue(key, out var actual) || actual != value)
                return false;
        }

        return true;
    }

    private static IReadOnlyList<SearchResult> TakeTop(
        List<(double Score, EmbeddedChunk Entry)> scored, int topK)
    {
        scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        var count = Math.Min(topK, scored.Count);
        var results = new List<SearchResult>(count);
        for (var i = 0; i < count; i++)
            results.Add(new SearchResult { Chunk = scored[i].Entry.Chunk, Score = scored[i].Score });

        return results;
    }
}
