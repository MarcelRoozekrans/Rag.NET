using Microsoft.Extensions.AI;

namespace Rag.NET.Models.Options;

/// <summary>
/// Per-call overrides for <c>IRagPipeline.AskAsync</c>/<c>AskStreamingAsync</c>, covering both the
/// retrieval that gathers sources and the synthesis that turns them into an answer.
/// </summary>
/// <remarks>
/// <para>
/// Retrieval is set in one of two ways. The short form is eight fields: <see cref="TopK"/>,
/// <see cref="MaxContextTokens"/>, <see cref="MinScore"/>, <see cref="UseHybridSearch"/>,
/// <see cref="UseLostInTheMiddleReordering"/>, <see cref="UseRedundancyFilter"/>,
/// <see cref="RedundancyThreshold"/> and <see cref="MetadataFilter"/>. They are copied into a new
/// <see cref="RetrievalOptions"/>, and every other retrieval option keeps its default. The full form
/// is <see cref="Retrieval"/>, which reaches every retrieval option, MMR and switching HyDE,
/// multi-query or reranking off included.
/// </para>
/// <para>
/// The two forms do not mix. Assigning any of the eight fields, even to its default value, and
/// setting <see cref="Retrieval"/> makes <c>AskAsync</c> and <c>AskStreamingAsync</c> throw
/// <see cref="ArgumentException"/>. See <see cref="Retrieval"/> for the remote clients.
/// </para>
/// </remarks>
public sealed class RagOptions
{
    private int _topK = 5;
    private int? _maxContextTokens;
    private double _minScore;
    private bool _useHybridSearch;
    private bool _useLostInTheMiddleReordering;
    private bool _useRedundancyFilter;
    private float _redundancyThreshold = 0.95f;
    private IDictionary<string, MetadataValue>? _metadataFilter;
    private RetrievalField _assigned;

    /// <summary>
    /// The complete retrieval options for this call. <see langword="null"/> by default, in which case
    /// the eight short-form retrieval fields on this class are used. When set, it is passed to the
    /// retriever unchanged, so every retrieval option is reachable per call: <c>UseMmr</c>,
    /// <c>UseHyde = false</c>, <c>UseMultiQuery = false</c>, <c>UseReranking = false</c> and the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting this together with any of the short-form fields, even to its default value, makes the
    /// in-process <c>RagPipeline</c>'s <c>AskAsync</c> and <c>AskStreamingAsync</c> throw
    /// <see cref="ArgumentException"/> before any retrieval runs. Move those values into this
    /// object instead.
    /// </para>
    /// <para>
    /// The remote clients, <c>HttpRagPipeline</c> and <c>GrpcRagPipeline</c>, cannot send this to
    /// the server yet and throw <see cref="NotSupportedException"/> when it is set (issue #944).
    /// </para>
    /// <para>
    /// Serializing a <see cref="RagOptions"/> writes the short-form fields too, so deserializing
    /// that JSON assigns them. A round-tripped instance with this property set therefore throws.
    /// Serialize the <see cref="RetrievalOptions"/> separately instead.
    /// </para>
    /// </remarks>
    public RetrievalOptions? Retrieval { get; set; }
    /// <summary>
    /// Chunks to retrieve before synthesis. Defaults to 5. Forwarded to the retrieval stage as
    /// <c>RetrievalOptions.TopK</c>; retrieval fails validation if this is not greater than 0.
    /// </summary>
    public int TopK { get => _topK; set { _topK = value; _assigned |= RetrievalField.TopK; } }

    /// <summary>
    /// Caps the combined length of retrieved context, in cl100k_base tokens; forwarded to
    /// <c>RetrievalOptions.MaxContextTokens</c>. <see langword="null"/> applies no length bound.
    /// <see cref="TopK"/> bounds the number of chunks, not their length (issue #85).
    /// </summary>
    public int? MaxContextTokens { get => _maxContextTokens; set { _maxContextTokens = value; _assigned |= RetrievalField.MaxContextTokens; } }

    /// <summary>
    /// Minimum similarity score a retrieved chunk must meet — a similarity score, not a
    /// percentage. Defaults to 0.0 (no filtering). Passed straight through to the vector store's
    /// own score threshold; filtering happens inside the store, not here.
    /// </summary>
    public double MinScore { get => _minScore; set { _minScore = value; _assigned |= RetrievalField.MinScore; } }

    /// <summary>
    /// Combines dense vector search with sparse/keyword search (reciprocal rank fusion) for this
    /// call. Defaults to <see langword="false"/> (dense search only).
    /// </summary>
    public bool UseHybridSearch { get => _useHybridSearch; set { _useHybridSearch = value; _assigned |= RetrievalField.UseHybridSearch; } }

    /// <summary>
    /// Reorders retrieved chunks so the most relevant sit at the start and end of the context
    /// rather than the middle, countering LLMs' tendency to attend less to mid-context text.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool UseLostInTheMiddleReordering { get => _useLostInTheMiddleReordering; set { _useLostInTheMiddleReordering = value; _assigned |= RetrievalField.UseLostInTheMiddleReordering; } }

    /// <summary>
    /// Drops near-duplicate chunks by embedding cosine similarity before synthesis. Defaults to
    /// <see langword="false"/>; when enabled, <see cref="RedundancyThreshold"/> is the similarity
    /// cutoff at which a later chunk is considered a duplicate and dropped.
    /// </summary>
    public bool UseRedundancyFilter { get => _useRedundancyFilter; set { _useRedundancyFilter = value; _assigned |= RetrievalField.UseRedundancyFilter; } }

    /// <summary>
    /// Cosine similarity at or above which two chunks are treated as redundant, so the later one
    /// is dropped. Range 0.0–1.0, default 0.95. Ignored unless <see cref="UseRedundancyFilter"/>
    /// is <see langword="true"/>.
    /// </summary>
    public float RedundancyThreshold { get => _redundancyThreshold; set { _redundancyThreshold = value; _assigned |= RetrievalField.RedundancyThreshold; } }

    /// <summary>
    /// Restricts retrieval to chunks whose metadata matches every key/value pair exactly
    /// (typed equality — a number filter value matches a number, not its string form — with
    /// ordinal comparison for strings and AND semantics across pairs). <see langword="null"/> or
    /// an empty dictionary means no filtering.
    /// </summary>
    public IDictionary<string, MetadataValue>? MetadataFilter { get => _metadataFilter; set { _metadataFilter = value; _assigned |= RetrievalField.MetadataFilter; } }

    /// <summary>
    /// Replaces the answer engine's built-in system prompt for this call.
    /// <see langword="null"/> falls back to the engine's default prompt; an empty string is used
    /// verbatim as an empty prompt rather than triggering that fallback.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Sends retrieved sources as a tool result rather than inside the user message.
    /// <see langword="false"/> by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default shape puts the context and the question in one user message. Setting this
    /// produces System, User(query), Assistant(tool call), Tool(context) instead — so the user
    /// message contains only what the user actually typed.
    /// </para>
    /// <para>
    /// Requested in #365 to let PII detection and redaction run over the user's own text without
    /// also scanning retrieved source content the caller already trusts.
    /// </para>
    /// <para>
    /// <b>Off by default, and that is not timidity.</b> The answer cache is keyed on a prompt
    /// embedding the context, so changing the message shape changes every cache key: cache misses,
    /// regeneration, and different numbers. Every pinned figure in
    /// <c>MultiHopRagAnswerReproduction</c> was measured against the default shape. Making this the
    /// default would invalidate all of them without a word.
    /// </para>
    /// </remarks>
    public bool SendSourcesAsToolResult { get; set; }

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

    /// <summary>
    /// The short-form retrieval fields that were assigned, by name, in declaration order. A field
    /// counts once its setter has run, whatever the value.
    /// </summary>
    internal IReadOnlyList<string> AssignedRetrievalFields
    {
        get
        {
            var names = new List<string>();
            foreach (var flag in RetrievalFieldOrder)
            {
                if ((_assigned & flag) != RetrievalField.None)
                    names.Add(flag.ToString());
            }

            return names;
        }
    }

    private static readonly RetrievalField[] RetrievalFieldOrder =
    [
        RetrievalField.TopK,
        RetrievalField.MaxContextTokens,
        RetrievalField.MinScore,
        RetrievalField.UseHybridSearch,
        RetrievalField.UseLostInTheMiddleReordering,
        RetrievalField.UseRedundancyFilter,
        RetrievalField.RedundancyThreshold,
        RetrievalField.MetadataFilter,
    ];

    // Member names match the property names, so ToString() gives the name the exception reports.
    [Flags]
    private enum RetrievalField
    {
        None = 0,
        TopK = 1 << 0,
        MaxContextTokens = 1 << 1,
        MinScore = 1 << 2,
        UseHybridSearch = 1 << 3,
        UseLostInTheMiddleReordering = 1 << 4,
        UseRedundancyFilter = 1 << 5,
        RedundancyThreshold = 1 << 6,
        MetadataFilter = 1 << 7,
    }
}
