namespace Rag.NET.Models;

/// <summary>
/// Token-level embedding output of an <see cref="Rag.NET.Abstractions.ITokenEmbeddingGenerator"/>:
/// a per-token vector matrix aligned with the char span of each token in the input text.
/// Late chunking pools rows of this matrix over a chunk's token window to derive a
/// context-aware chunk embedding, and uses <see cref="TokenOffsets"/> to slice the chunk text
/// back out of the original input.
/// </summary>
public sealed record TokenEmbeddingResult
{
    /// <summary>Row-major [TokenCount x Dimension] token embedding matrix.</summary>
    public required ReadOnlyMemory<float> Embeddings { get; init; }

    /// <summary>Length of each token vector (the number of columns per row).</summary>
    public required int Dimension { get; init; }

    /// <summary>Char span (start, end-exclusive) of each token in the input text.</summary>
    public required IReadOnlyList<(int Start, int End)> TokenOffsets { get; init; }
}
