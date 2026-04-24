namespace Rag.NET.Models;

public sealed record RagResponse
{
    public required string Answer { get; init; }

    /// <summary>
    /// The sources used to build the prompt. When contextual compression is enabled,
    /// this reflects the post-compression list — <see cref="SearchResult.CompressedText"/>
    /// is populated and consumers reading the originals should use <c>SearchResult.Chunk.Text</c>.
    /// </summary>
    public required IReadOnlyList<SearchResult> Sources { get; init; }
}
