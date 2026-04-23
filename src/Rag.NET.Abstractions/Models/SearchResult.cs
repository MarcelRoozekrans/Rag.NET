namespace Rag.NET.Models;

public sealed record SearchResult
{
    public required TextChunk Chunk { get; init; }
    public required double Score { get; init; }

    /// <summary>
    /// Compressed-for-LLM view of <see cref="TextChunk.Text"/>. <see langword="null"/>
    /// when no compression was applied. Answer engines prefer this over
    /// <see cref="TextChunk.Text"/> when non-null.
    /// </summary>
    public string? CompressedText { get; init; }
}
