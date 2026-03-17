using Microsoft.Extensions.AI;
using Rag.NET.Models;

namespace Rag.NET.PostRetrieval;

public static class RedundancyFilter
{
    /// <summary>
    /// Filters out near-duplicate chunks using cosine similarity of their embeddings.
    /// Re-embeds all chunks in a single batch call, then greedily accepts each chunk
    /// only if its similarity to every previously accepted chunk is below <paramref name="threshold"/>.
    /// </summary>
    public static async Task<IReadOnlyList<SearchResult>> FilterAsync(
        IReadOnlyList<SearchResult> results,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        float threshold,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(embedder);

        if (results.Count == 0)
            return Array.Empty<SearchResult>();

        var texts = results.Select(r => r.Chunk.Text).ToList();
        var embeddings = await embedder.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);

        var vectors = embeddings.Select(e => e.Vector).ToArray();
        var accepted = new List<(SearchResult Result, ReadOnlyMemory<float> Vector)>();

        for (int i = 0; i < results.Count; i++)
        {
            bool redundant = false;
            for (int j = 0; j < accepted.Count; j++)
            {
                if (EmbeddingMath.CosineSimilarity(vectors[i], accepted[j].Vector) >= threshold)
                {
                    redundant = true;
                    break;
                }
            }

            if (!redundant)
                accepted.Add((results[i], vectors[i]));
        }

        return accepted.Select(a => a.Result).ToList();
    }
}
