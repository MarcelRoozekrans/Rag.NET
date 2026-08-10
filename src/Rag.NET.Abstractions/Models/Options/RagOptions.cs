using Microsoft.Extensions.AI;

namespace Rag.NET.Models.Options;

/// <summary>
/// Per-call overrides for <c>IRagPipeline.AskAsync</c>/<c>AskStreamingAsync</c>, covering both the
/// retrieval that gathers sources and the synthesis that turns them into an answer.
/// </summary>
/// <remarks>
/// <see cref="TopK"/>, <see cref="MinScore"/>, <see cref="MetadataFilter"/>,
/// <see cref="UseHybridSearch"/>, <see cref="UseLostInTheMiddleReordering"/>,
/// <see cref="UseRedundancyFilter"/> and <see cref="RedundancyThreshold"/> are forwarded verbatim
/// into a <c>RetrievalOptions</c> for the retrieval stage; per-call retrieval knobs that have no
/// counterpart here (MMR, reranking, HyDE, and so on) cannot be overridden through
/// <see cref="RagOptions"/> and take whatever the pipeline was configured with at startup.
/// </remarks>
public sealed class RagOptions
{
    /// <summary>
    /// Chunks to retrieve before synthesis. Defaults to 5. Forwarded to the retrieval stage as
    /// <c>RetrievalOptions.TopK</c>; retrieval fails validation if this is not greater than 0.
    /// </summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Minimum similarity score a retrieved chunk must meet — a similarity score, not a
    /// percentage. Defaults to 0.0 (no filtering). Passed straight through to the vector store's
    /// own score threshold; filtering happens inside the store, not here.
    /// </summary>
    public double MinScore { get; set; } = 0.0;

    /// <summary>
    /// Combines dense vector search with sparse/keyword search (reciprocal rank fusion) for this
    /// call. Defaults to <see langword="false"/> (dense search only).
    /// </summary>
    public bool UseHybridSearch { get; set; }

    /// <summary>
    /// Reorders retrieved chunks so the most relevant sit at the start and end of the context
    /// rather than the middle, countering LLMs' tendency to attend less to mid-context text.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool UseLostInTheMiddleReordering { get; set; }

    /// <summary>
    /// Drops near-duplicate chunks by embedding cosine similarity before synthesis. Defaults to
    /// <see langword="false"/>; when enabled, <see cref="RedundancyThreshold"/> is the similarity
    /// cutoff at which a later chunk is considered a duplicate and dropped.
    /// </summary>
    public bool UseRedundancyFilter { get; set; }

    /// <summary>
    /// Cosine similarity at or above which two chunks are treated as redundant, so the later one
    /// is dropped. Range 0.0–1.0, default 0.95. Ignored unless <see cref="UseRedundancyFilter"/>
    /// is <see langword="true"/>.
    /// </summary>
    public float RedundancyThreshold { get; set; } = 0.95f;

    /// <summary>
    /// Restricts retrieval to chunks whose metadata matches every key/value pair exactly
    /// (typed equality — a number filter value matches a number, not its string form — with
    /// ordinal comparison for strings and AND semantics across pairs). <see langword="null"/> or
    /// an empty dictionary means no filtering.
    /// </summary>
    public IDictionary<string, MetadataValue>? MetadataFilter { get; set; }

    /// <summary>
    /// Replaces the answer engine's built-in system prompt for this call.
    /// <see langword="null"/> falls back to the engine's default prompt; an empty string is used
    /// verbatim as an empty prompt rather than triggering that fallback.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Overrides the chat client's sampling temperature for this call.
    /// <see langword="null"/> leaves the chat client's own configured default in place.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Prior conversation turns to include ahead of the retrieved context, oldest first.
    /// <see langword="null"/> or empty omits conversation history from the prompt entirely.
    /// </summary>
    public IList<ChatMessage>? ConversationHistory { get; set; }

    /// <summary>
    /// Selects which answer-generation engine handles this call. Determines whether
    /// <see cref="MapReduceOptions"/> or <see cref="RefineOptions"/> is read — the strategy not
    /// selected is ignored even if set. Defaults to
    /// <see cref="Rag.NET.Models.Options.SynthesisStrategy.Default"/> (a single chat completion
    /// over all retrieved sources).
    /// </summary>
    public SynthesisStrategy SynthesisStrategy { get; set; } = SynthesisStrategy.Default;

    /// <summary>
    /// Tuning for the map step (concurrency, prompt templates) when the strategy above is
    /// <see cref="Rag.NET.Models.Options.SynthesisStrategy.MapReduce"/>.
    /// <see langword="null"/> uses <see cref="Rag.NET.Models.Options.MapReduceOptions"/>'s own
    /// defaults; ignored for any other strategy.
    /// </summary>
    public MapReduceOptions? MapReduceOptions { get; set; }

    /// <summary>
    /// Prompt templates for the iterative refine loop when the strategy above is
    /// <see cref="Rag.NET.Models.Options.SynthesisStrategy.Refine"/>.
    /// <see langword="null"/> uses <see cref="Rag.NET.Models.Options.RefineOptions"/>'s own
    /// defaults; ignored for any other strategy.
    /// </summary>
    public RefineOptions? RefineOptions { get; set; }

    /// <summary>
    /// Bypass contextual compression for this call even when an
    /// <c>IContextualCompressor</c> is registered. Use when raw source
    /// text is required (admin tooling, UI citation rendering).
    /// </summary>
    public bool SkipCompression { get; set; }
}
