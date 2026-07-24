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
    /// <summary>Maximum tokens the underlying model accepts in one pass.</summary>
    int MaxTokens { get; }

    /// <summary>Embed the full text, returning one vector per token plus char offsets.</summary>
    ValueTask<TokenEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default);
}
