using Microsoft.Extensions.AI;

namespace Rag.NET.Models.Options;

public sealed class RagOptions
{
    public int TopK { get; set; } = 5;
    public double MinScore { get; set; } = 0.0;
    public bool UseHybridSearch { get; set; }
    public bool UseLostInTheMiddleReordering { get; set; }
    public bool UseRedundancyFilter { get; set; }
    public float RedundancyThreshold { get; set; } = 0.95f;
    public IDictionary<string, string>? MetadataFilter { get; set; }
    public string? SystemPrompt { get; set; }
    public float? Temperature { get; set; }
    public IList<ChatMessage>? ConversationHistory { get; set; }
    public SynthesisStrategy SynthesisStrategy { get; set; } = SynthesisStrategy.Default;
    public MapReduceOptions? MapReduceOptions { get; set; }
    public RefineOptions? RefineOptions { get; set; }

    /// <summary>
    /// Bypass contextual compression for this call even when an
    /// <c>IContextualCompressor</c> is registered. Use when raw source
    /// text is required (admin tooling, UI citation rendering).
    /// </summary>
    public bool SkipCompression { get; set; }
}
