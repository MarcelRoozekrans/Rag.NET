using Rag.NET.Abstractions;

namespace Rag.NET.Models.Options;

/// <summary>Options for late chunking (token-level embedding pooling).</summary>
public sealed class LateChunkingOptions
{
    /// <summary>Tokens per chunk window.</summary>
    public int WindowSizeTokens { get; set; } = 256;

    /// <summary>Tokens shared between adjacent windows. Must be smaller than <see cref="WindowSizeTokens"/>.</summary>
    public int OverlapTokens { get; set; } = 32;

    /// <summary>Optional dedicated generator; falls back to the DI-registered one.</summary>
    public ITokenEmbeddingGenerator? Generator { get; set; }
}
