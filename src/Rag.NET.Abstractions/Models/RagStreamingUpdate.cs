namespace Rag.NET.Models;

/// <summary>
/// One increment of <c>IAnswerEngine.AskStreamingAsync</c>'s output. A caller accumulates
/// <see cref="TextDelta"/> across the stream to reconstruct the full answer.
/// </summary>
public sealed record RagStreamingUpdate
{
    /// <summary>
    /// The next slice of generated answer text to append. <see langword="null"/> on an update that
    /// carries only <see cref="Sources"/> with no new text.
    /// </summary>
    public string? TextDelta { get; init; }

    /// <summary>
    /// The sources used to build the prompt. When contextual compression is enabled,
    /// this reflects the post-compression list — <see cref="SearchResult.CompressedText"/>
    /// is populated and consumers reading the originals should use <c>SearchResult.Chunk.Text</c>.
    /// </summary>
    public IReadOnlyList<SearchResult>? Sources { get; init; }
}
