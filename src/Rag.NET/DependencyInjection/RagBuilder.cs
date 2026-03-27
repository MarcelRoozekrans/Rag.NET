using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.Retry;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.HyDE;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Memory;
using Rag.NET.SelfQuery;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
using Rag.NET.Retrieval;
using Rag.NET.Search;
using Rag.NET.Storage;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Fluent builder for configuring the Rag.NET pipeline services.
/// Obtain an instance via <c>services.AddRagNet(rag => ...)</c>.
/// </summary>
public sealed class RagBuilder(IServiceCollection services)
{
    /// <summary>Gets the underlying <see cref="IServiceCollection"/> for advanced registrations.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// Registers <see cref="SemanticChunkingStrategy"/> as <see cref="IChunkingStrategy"/>,
    /// <see cref="IDocumentChunkingStrategy"/>, and <see cref="IChunkRefinementStrategy"/>.
    /// All three interfaces resolve to the same singleton instance.
    /// Uses the same <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> registered for retrieval
    /// by default. Override via <see cref="SemanticChunkingOptions.ChunkingEmbedder"/> for a
    /// smaller/faster model at chunking time.
    /// </summary>
    public RagBuilder UseSemanticChunking(SemanticChunkingOptions? options = null)
    {
        Services.AddSingleton(options ?? new SemanticChunkingOptions());
        Services.AddSingleton<SemanticChunkingStrategy>();
        Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
        Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
        Services.AddSingleton<IChunkRefinementStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
        return this;
    }

    /// <summary>
    /// Registers <see cref="SemanticChunkingStrategy"/> as only <see cref="IChunkRefinementStrategy"/>.
    /// Use with <c>UseHierarchicalMerging()</c> to add semantic sub-splitting to a hierarchical pipeline
    /// without replacing the primary chunking strategy.
    /// </summary>
    public RagBuilder UseSemanticRefinement(SemanticChunkingOptions? options = null)
    {
        Services.AddSingleton(options ?? new SemanticChunkingOptions());
        Services.AddSingleton<SemanticChunkingStrategy>();
        Services.AddSingleton<IChunkRefinementStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
        return this;
    }

    /// <summary>
    /// Registers a custom chunking strategy. Optionally configures <see cref="ChunkingOptions"/>
    /// (MaxChunkSize and Overlap), which are interpreted as characters by most built-in strategies.
    /// </summary>
    /// <typeparam name="TStrategy">The <see cref="IChunkingStrategy"/> implementation to use.</typeparam>
    /// <param name="configure">Optional delegate to configure chunking options.</param>
    public RagBuilder UseChunkingStrategy<TStrategy>(Action<ChunkingOptions>? configure = null)
        where TStrategy : class, IChunkingStrategy
    {
        Services.AddSingleton<IChunkingStrategy, TStrategy>();

        if (configure is not null)
        {
            var options = new ChunkingOptions();
            configure(options);
            Services.AddSingleton(options);
        }

        return this;
    }

    /// <summary>
    /// Registers <see cref="TokenAwareChunkingStrategy"/>, which splits text by token count
    /// rather than character count using the specified model's tokenizer.
    /// <see cref="ChunkingOptions.MaxChunkSize"/> and <see cref="ChunkingOptions.Overlap"/>
    /// are interpreted as token counts when this strategy is active.
    /// </summary>
    /// <param name="modelName">
    /// The model name used to select the tokenizer encoding (e.g., <c>"gpt-4"</c>, <c>"gpt-3.5-turbo"</c>).
    /// Defaults to <c>"gpt-4"</c> (cl100k_base encoding, compatible with most modern embedding models).
    /// </param>
    public RagBuilder UseTokenAwareChunking(string modelName = "gpt-4")
    {
        Services.AddSingleton<IChunkingStrategy>(_ => new TokenAwareChunkingStrategy(modelName));
        return this;
    }

    /// <summary>
    /// Registers <see cref="HierarchicalMergerChunkingStrategy"/> which merges document sections
    /// into heading-subtree chunks. Each chunk covers one heading and all body text under it
    /// down to <paramref name="options"/>.<see cref="HierarchicalMergerOptions.MaxDepth"/>.
    /// Uses <see cref="DocumentSection.HeadingLevel"/> when available; falls back to
    /// <see cref="HierarchicalMergerOptions.HeadingPatterns"/> for formats without heading metadata.
    /// </summary>
    public RagBuilder UseHierarchicalMerging(HierarchicalMergerOptions? options = null)
    {
        var opts = options ?? new HierarchicalMergerOptions();
        Services.AddSingleton(opts);
        Services.AddSingleton<IChunkingStrategy>(_ => new HierarchicalMergerChunkingStrategy(opts));
        return this;
    }

    /// <summary>
    /// Registers a document parser. Multiple parsers can be registered; the pipeline
    /// selects the first one whose <c>CanParse</c> returns <see langword="true"/> for a given content type.
    /// </summary>
    /// <typeparam name="TParser">The <see cref="IDocumentParser"/> implementation to register.</typeparam>
    public RagBuilder AddParser<TParser>() where TParser : class, IDocumentParser
    {
        Services.AddSingleton<IDocumentParser, TParser>();
        return this;
    }

    /// <summary>
    /// Wraps the registered <see cref="IRetriever"/> with <see cref="DeepResearchRetriever"/>.
    /// On each retrieval call, runs a sufficiency-gated loop: retrieve, ask the LLM whether the
    /// result is sufficient, and if not generate focused sub-queries and retrieve again.
    /// Results are merged and deduplicated across all iterations.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// The decorator is wired by <c>AddRagNet</c> after the
    /// builder delegate returns — calling this method outside of <c>AddRagNet</c>'s configure
    /// delegate has no effect.
    /// </remarks>
    /// <param name="options">Optional options; defaults to <see cref="DeepResearchOptions"/> defaults.</param>
    public RagBuilder UseDeepResearch(DeepResearchOptions? options = null)
    {
        Services.AddSingleton(options ?? new DeepResearchOptions());
        return this;
    }

    /// <summary>
    /// Registers <see cref="LlmQueryExpander"/> as the <see cref="IQueryExpander"/>.
    /// When registered, <see cref="RagPipeline"/> expands each query into
    /// <see cref="MultiQueryOptions.VariantCount"/> alternatives, fans out to the vector store
    /// in parallel, and merges deduplicated results.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseMultiQuery = false }</c>.
    /// </remarks>
    /// <param name="configure">Optional delegate to configure <see cref="MultiQueryOptions"/>.</param>
    public RagBuilder UseMultiQueryRetrieval(Action<MultiQueryOptions>? configure = null)
    {
        var options = new MultiQueryOptions();
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddSingleton<IQueryExpander, LlmQueryExpander>();
        return this;
    }

    /// <summary>
    /// Registers <see cref="LlmHypotheticalDocumentGenerator"/> as the <see cref="IHypotheticalDocumentGenerator"/>.
    /// When registered, the retriever embeds a hypothetical document generated by the LLM
    /// instead of the raw query, improving recall for asymmetric retrieval (short query vs. long document).
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseHyde = false }</c>.
    /// </remarks>
    /// <param name="configure">Optional delegate to configure <see cref="HydeOptions"/>.</param>
    public RagBuilder UseHyde(Action<HydeOptions>? configure = null)
    {
        var options = new HydeOptions();
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddSingleton<IHypotheticalDocumentGenerator, LlmHypotheticalDocumentGenerator>();
        return this;
    }

    /// <summary>
    /// Enables LLM-driven metadata extraction at ingestion time.
    /// When registered, an LLM call is made per chunk to extract structured key-value tags,
    /// which are stored in chunk metadata for use with <see cref="UseSelfQuery"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// When <paramref name="schema"/> is provided, extraction is constrained to the listed fields.
    /// </remarks>
    /// <param name="schema">Optional list of fields to extract. When null, the LLM extracts freely.</param>
    public RagBuilder UseLlmMetadataExtraction(IReadOnlyList<AttributeInfo>? schema = null)
    {
        Services.AddSingleton(new LlmMetadataExtractionOptions { Schema = schema });
        return this;
    }

    /// <summary>
    /// Enables self-query rewriting at retrieval time.
    /// When registered, the LLM parses each question into a refined semantic query
    /// and a structured metadata filter before retrieval executes.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseSelfQuery = false }</c>.
    /// When <paramref name="schema"/> is provided, filtering is constrained to the listed fields.
    /// </remarks>
    /// <param name="schema">Optional list of filterable fields. When null, the LLM filters freely.</param>
    public RagBuilder UseSelfQuery(IReadOnlyList<AttributeInfo>? schema = null)
    {
        Services.AddSingleton(new SelfQueryOptions { Schema = schema });
        return this;
    }

    /// <summary>
    /// Enables two-level retrieval caching backed by <see cref="HybridCache"/>.
    /// Embedding cache caches query→embedding mappings. Result cache
    /// caches the complete post-processed result list.
    /// </summary>
    /// <remarks>
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseCacheEmbedding = false, UseCacheResult = false }</c>.
    /// </remarks>
    /// <param name="configure">Optional delegate to configure <see cref="CachingOptions"/>.</param>
    public RagBuilder UseCaching(Action<CachingOptions>? configure = null)
    {
        var options = new CachingOptions();
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddHybridCache();
        return this;
    }

    /// <summary>
    /// Enables parent-document retrieval. At ingestion, documents are chunked twice:
    /// small child chunks are embedded for precise matching, large parent chunks are
    /// stored in-memory for context-rich answer generation. At retrieval, child matches
    /// are replaced with their parent text.
    /// </summary>
    /// <remarks>
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseParentDocument = false }</c>.
    /// </remarks>
    /// <param name="configure">Optional delegate to configure <see cref="ParentDocumentOptions"/>.</param>
    public RagBuilder UseParentDocumentRetrieval(Action<ParentDocumentOptions>? configure = null)
    {
        var options = new ParentDocumentOptions();
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddSingleton<InMemoryParentChunkStore>();
        Services.TryAddSingleton<IParentChunkStore>(sp => sp.GetRequiredService<InMemoryParentChunkStore>());
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TReranker"/> as the <see cref="IReranker"/>.
    /// When registered, <see cref="RagPipeline"/> rescores search results using
    /// the cross-encoder for higher precision ranking.
    /// </summary>
    /// <remarks>
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseReranking = false }</c>.
    /// Over-fetch control: set <c>RetrievalOptions.CandidateCount</c> (defaults to TopK * 3).
    /// </remarks>
    public RagBuilder UseReranking<TReranker>() where TReranker : class, IReranker
    {
        Services.AddSingleton<IReranker, TReranker>();
        return this;
    }

    /// <summary>
    /// Registers <see cref="MmrRetriever"/> in the post-retrieval chain.
    /// When registered, MMR selection is opt-in per call: set
    /// <c>new RetrievalOptions { UseMmr = true }</c> to activate.
    /// </summary>
    /// <remarks>
    /// MMR over-fetches candidates (<see cref="RetrievalOptions.MmrCandidateCount"/>, default TopK × 3),
    /// then selects <see cref="RetrievalOptions.TopK"/> results balancing relevance and diversity.
    /// Requires <c>IEmbeddingGenerator</c> to be registered in DI.
    /// Per-call activation: pass <c>new RetrievalOptions { UseMmr = true }</c>.
    /// </remarks>
    public RagBuilder UseMmr()
    {
        Services.AddSingleton<MmrEnabled>();
        return this;
    }

    /// <summary>
    /// Registers SQLite-backed persistence for <see cref="IBm25Index"/> and <see cref="IParentChunkStore"/>.
    /// On startup, both stores load persisted data from <paramref name="dbPath"/>.
    /// Every Add/Remove writes through to SQLite synchronously.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite database file. Created if it does not exist.</param>
    /// <param name="collectionName">
    /// Optional stale-data guard. If the registered name differs from what is stored in the database,
    /// all persisted data is wiped before loading. Change this value when replacing the vector store.
    /// Omit to skip the stale guard.
    /// </param>
    public RagBuilder UseSqlitePersistence(string dbPath, string? collectionName = null)
    {
        Services.AddSingleton<SqliteBm25Index>(sp => new SqliteBm25Index(dbPath, collectionName, sp.GetService<SynonymMap>()));
        Services.AddSingleton<IBm25Index>(sp => sp.GetRequiredService<SqliteBm25Index>());

        Services.AddSingleton<SqliteParentChunkStore>(_ => new SqliteParentChunkStore(dbPath, collectionName));
        Services.AddSingleton<IParentChunkStore>(sp => sp.GetRequiredService<SqliteParentChunkStore>());

        return this;
    }

    /// <summary>
    /// Registers a <see cref="SynonymMap"/> that expands tokens at both BM25 index time and query time.
    /// Synonyms are bidirectional: any term in a group matches all other terms in that group.
    /// The map is a singleton — call <see cref="SynonymMap.AddGroup"/> or
    /// <see cref="SynonymMap.RemoveGroup"/> at runtime for live updates without restart.
    /// </summary>
    public RagBuilder UseBm25Synonyms(SynonymMap synonymMap)
    {
        Services.AddSingleton(synonymMap);
        return this;
    }

    /// <summary>
    /// Registers <see cref="SqliteContentHashStore"/> as the <see cref="IContentHashStore"/>.
    /// When registered, <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> automatically skips
    /// files that have not changed since the last ingestion run.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite file. Created if it does not exist.</param>
    public RagBuilder UseContentHashRecordManager(string dbPath)
    {
        Services.AddSingleton<IContentHashStore>(_ => new SqliteContentHashStore(dbPath));
        return this;
    }

    /// <summary>
    /// Adds a Polly resilience pipeline named <c>"rag-net"</c> that wraps embedding and vector-store calls.
    /// When no <paramref name="configure"/> delegate is provided, a default exponential back-off retry
    /// (3 attempts, 1 s base delay, jitter) is applied.
    /// </summary>
    /// <param name="configure">
    /// Optional delegate to customise the <see cref="ResiliencePipelineBuilder"/>.
    /// Replaces the default retry policy when supplied.
    /// </param>
    public RagBuilder ConfigureResilience(Action<ResiliencePipelineBuilder>? configure = null)
    {
        Services.AddResiliencePipeline("rag-net", builder =>
        {
            if (configure is not null)
            {
                configure(builder);
            }
            else
            {
                builder.AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                });
            }
        });

        return this;
    }

    /// <summary>
    /// Registers <see cref="ConversationMemoryPipeline"/> as the <see cref="IConversationMemory"/>.
    /// When registered, answer engines automatically trim conversation history before each call
    /// using the configured sliding-window, token-budget, and optional summary strategies.
    /// </summary>
    /// <param name="options">
    /// Optional memory options. When null, a default <see cref="ConversationMemoryOptions"/> is used
    /// (no window or token limits — history passes through unchanged until configured).
    /// </param>
    public RagBuilder UseConversationMemory(ConversationMemoryOptions? options = null)
    {
        var opts = options ?? new ConversationMemoryOptions();
        Services.AddSingleton(opts);
        Services.AddSingleton<IConversationMemory>(sp =>
            new ConversationMemoryPipeline(
                opts,
                sp.GetService<IChatClient>(),
                sp.GetService<ILogger<ConversationMemoryPipeline>>() ?? NullLogger<ConversationMemoryPipeline>.Instance));
        return this;
    }
}
