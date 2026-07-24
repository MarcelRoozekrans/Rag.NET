using System.Globalization;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Storage;

/// <summary>
/// Federates multiple <see cref="IVectorStore"/> instances behind a single store.
/// Searches fan out to all stores concurrently and are merged with Reciprocal Rank
/// Fusion; writes and deletes go to the primary store only
/// (<see cref="FederatedStoreOptions.PrimaryIndex"/>) — secondary stores are never
/// modified. A failing store is skipped with a warning; the search only throws when
/// every store failed.
/// </summary>
/// <remarks>
/// Each merged result's <see cref="TextChunk.Metadata"/> gains a <c>source.store</c>
/// entry naming the store it came from (<see cref="FederatedStoreOptions.StoreNames"/>
/// or the store index). Federation is dense-only: <c>IHybridSearchable</c> and other
/// capability interfaces of the underlying stores are not federated.
/// </remarks>
public sealed class FederatedVectorStore : IVectorStore
{
    private const string SourceStoreMetadataKey = "source.store";

    private readonly IReadOnlyList<IVectorStore> _stores;
    private readonly FederatedStoreOptions _options;
    private readonly ILogger<FederatedVectorStore>? _logger;

    public FederatedVectorStore(
        IReadOnlyList<IVectorStore> stores,
        FederatedStoreOptions options,
        ILogger<FederatedVectorStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(options);
        if (stores.Count < 2)
        {
            throw new ArgumentException(
                $"Federation requires at least 2 stores, got {stores.Count}. Use the store directly instead.",
                nameof(stores));
        }
        if (options.PrimaryIndex < 0 || options.PrimaryIndex >= stores.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.PrimaryIndex,
                $"{nameof(FederatedStoreOptions)}.{nameof(FederatedStoreOptions.PrimaryIndex)} must be within [0, {stores.Count - 1}].");
        }
        if (options.RrfK < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.RrfK,
                $"{nameof(FederatedStoreOptions)}.{nameof(FederatedStoreOptions.RrfK)} must be at least 1.");
        }
        if (options.StoreNames is { } names && names.Count != stores.Count)
        {
            throw new ArgumentException(
                $"{nameof(FederatedStoreOptions)}.{nameof(FederatedStoreOptions.StoreNames)} has {names.Count} name(s) for {stores.Count} store(s); counts must match.",
                nameof(options));
        }

        _stores = stores;
        _options = options;
        _logger = logger;
    }

    /// <summary>Stores the chunks in the primary store only.</summary>
    public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
        _stores[_options.PrimaryIndex].StoreAsync(chunks, cancellationToken);

    /// <summary>Deletes from the primary store only — secondary stores are not touched.</summary>
    public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
        _stores[_options.PrimaryIndex].DeleteByDocumentIdAsync(documentId, cancellationToken);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var perStore = await FanOutAsync(queryEmbedding, options, cancellationToken).ConfigureAwait(false);
        ThrowIfAllFailed(perStore);
        return MergeByRrf(perStore, options.TopK);
    }

    private Task<IReadOnlyList<SearchResult>?[]> FanOutAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<IReadOnlyList<SearchResult>?>[_stores.Count];
        for (int i = 0; i < _stores.Count; i++)
            tasks[i] = SearchStoreAsync(i, queryEmbedding, options, cancellationToken);
        return Task.WhenAll(tasks);
    }

    private async Task<IReadOnlyList<SearchResult>?> SearchStoreAsync(
        int storeIndex,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _stores[storeIndex].SearchAsync(queryEmbedding, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger is not null)
                RagPipelineLog.FederatedStoreSearchFailed(_logger, StoreLabel(storeIndex), ex);
            return null;
        }
    }

    private void ThrowIfAllFailed(IReadOnlyList<SearchResult>?[] perStore)
    {
        foreach (var storeResults in perStore)
        {
            if (storeResults is not null) return;
        }

        var labels = new string[_stores.Count];
        for (int i = 0; i < _stores.Count; i++)
            labels[i] = StoreLabel(i);
        throw new InvalidOperationException(
            $"All federated vector stores failed to serve the search: {string.Join(", ", labels)}. See warning logs for the per-store errors.");
    }

    private IReadOnlyList<SearchResult> MergeByRrf(IReadOnlyList<SearchResult>?[] perStore, int topK)
    {
        if (topK <= 0) return [];
        int k = _options.RrfK;

        // N-way RRF (RrfMerger semantics, generalised): contribution 1/(k + rank) with
        // 1-based ranks; the chunk instance is kept from the first store that returned it.
        var rrfScores = new Dictionary<(string docId, int chunkIndex), double>();
        var firstHit = new Dictionary<(string docId, int chunkIndex), (TextChunk chunk, int storeIndex)>();

        for (int s = 0; s < perStore.Length; s++)
        {
            var hits = perStore[s];
            if (hits is null) continue;
            for (int rank = 0; rank < hits.Count; rank++)
            {
                var chunk = hits[rank].Chunk;
                var key = ((string)chunk.DocumentId, chunk.ChunkIndex);
                var contrib = 1.0 / (k + rank + 1);
                rrfScores[key] = rrfScores.TryGetValue(key, out var existing) ? existing + contrib : contrib;
                firstHit.TryAdd(key, (chunk, s));
            }
        }

        var sorted = new List<(double score, TextChunk chunk, int storeIndex)>(rrfScores.Count);
        foreach (var (key, score) in rrfScores)
        {
            var (chunk, storeIndex) = firstHit[key];
            sorted.Add((score, chunk, storeIndex));
        }
        sorted.Sort(static (a, b) => b.score.CompareTo(a.score));

        var count = Math.Min(topK, sorted.Count);
        var results = new List<SearchResult>(count);
        for (int i = 0; i < count; i++)
            results.Add(new SearchResult { Chunk = TagSource(sorted[i].chunk, sorted[i].storeIndex), Score = sorted[i].score });
        return results;
    }

    /// <summary>
    /// Returns a copy of the chunk with <c>source.store</c> stamped into a fresh metadata
    /// dictionary — the source store's chunk (and its metadata) is never mutated.
    /// </summary>
    private TextChunk TagSource(TextChunk chunk, int storeIndex)
    {
        var metadata = new Dictionary<string, string>(chunk.Metadata, StringComparer.Ordinal)
        {
            [SourceStoreMetadataKey] = StoreLabel(storeIndex),
        };
        return chunk with { Metadata = metadata };
    }

    private string StoreLabel(int storeIndex) =>
        _options.StoreNames is { } names ? names[storeIndex] : storeIndex.ToString(CultureInfo.InvariantCulture);
}
