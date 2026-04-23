namespace Rag.NET.QueryTechniques.ContextualCompression;

/// <summary>
/// Configuration for <see cref="IContextualCompressor"/>. Exactly one stopping
/// criterion (<see cref="KeepTopSentences"/> or <see cref="MaxTokensPerChunk"/>)
/// must be set — validated at registration time by the <c>UseContextualCompression</c>
/// extension.
/// </summary>
public sealed class ContextualCompressionOptions
{
    /// <summary>Which compressor implementation to register.</summary>
    public ContextualCompressionStrategy Strategy { get; set; }
        = ContextualCompressionStrategy.Extractive;

    /// <summary>Keep the top-N most relevant sentences per chunk.</summary>
    /// <remarks>
    /// Precedence: when both this and <see cref="MaxTokensPerChunk"/> are set,
    /// <see cref="KeepTopSentences"/> wins (simpler mental model).
    /// </remarks>
    public int? KeepTopSentences { get; set; } = 3;

    /// <summary>
    /// Soft cap — keep highest-scoring sentences until the cap is reached.
    /// Uses the tokenizer configured on the compressor. Guideline, not a hard limit
    /// (abstractive mode may exceed it by a small margin).
    /// </summary>
    public int? MaxTokensPerChunk { get; set; }
}
