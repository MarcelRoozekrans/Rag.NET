using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Produces token-level embeddings for a text: one vector per token plus the char span of each
/// token in the input. This is the building block for late chunking, where the full text is
/// embedded in a single pass (so every token vector carries whole-document context) and chunk
/// embeddings are then derived by pooling token vectors over each chunk's token window —
/// instead of embedding each chunk in isolation.
/// </summary>
public interface ITokenEmbeddingGenerator
{
    /// <summary>
    /// Maximum tokens the underlying model accepts in one pass. This is an advisory sizing hint
    /// to callers (e.g. for choosing section sizes) — it is NOT a hard input limit of
    /// <see cref="GenerateAsync"/>.
    /// </summary>
    int MaxTokens { get; }

    /// <summary>
    /// Embed the full text, returning one vector per token plus char offsets.
    /// Implementations MUST accept text of any length: inputs whose token count exceeds
    /// <see cref="MaxTokens"/> are windowed internally into model-sized passes and the
    /// per-window matrices stitched back into a single result covering every token.
    /// </summary>
    ValueTask<TokenEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default);
}
