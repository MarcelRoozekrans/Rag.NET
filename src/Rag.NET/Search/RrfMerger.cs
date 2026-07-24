using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Search;

/// <summary>
/// Reciprocal Rank Fusion: score(d) = Σ weight_i/(k + rank_i), k=60 (1-based ranks).
/// </summary>
internal static class RrfMerger
{
    private const int DefaultK = 60;

    internal static IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<SearchResult> dense,
        IReadOnlyList<(TextChunk chunk, double score)> bm25Hits,
        int topK)
    {
        return MergeMany([(dense, 1.0), (ToSearchResults(bm25Hits), 1.0)], topK, DefaultK);
    }

    internal static IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<SearchResult> dense,
        IReadOnlyList<(TextChunk chunk, double score)> bm25Hits,
        int topK,
        EnsembleOptions options)
    {
        return MergeMany(
            [(dense, options.DenseWeight), (ToSearchResults(bm25Hits), options.Bm25Weight)],
            topK,
            options.K);
    }

    /// <summary>
    /// N-way weighted RRF: each ranking contributes <c>weight / (k + rank + 1)</c> per hit
    /// (1-based ranks); contributions accumulate by <c>(DocumentId, ChunkIndex)</c>, the chunk
    /// instance comes from the first ranking containing it, and the merged
    /// <see cref="SearchResult.Score"/> is the RRF score. <paramref name="k"/> is clamped to
    /// at least 1.
    /// </summary>
    internal static IReadOnlyList<SearchResult> MergeMany(
        IReadOnlyList<(IReadOnlyList<SearchResult> Hits, double Weight)> rankings,
        int topK,
        int k)
    {
        if (topK <= 0) return [];
        var rankConstant = Math.Max(1, k);

        var rrfScores = new Dictionary<(string docId, int chunkIndex), double>();
        var chunkLookup = new Dictionary<(string docId, int chunkIndex), TextChunk>();

        for (var r = 0; r < rankings.Count; r++)
        {
            var (hits, weight) = rankings[r];
            for (int rank = 0; rank < hits.Count; rank++)
            {
                var chunk = hits[rank].Chunk;
                var key = (chunk.DocumentId, chunk.ChunkIndex);
                var contrib = weight / (rankConstant + rank + 1);
                rrfScores[key] = rrfScores.TryGetValue(key, out var s) ? s + contrib : contrib;
                chunkLookup.TryAdd(key, chunk);
            }
        }

        var sorted = new List<(double score, TextChunk chunk)>(rrfScores.Count);
        foreach (var (key, score) in rrfScores)
            sorted.Add((score, chunkLookup[key]));

        sorted.Sort(static (a, b) => b.score.CompareTo(a.score));

        var count = Math.Min(topK, sorted.Count);
        var result = new List<SearchResult>(count);
        for (int i = 0; i < count; i++)
            result.Add(new SearchResult { Chunk = sorted[i].chunk, Score = sorted[i].score });

        return result;
    }

    internal static IReadOnlyList<SearchResult> ToSearchResults(
        IReadOnlyList<(TextChunk chunk, double score)> hits)
    {
        var results = new List<SearchResult>(hits.Count);
        for (var i = 0; i < hits.Count; i++)
            results.Add(new SearchResult { Chunk = hits[i].chunk, Score = hits[i].score });
        return results;
    }
}
