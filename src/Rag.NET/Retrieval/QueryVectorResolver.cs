using Microsoft.Extensions.AI;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Resolves the dense query vector for search behaviors:
/// a pre-computed <see cref="RetrievalOptions.EmbeddingOverride"/> (set by HyDE v2 averaging)
/// wins; otherwise the <see cref="RetrievalOptions.EmbeddingTextOverride"/> (single-doc HyDE)
/// or the raw query is embedded. Shared by the vector-store and ensemble (dense arm) behaviors.
/// </summary>
internal static class QueryVectorResolver
{
    public static async ValueTask<ReadOnlyMemory<float>> ResolveAsync(
        RetrievalOptions options,
        string query,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        CancellationToken ct)
    {
        // Empty means absent — same contract as TextChunk.Embedding.
        if (options.EmbeddingOverride is { IsEmpty: false } over)
            return over;

        var textToEmbed = options.EmbeddingTextOverride ?? query;
        var embeddings = await embedder.GenerateAsync([textToEmbed], cancellationToken: ct).ConfigureAwait(false);
        return embeddings[0].Vector;
    }
}
