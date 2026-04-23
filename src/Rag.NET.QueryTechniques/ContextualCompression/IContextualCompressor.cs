using Rag.NET.Models;

namespace Rag.NET.QueryTechniques.ContextualCompression;

/// <summary>
/// Compresses retrieved chunks to only the content relevant to the query,
/// populating <see cref="SearchResult.CompressedText"/>. Non-destructive —
/// <see cref="TextChunk.Text"/> is never modified.
/// </summary>
public interface IContextualCompressor
{
    /// <summary>Compress each chunk's relevant content for <paramref name="query"/>.</summary>
    /// <remarks>
    /// Failing compression for an individual chunk is logged and returns the chunk
    /// with <see cref="SearchResult.CompressedText"/> set to <see langword="null"/> —
    /// the call never throws for per-chunk failures. Cancellation propagates.
    /// </remarks>
    ValueTask<IReadOnlyList<SearchResult>> CompressAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        CancellationToken cancellationToken = default);
}
