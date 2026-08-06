namespace Rag.NET.Models;

/// <summary>A chunk returned by a search, alongside the score it was found with.</summary>
public sealed record SearchResult
{
    /// <summary>The matched chunk.</summary>
    public required TextChunk Chunk { get; init; }

    /// <summary>
    /// The score this result was found with — not a percentage. Assumed to be a similarity,
    /// roughly bounded to <c>[0, 1]</c> and safe to threshold, unless the originating store
    /// implements <see cref="Rag.NET.Abstractions.IScoreScaleAware"/> and declares
    /// <see cref="Rag.NET.Abstractions.ScoreScale.OpaqueRanking"/> — an ordinal-only ranking
    /// signal (for example Reciprocal Rank Fusion) that must not be compared against a fixed
    /// cut-off.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Compressed-for-LLM view of <see cref="TextChunk.Text"/>. <see langword="null"/>
    /// when no compression was applied. Answer engines prefer this over
    /// <see cref="TextChunk.Text"/> when non-null.
    /// </summary>
    public string? CompressedText { get; init; }
}
