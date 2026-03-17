using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Rag.NET.Models;

namespace Rag.NET.PostRetrieval;

public static class MmrSelector
{
    /// <summary>
    /// Greedily selects <paramref name="topK"/> results that are both relevant to
    /// <paramref name="query"/> and maximally dissimilar from each other.
    /// </summary>
    /// <param name="lambda">
    /// Trade-off weight: 1.0 = pure relevance, 0.0 = pure diversity. Default 0.5.
    /// </param>
    public static async Task<IReadOnlyList<SearchResult>> SelectAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        int topK,
        float lambda = 0.5f,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentException.ThrowIfNullOrEmpty(query);

        if (candidates.Count == 0)
            return Array.Empty<SearchResult>();

        var k = Math.Min(topK, candidates.Count);

        var queryEmbedding = await embedder.GenerateAsync([query], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var queryVec = queryEmbedding[0].Vector;

        var chunkTexts = candidates.Select(r => r.Chunk.Text).ToList();
        var chunkEmbeddings = await embedder.GenerateAsync(chunkTexts, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var chunkVecs = chunkEmbeddings.Select(e => e.Vector).ToArray();

        var selected = new List<(SearchResult Result, ReadOnlyMemory<float> Vector)>(k);
        var remaining = new bool[candidates.Count]; // true = already selected/removed

        for (int iter = 0; iter < k; iter++)
        {
            int bestIdx = -1;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < remaining.Length; i++)
            {
                if (remaining[i]) continue;

                var simQuery = EmbeddingMath.CosineSimilarity(chunkVecs[i], queryVec);

                float maxSimSelected = 0f;
                foreach (ref readonly var sel in CollectionsMarshal.AsSpan(selected))
                    maxSimSelected = Math.Max(maxSimSelected, EmbeddingMath.CosineSimilarity(chunkVecs[i], sel.Vector));

                var score = lambda * simQuery - (1f - lambda) * maxSimSelected;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) break;
            selected.Add((candidates[bestIdx], chunkVecs[bestIdx]));
            remaining[bestIdx] = true;
        }

        return selected.Select(s => s.Result).ToList();
    }
}
