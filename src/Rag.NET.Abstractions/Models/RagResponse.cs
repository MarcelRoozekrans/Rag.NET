namespace Rag.NET.Models;

/// <summary>The complete result of <c>IAnswerEngine.AskAsync</c> — the generated answer plus the sources it was built from.</summary>
public sealed record RagResponse
{
    /// <summary>The generated answer text.</summary>
    public required string Answer { get; init; }

    /// <summary>
    /// The sources used to build the prompt. When contextual compression is enabled,
    /// this reflects the post-compression list — <see cref="SearchResult.CompressedText"/>
    /// is populated and consumers reading the originals should use <c>SearchResult.Chunk.Text</c>.
    /// </summary>
    public required IReadOnlyList<SearchResult> Sources { get; init; }
}
