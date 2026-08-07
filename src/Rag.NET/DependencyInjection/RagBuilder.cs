using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.SelfQuery;
using Rag.NET.Retrieval;
using Rag.NET.Search;
using Rag.NET.Storage;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Fluent builder for configuring the Rag.NET pipeline services.
/// Obtain an instance via <c>services.AddRagNet(rag => ...)</c>.
/// </summary>
public sealed class RagBuilder(IServiceCollection services) : IRagBuilder
{
    /// <summary>Gets the underlying <see cref="IServiceCollection"/> for advanced registrations.</summary>
    public IServiceCollection Services { get; } = services;

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
    /// Registers a document parser. Multiple parsers can be registered; the pipeline
    /// selects the first one whose <c>CanParse</c> returns <see langword="true"/> for a given content type.
    /// </summary>
    /// <typeparam name="TParser">The <see cref="IDocumentParser"/> implementation to register.</typeparam>
    /// <param name="replaces">
    /// When set, deliberately overrides <paramref name="replaces"/>: every registered
    /// <see cref="IDocumentParser"/> descriptor implemented by it, and every
    /// <see cref="ParserClaim"/> declared for it, are removed before <typeparamref name="TParser"/>
    /// is registered. Removing the old registration — not just its claim — is load-bearing: parser
    /// selection takes the <i>first</i> registered parser whose <c>CanParse</c> matches, and
    /// built-in parsers register before <c>configure</c> runs, so leaving the replaced descriptor in
    /// place would let it keep winning even once the conflict is silenced. <paramref name="replaces"/>
    /// is matched by <see cref="Type.FullName"/>, not <see cref="Type.Name"/> — see
    /// <see cref="ParserClaim.ParserTypeName"/>'s remarks for why a short-name match would collapse
    /// two distinct parsers that happen to share one. A <paramref name="replaces"/> that was never
    /// registered removes nothing and is not an error.
    /// </param>
    public RagBuilder AddParser<TParser>(Type? replaces = null) where TParser : class, IDocumentParser
    {
        if (replaces is not null)
        {
            RemoveReplacedParser(replaces);
        }

        Services.AddSingleton<IDocumentParser, TParser>();
        return this;
    }

    /// <summary>
    /// Removes <paramref name="replaced"/>'s <see cref="IDocumentParser"/> registration and every
    /// <see cref="ParserClaim"/> declared for it, so a caller of
    /// <see cref="AddParser{TParser}(Type?)"/> that names it as <c>replaces</c> actually wins
    /// selection rather than merely avoiding the conflict check.
    /// </summary>
    private void RemoveReplacedParser(Type replaced)
    {
        var replacedTypeName = replaced.FullName ?? replaced.Name;

        for (var i = Services.Count - 1; i >= 0; i--)
        {
            var descriptor = Services[i];

            var isReplacedParser =
                descriptor.ServiceType == typeof(IDocumentParser) &&
                descriptor.ImplementationType == replaced;

            var isReplacedClaim =
                descriptor.ServiceType == typeof(ParserClaim) &&
                descriptor.ImplementationInstance is ParserClaim claim &&
                string.Equals(claim.ParserTypeName, replacedTypeName, StringComparison.Ordinal);

            if (isReplacedParser || isReplacedClaim)
            {
                Services.RemoveAt(i);
            }
        }
    }

    /// <inheritdoc/>
    IRagBuilder IRagBuilder.AddParser<TParser>(Type? replaces) => AddParser<TParser>(replaces);

    /// <inheritdoc/>
    IRagBuilder IRagBuilder.UseReranking<TReranker>() => UseReranking<TReranker>();

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
    /// Registers <see cref="Rag.NET.Retrieval.TagRetriever"/> as a decorator over the existing
    /// <see cref="IRetriever"/>. At query time, the decorator embeds the query, cosine-scans
    /// the tag index populated during ingestion, and injects matching tag key-value pairs
    /// as <see cref="Rag.NET.Models.Options.RetrievalOptions.MetadataFilter"/> entries.
    /// Requires <c>IEmbeddingGenerator</c> to be registered.
    /// </summary>
    /// <remarks>
    /// The decorator is wired by <c>AddRagNet</c> after the builder delegate returns.
    /// When both <c>UseDeepResearch</c> and <c>UseTagRetrieval</c> are configured,
    /// the stacking order is <c>TagRetriever → DeepResearchRetriever → PipelineRetriever</c>.
    /// </remarks>
    public RagBuilder UseTagRetrieval(TagRetrievalOptions? options = null)
    {
        Services.AddSingleton(options ?? new TagRetrievalOptions());
        Services.TryAddSingleton<ITagIndex, InMemoryTagIndex>();
        return this;
    }

    /// <summary>
    /// Registers <see cref="Rag.NET.Retrieval.TimeWeightedRetriever"/> as a decorator over the
    /// existing <see cref="IRetriever"/>. After retrieval, each result's similarity score is
    /// multiplied by <c>e^(−DecayRate × age_hours)</c> where age is derived from
    /// <c>chunk.Metadata["created_at"]</c> written at ingest time by
    /// <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/>.
    /// Results are re-sorted by the combined score before being returned.
    /// </summary>
    /// <remarks>
    /// The decorator is wired by <c>AddRagNet</c> after the builder delegate returns.
    /// When combined with other decorators, stacking order (outermost first) is:
    /// <c>TagRetriever → TimeWeightedRetriever → DeepResearchRetriever → PipelineRetriever</c>.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseTimeWeighting = false }</c>.
    /// </remarks>
    public RagBuilder UseTimeWeighting(TimeWeightedOptions? options = null)
    {
        Services.AddSingleton(options ?? new TimeWeightedOptions());
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
    /// Registers <see cref="ConversationMemoryPipeline"/> as the <see cref="IConversationMemory"/>.
    /// When registered, answer engines automatically trim conversation history before each call
    /// using the configured sliding-window, token-budget, and optional summary strategies.
    /// Use the optional <paramref name="configure"/> delegate to wrap the pipeline with additional
    /// decorators (e.g. <c>Rag.NET.Memory.RagBuilderExtensions.UsePersistentMemory</c>).
    /// </summary>
    /// <param name="options">Optional memory options. Defaults to pass-through (no trimming).</param>
    /// <param name="configure">Optional delegate to configure memory decorators.</param>
    public RagBuilder UseConversationMemory(
        ConversationMemoryOptions? options = null,
        Action<ConversationMemoryBuilder>? configure = null)
    {
        var opts = options ?? new ConversationMemoryOptions();
        Services.AddSingleton(opts);

        var memBuilder = new ConversationMemoryBuilder(Services);
        configure?.Invoke(memBuilder);

        if (memBuilder.DecoratorFactory is { } factory)
        {
            Services.AddSingleton<IConversationMemory>(sp =>
            {
                IConversationMemory pipeline = new ConversationMemoryPipeline(
                    opts,
                    sp.GetService<IChatClient>(),
                    sp.GetService<ILogger<ConversationMemoryPipeline>>() ?? NullLogger<ConversationMemoryPipeline>.Instance);
                return factory(sp, pipeline);
            });
        }
        else
        {
            Services.AddSingleton<IConversationMemory>(sp =>
                new ConversationMemoryPipeline(
                    opts,
                    sp.GetService<IChatClient>(),
                    sp.GetService<ILogger<ConversationMemoryPipeline>>() ?? NullLogger<ConversationMemoryPipeline>.Instance));
        }

        return this;
    }
}
