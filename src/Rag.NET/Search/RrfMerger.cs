using Rag.NET.Models;

namespace Rag.NET.Search;

/// <summary>
/// Reciprocal Rank Fusion: score(d) = Σ 1/(k + rank_i), k=60 (1-based ranks).
/// </summary>
internal static class RrfMerger
{
    private const double K = 60.0;

    public static IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<SearchResult> dense,
        IReadOnlyList<(int docId, double score)> bm25Hits,
        IReadOnlyList<TextChunk> allChunks,
        int topK)
    {
        if (topK <= 0) return [];

        var rrfScores = new Dictionary<(string docId, int chunkIndex), double>();
        var chunkLookup = new Dictionary<(string docId, int chunkIndex), TextChunk>();

        // Dense results (1-based rank)
        for (int rank = 0; rank < dense.Count; rank++)
        {
            var chunk = dense[rank].Chunk;
            var key = (chunk.DocumentId, chunk.ChunkIndex);
            var contrib = 1.0 / (K + rank + 1);
            rrfScores[key] = rrfScores.TryGetValue(key, out var s) ? s + contrib : contrib;
            chunkLookup.TryAdd(key, chunk);
        }

        // BM25 results (1-based rank)
        for (int rank = 0; rank < bm25Hits.Count; rank++)
        {
            var (docId, _) = bm25Hits[rank];
            var chunk = allChunks[docId];
            var key = (chunk.DocumentId, chunk.ChunkIndex);
            var contrib = 1.0 / (K + rank + 1);
            rrfScores[key] = rrfScores.TryGetValue(key, out var s) ? s + contrib : contrib;
            chunkLookup.TryAdd(key, chunk);
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
}
