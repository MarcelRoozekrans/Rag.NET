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
/// capability interfaces of the underlying stores are not federated. The one capability it
/// declares for itself is <see cref="IScoreScaleAware"/>: merged scores are RRF, so they are
/// <see cref="ScoreScale.OpaqueRanking"/>.
/// </remarks>
public sealed class FederatedVectorStore : IVectorStore, IScoreScaleAware
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

    /// <summary>
    /// Always <see cref="ScoreScale.OpaqueRanking"/>. Merged scores are Reciprocal Rank
    /// Fusion sums — <c>1 / (RrfK + rank)</c> per contributing store, so at most about
    /// <c>0.033</c> for two stores with the default <c>RrfK</c> of 60 — not similarities.
    /// Ranking them is meaningful; thresholding them is not. This is not delegated to the
    /// underlying stores: their scales are lost in fusion regardless of what they were.
    /// </summary>
    public ScoreScale ScoreScale => ScoreScale.OpaqueRanking;

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

    private Task<(IReadOnlyList<SearchResult>? Results, Exception? Error)[]> FanOutAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<(IReadOnlyList<SearchResult>? Results, Exception? Error)>[_stores.Count];
        for (int i = 0; i < _stores.Count; i++)
            tasks[i] = SearchStoreAsync(i, queryEmbedding, options, cancellationToken);
        return Task.WhenAll(tasks);
    }

    private async Task<(IReadOnlyList<SearchResult>? Results, Exception? Error)> SearchStoreAsync(
        int storeIndex,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await _stores[storeIndex].SearchAsync(queryEmbedding, options, cancellationToken).ConfigureAwait(false);
            return (results, null);
        }
        // Only the caller's cancellation aborts the federated search. A store-internal
        // OperationCanceledException (e.g. an HttpClient/gRPC deadline surfacing as
        // TaskCanceledException) is a per-store failure and falls through to the skip path.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger is not null)
                RagPipelineLog.FederatedStoreSearchFailed(_logger, StoreLabel(storeIndex), ex);
            return (null, ex);
        }
    }

    private void ThrowIfAllFailed((IReadOnlyList<SearchResult>? Results, Exception? Error)[] perStore)
    {
        foreach (var (results, _) in perStore)
        {
            if (results is not null) return;
        }

        var labels = new string[_stores.Count];
        for (int i = 0; i < _stores.Count; i++)
            labels[i] = StoreLabel(i);

        var errors = new List<Exception>(perStore.Length);
        foreach (var (_, error) in perStore)
        {
            if (error is not null) errors.Add(error);
        }

        throw new InvalidOperationException(
            $"All federated vector stores failed to serve the search: {string.Join(", ", labels)}.",
            new AggregateException(errors));
    }

    /// <summary>
    /// N-way RRF (RrfMerger semantics, generalised): each hit contributes
    /// <c>1 / (k + rank)</c> with 1-based per-store ranks; the chunk instance is kept from
    /// the first store that returned it. Ties on the merged score are broken
    /// deterministically: lower store index of first appearance wins, then lower per-store
    /// rank in that store.
    /// </summary>
    private IReadOnlyList<SearchResult> MergeByRrf((IReadOnlyList<SearchResult>? Results, Exception? Error)[] perStore, int topK)
    {
        if (topK <= 0) return [];
        int k = _options.RrfK;

        var rrfScores = new Dictionary<(string docId, int chunkIndex), double>();
        var firstHit = new Dictionary<(string docId, int chunkIndex), (TextChunk chunk, int storeIndex, int rank)>();

        for (int s = 0; s < perStore.Length; s++)
        {
            var hits = perStore[s].Results;
            if (hits is null) continue;
            for (int rank = 0; rank < hits.Count; rank++)
            {
                var chunk = hits[rank].Chunk;
                var key = ((string)chunk.DocumentId, chunk.ChunkIndex);
                var contrib = 1.0 / (k + rank + 1);
                rrfScores[key] = rrfScores.TryGetValue(key, out var existing) ? existing + contrib : contrib;
                firstHit.TryAdd(key, (chunk, s, rank));
            }
        }

        var sorted = new List<(double score, TextChunk chunk, int storeIndex, int rank)>(rrfScores.Count);
        foreach (var (key, score) in rrfScores)
        {
            var (chunk, storeIndex, rank) = firstHit[key];
            sorted.Add((score, chunk, storeIndex, rank));
        }
        sorted.Sort(static (a, b) =>
        {
            var byScore = b.score.CompareTo(a.score);
            if (byScore != 0) return byScore;
            var byStore = a.storeIndex.CompareTo(b.storeIndex);
            return byStore != 0 ? byStore : a.rank.CompareTo(b.rank);
        });

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
        var metadata = new Dictionary<string, MetadataValue>(chunk.Metadata, StringComparer.Ordinal)
        {
            [SourceStoreMetadataKey] = StoreLabel(storeIndex),
        };
        return chunk with { Metadata = metadata };
    }

    private string StoreLabel(int storeIndex) =>
        _options.StoreNames is { } names ? names[storeIndex] : storeIndex.ToString(CultureInfo.InvariantCulture);
}
