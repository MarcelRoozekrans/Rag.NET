namespace Rag.NET.Models;

/// <summary>
/// Token-level embedding output of an <see cref="Rag.NET.Abstractions.ITokenEmbeddingGenerator"/>:
/// a per-token vector matrix aligned with the char span of each token in the input text.
/// Late chunking pools rows of this matrix over a chunk's token window to derive a
/// context-aware chunk embedding, and uses <see cref="TokenOffsets"/> to slice the chunk text
/// back out of the original input.
/// <para>
/// Invariants a producer must uphold (consumers validate and treat violations as generator
/// failures):
/// <list type="bullet">
/// <item><description><c>Embeddings.Length == TokenOffsets.Count * Dimension</c> — row
/// <c>i</c> of the matrix is the vector for the token described by
/// <c>TokenOffsets[i]</c>.</description></item>
/// <item><description><see cref="TokenOffsets"/> lists tokens in input order; each entry is a
/// char span into the input text with an inclusive <c>Start</c> and an exclusive
/// <c>End</c>.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed record TokenEmbeddingResult
{
    /// <summary>Row-major [TokenCount x Dimension] token embedding matrix.</summary>
    public required ReadOnlyMemory<float> Embeddings { get; init; }

    /// <summary>Length of each token vector (the number of columns per row).</summary>
    public required int Dimension { get; init; }

    /// <summary>
    /// Char span (start inclusive, end exclusive) of each token in the input text, in input
    /// order; row <c>i</c> of <see cref="Embeddings"/> belongs to <c>TokenOffsets[i]</c>.
    /// </summary>
    public required IReadOnlyList<(int Start, int End)> TokenOffsets { get; init; }
}
