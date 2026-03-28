namespace Rag.NET.Models.Options;

public sealed class TimeWeightedOptions
{
    /// <summary>
    /// Decay constant λ in <c>score × e^(−λ × age_hours)</c>.
    /// Default 0.01 halves relevance after ~69 hours (~3 days).
    /// </summary>
    public double DecayRate { get; init; } = 0.01;

    /// <summary>
    /// Ordered list of <see cref="Rag.NET.Models.TextChunk"/> metadata keys to check
    /// when the primary <c>"created_at"</c> key is absent.
    /// First key with a parseable ISO 8601 value wins.
    /// Useful for documents from external systems that store timestamps under a different key.
    /// </summary>
    public IReadOnlyList<string> FallbackMetadataKeys { get; init; } = [];
}
