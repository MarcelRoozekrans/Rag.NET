namespace Rag.NET.Models.Options;

/// <summary>
/// Options for <c>TokenAwareChunkingStrategy</c> — the sliding-window baseline.
/// When <see cref="WindowSizeTokens"/> / <see cref="OverlapTokens"/> are null the strategy
/// falls back to <see cref="ChunkingOptions.MaxChunkSize"/> / <see cref="ChunkingOptions.Overlap"/>
/// interpreted as token counts.
/// </summary>
public sealed class TokenAwareChunkingOptions
{
    /// <summary>
    /// The model name used to select the tokenizer encoding (e.g., "gpt-4", "gpt-3.5-turbo").
    /// Defaults to "gpt-4" which uses the cl100k_base encoding.
    /// </summary>
    public string ModelName { get; set; } = "gpt-4";

    /// <summary>
    /// Fixed sliding-window size in tokens. When null, <see cref="ChunkingOptions.MaxChunkSize"/> is used.
    /// </summary>
    public int? WindowSizeTokens { get; set; }

    /// <summary>
    /// Overlap between consecutive windows in tokens. When null, <see cref="ChunkingOptions.Overlap"/> is used.
    /// </summary>
    public int? OverlapTokens { get; set; }
}
