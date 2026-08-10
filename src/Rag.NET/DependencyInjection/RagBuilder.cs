using System.Reflection;
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
            ValidateChunkingOptions(options);
            Services.AddSingleton(options);
        }

        return this;
    }

    /// <summary>
    /// Rejects invalid chunking options at the line that configured them.
    /// <para>
    /// Validation used to happen only on first ingestion, in <c>PipelineIngestor</c>. That is too
    /// late to be useful: issue #90 was opened because <c>Overlap = -1</c> appeared to work, and
    /// it appeared to work because the reporter had not ingested anything yet. A configuration
    /// error should fail where it was written, with a stack trace pointing at the caller's
    /// lambda, not on some later call that happens to consume it.
    /// </para>
    /// <para>
    /// The ingestion-time check stays as well. These options are mutable and registered as a bare
    /// singleton, so nothing stops a caller mutating them after registration, and a
    /// <see cref="ChunkingOptions"/> can reach the container without passing through this method.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A property is out of range, or <see cref="ChunkingOptions.Overlap"/> is not smaller than
    /// <see cref="ChunkingOptions.MaxChunkSize"/> — the generated validator covers both, the
    /// second through <see cref="ChunkingOptions.ValidateOverlapFitsChunk"/>.
    /// </exception>
    private static void ValidateChunkingOptions(ChunkingOptions options) =>
        ThrowIfInvalid(new ChunkingOptionsValidator().Validate(options), nameof(options), "chunking");

    /// <summary>
    /// Rejects any invalid options object at the line that configured it, with a stack trace
    /// pointing at the caller's registration rather than at some later call that happens to
    /// consume the singleton — the lesson of issue #90, applied uniformly to every options
    /// type a <c>Use*</c> method registers (see <see cref="ValidateChunkingOptions"/> for the
    /// original case).
    /// </summary>
    /// <param name="result">The generated validator's verdict on the configured options.</param>
    /// <param name="paramName">The caller's options parameter, for <see cref="ArgumentException.ParamName"/>.</param>
    /// <param name="description">What was being configured, for the failure message.</param>
    /// <exception cref="ArgumentException">The options violate a declared constraint.</exception>
    private static void ThrowIfInvalid(
        ZeroAlloc.Validation.ValidationResult result, string paramName, string description)
    {
        if (result.IsValid)
        {
            return;
        }

        // Projected by index into an array, the shape PipelineIngestor.MapFailures uses:
        // ValidationFailure is a non-readonly struct, so enumerating it by value trips EPS06
        // on the hidden copy, while a bare indexed loop over the span trips HLQ013.
        var failures = result.Failures;
        var described = new string[failures.Length];
        for (var i = 0; i < failures.Length; i++)
        {
            described[i] = $"{failures[i].PropertyName} — {failures[i].ErrorMessage}";
        }

        throw new ArgumentException(
            $"The {description} options configured here are invalid: " +
            string.Join("; ", described),
            paramName);
    }

    /// <summary>
    /// Rejects parent-document sizing that the chunking strategies cannot act on.
    /// <para>
    /// <b>This closes a hang, not just a bad value (issue #93).</b>
    /// <c>ParentDocumentIngestionBehavior</c> builds its own <see cref="ChunkingOptions"/> from
    /// these two properties and hands it straight to a strategy, so it never met
    /// <c>ChunkingOptionsValidator</c> — the ingestion-time check only ever saw the <i>main</i>
    /// options. With <c>ParentChunkSize = 0</c> both strategies loop forever:
    /// <c>RecursiveChunkingStrategy</c> advances its index by the chunk size, and
    /// <c>FixedSizeChunkingStrategy</c>'s own guard falls back to the same zero it was guarding
    /// against. Ingestion hangs with no error and no progress.
    /// </para>
    /// <para>
    /// Validated by constructing the very options the behaviour will construct and running the
    /// main path's rule over them, so the two paths cannot drift into disagreeing about what a
    /// valid chunk size is — that drift being what caused this.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">The sizing is one no strategy can make progress on.</exception>
    private static void ValidateParentDocumentOptions(ParentDocumentOptions options)
    {
        try
        {
            ValidateChunkingOptions(new ChunkingOptions
            {
                MaxChunkSize = options.ParentChunkSize,
                Overlap = options.ParentOverlap,
            });
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                "ParentDocumentOptions describes a chunking pass that cannot run: " + ex.Message +
                " (ParentChunkSize maps to MaxChunkSize and ParentOverlap to Overlap.)",
                nameof(options),
                ex);
        }
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
    /// <param name="replacesTypeNames">
    /// The same override as <paramref name="replaces"/>, expressed by full type name rather than by
    /// <see cref="Type"/>. Exists for a caller that cannot reference the replaced parser's assembly
    /// at all — <c>Rag.NET.Chunking.Templates</c> overriding <c>Rag.NET.Parsers.Office</c>'s Excel
    /// parser, which may not even be installed, is the motivating case. Matched the same way as
    /// <paramref name="replaces"/> — by full type name, and a name with no matching registration
    /// removes nothing and is not an error.
    /// </param>
    /// <remarks>
    /// When <typeparamref name="TParser"/> implements <see cref="IDeclaresContentTypes"/>, this also
    /// declares one <see cref="ParserClaim"/> per content type it reports — see
    /// <see cref="DeclareContentTypeClaims{TParser}"/>. A <typeparamref name="TParser"/> that does
    /// not implement it declares nothing, exactly as before that interface existed.
    /// </remarks>
    public RagBuilder AddParser<TParser>(Type? replaces = null, string[]? replacesTypeNames = null)
        where TParser : class, IDocumentParser
    {
        if (replaces is not null)
        {
            RemoveReplacedParser(replaces.FullName ?? replaces.Name);
        }

        if (replacesTypeNames is not null)
        {
            foreach (var replacedTypeName in replacesTypeNames)
            {
                RemoveReplacedParser(replacedTypeName);
            }
        }

        Services.AddSingleton<IDocumentParser, TParser>();
        DeclareContentTypeClaims<TParser>(replaces, replacesTypeNames);
        return this;
    }

    /// <summary>
    /// Declares one <see cref="ParserClaim"/> per content type <typeparamref name="TParser"/>
    /// reports through <see cref="IDeclaresContentTypes"/> — the mechanism that closes
    /// <c>AddParser&lt;TParser&gt;()</c>'s documented blindness for any parser that opts in. A
    /// <typeparamref name="TParser"/> that does not implement <see cref="IDeclaresContentTypes"/>
    /// declares nothing, which is this method's no-op path and by far its most common one.
    /// </summary>
    /// <remarks>
    /// <paramref name="replaces"/> is recorded on every claim this declares: a single
    /// <c>AddParser&lt;TParser&gt;(replaces:)</c> call names one specific parser it deliberately
    /// overrides, and every content type <typeparamref name="TParser"/> claims in that same call is
    /// claimed as part of that override. <paramref name="replacesTypeNames"/> carries that same
    /// unambiguous meaning only when it names exactly one parser; naming several leaves no single
    /// answer to "which one did this claim override", so with more than one name the declared claims
    /// record no override rather than attributing it to an arbitrary one of them. Nothing in this
    /// repository calls <c>AddParser&lt;TParser&gt;(replacesTypeNames:)</c> with more than one name
    /// for a parser that also implements <see cref="IDeclaresContentTypes"/> today; this is the
    /// documented behaviour if one ever does.
    /// </remarks>
    private void DeclareContentTypeClaims<TParser>(Type? replaces, string[]? replacesTypeNames)
        where TParser : class, IDocumentParser
    {
        if (!typeof(IDeclaresContentTypes).IsAssignableFrom(typeof(TParser)))
        {
            return;
        }

        var contentTypes = (IReadOnlyCollection<string>)GetDeclaredContentTypesMethod
            .MakeGenericMethod(typeof(TParser))
            .Invoke(obj: null, parameters: null)!;
        var registrationMethod = $"AddParser<{typeof(TParser).Name}>()";
        var replacedTypeName = replacesTypeNames is { Length: 1 } single ? single[0] : null;

        foreach (var contentType in contentTypes)
        {
            Services.AddSingleton(ParserClaim.For<TParser>(
                contentType, registrationMethod, replaces: replaces, replacesTypeName: replacedTypeName));
        }
    }

    /// <summary>
    /// Reflection bridge for <see cref="DeclareContentTypeClaims{TParser}"/>: a static abstract
    /// interface member can only be invoked through a generic type parameter constrained by that
    /// interface, and <typeparamref name="TParser"/> there carries no such constraint — it is only
    /// known at that call site to implement <see cref="IDocumentParser"/>, with
    /// <see cref="IDeclaresContentTypes"/> checked at runtime because most callers of
    /// <c>AddParser&lt;TParser&gt;()</c> do not implement it. <see cref="GetDeclaredContentTypes{T}"/>
    /// carries the constraint the direct call site cannot, and is reached through
    /// <see cref="MethodInfo.MakeGenericMethod"/> once the runtime check confirms it applies.
    /// </summary>
    private static readonly MethodInfo GetDeclaredContentTypesMethod = typeof(RagBuilder).GetMethod(
        nameof(GetDeclaredContentTypes), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static IReadOnlyCollection<string> GetDeclaredContentTypes<TParser>()
        where TParser : IDeclaresContentTypes =>
        TParser.ContentTypes;

    /// <summary>
    /// Removes the parser named <paramref name="replacedTypeName"/>'s <see cref="IDocumentParser"/>
    /// registration and every <see cref="ParserClaim"/> declared for it, so a caller of
    /// <see cref="AddParser{TParser}(Type?, string[]?)"/> that names it — by <see cref="Type"/> or by
    /// name — actually wins selection rather than merely avoiding the conflict check. A name that
    /// matches nothing currently registered removes nothing, which is the no-op an optional
    /// dependency that was never added requires.
    /// <para>
    /// <b>Only reaches parsers registered by type.</b> Matching is on
    /// <see cref="ServiceDescriptor.ImplementationType"/>, which is <see langword="null"/> for a
    /// factory registration — <c>AddSingleton&lt;IDocumentParser&gt;(sp =&gt; …)</c>, as
    /// <c>Rag.NET.Parsers.Vision</c>, <c>.Email</c>, <c>.Archive</c> and this package's own
    /// registrations use. A factory descriptor does not say what type it will produce without
    /// resolving it, so naming one here removes nothing and looks identical to naming a package
    /// that was never installed. Both parsers this repository replaces today
    /// (<c>CsvDocumentParser</c>, <c>ExcelDocumentParser</c>) arrive through
    /// <see cref="AddParser{TParser}(Type?, string[]?)"/> and are therefore reachable; a caller
    /// trying to replace a factory-registered parser gets a silent no-op, which is the failure
    /// shape this whole mechanism exists to remove. Recorded as debt rather than fixed here.
    /// </para>
    /// </summary>
    private void RemoveReplacedParser(string replacedTypeName)
    {
        for (var i = Services.Count - 1; i >= 0; i--)
        {
            var descriptor = Services[i];

            var isReplacedParser =
                descriptor.ServiceType == typeof(IDocumentParser) &&
                string.Equals(descriptor.ImplementationType?.FullName, replacedTypeName, StringComparison.Ordinal);

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
    IRagBuilder IRagBuilder.AddParser<TParser>(Type? replaces, string[]? replacesTypeNames) =>
        AddParser<TParser>(replaces, replacesTypeNames);

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
    /// <exception cref="ArgumentException">
    /// <see cref="DeepResearchOptions.MaxDepth"/> or <see cref="DeepResearchOptions.SubQueryCount"/>
    /// is not positive — the generated <c>DeepResearchOptionsValidator</c> rejects the
    /// registration at the configuring line, per the constraints documented on those properties.
    /// </exception>
    public RagBuilder UseDeepResearch(DeepResearchOptions? options = null)
    {
        var effective = options ?? new DeepResearchOptions();
        ThrowIfInvalid(new DeepResearchOptionsValidator().Validate(effective), nameof(options), "deep-research");
        Services.AddSingleton(effective);
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
    /// <exception cref="ArgumentException">
    /// <see cref="TagRetrievalOptions.TopK"/> is not positive, or
    /// <see cref="TagRetrievalOptions.MinScore"/> is outside the cosine range — the generated
    /// <c>TagRetrievalOptionsValidator</c> rejects the registration at the configuring line,
    /// per the constraints documented on those properties.
    /// </exception>
    public RagBuilder UseTagRetrieval(TagRetrievalOptions? options = null)
    {
        var effective = options ?? new TagRetrievalOptions();
        ThrowIfInvalid(new TagRetrievalOptionsValidator().Validate(effective), nameof(options), "tag-retrieval");
        Services.AddSingleton(effective);
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
    /// <exception cref="ArgumentException">
    /// <see cref="TimeWeightedOptions.DecayRate"/> is negative or not finite — the generated
    /// <c>TimeWeightedOptionsValidator</c> rejects the registration at the configuring line,
    /// per the constraint documented on that property.
    /// </exception>
    public RagBuilder UseTimeWeighting(TimeWeightedOptions? options = null)
    {
        var effective = options ?? new TimeWeightedOptions();
        ThrowIfInvalid(new TimeWeightedOptionsValidator().Validate(effective), nameof(options), "time-weighting");
        Services.AddSingleton(effective);
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
        ValidateParentDocumentOptions(options);
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
