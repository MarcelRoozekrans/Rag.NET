using Microsoft.Extensions.AI;

namespace Rag.NET.Models.Options;

public sealed class SemanticChunkingOptions
{
    /// <summary>
    /// Breakpoint percentile for similarity scores. Consecutive sentence pairs with
    /// similarity in the bottom N percentile are treated as chunk boundaries.
    /// Lower = fewer breaks (larger chunks), higher = more breaks (smaller chunks).
    /// Default 0.25 (bottom 25%).
    /// </summary>
    public float BreakpointPercentile { get; init; } = 0.25f;

    /// <summary>
    /// Minimum chunk size in characters. Chunks smaller than this are merged with
    /// their nearest neighbor. Default 100.
    /// </summary>
    public int MinChunkSize { get; init; } = 100;

    /// <summary>
    /// Maximum chunk size in characters. Chunks exceeding this are split at
    /// sentence boundaries. Default 1500.
    /// </summary>
    public int MaxChunkSize { get; init; } = 1500;

    /// <summary>
    /// Optional embedding model override for chunking only. When null (default),
    /// uses the same <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> registered
    /// for retrieval. Set this when you want a smaller/faster model for chunking
    /// (e.g., MiniLM) while keeping a larger model for retrieval quality.
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>>? ChunkingEmbedder { get; init; }
}
