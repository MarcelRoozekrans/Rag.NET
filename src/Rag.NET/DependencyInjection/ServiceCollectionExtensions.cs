using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Search;

namespace Rag.NET.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNet(
        this IServiceCollection services,
        Action<RagBuilder>? configure = null,
        Action<IngestionPipelineBuilder>? ingestion = null,
        Action<RetrievalPipelineBuilder>? retrieval = null)
    {
        // ZeroAlloc.Inject-generated: registers IDocumentParser (Text, Markdown),
        // IChunkingStrategy (Recursive), all [Singleton] behaviors,
        // PipelineIngestor (as IIngestor), PipelineRetriever (as IRetriever).
        services.AddRagNETServices();
        DeclareBuiltInParserClaims(services);

        services.TryAddSingleton<ChunkingOptions>();
        services.AddSingleton<InMemoryBm25Index>(sp => new InMemoryBm25Index(sp.GetService<SynonymMap>()));

        // Build and register pipelines — behaviors are resolved from the container by builders
        var ingestionBuilder = new IngestionPipelineBuilder();
        ingestion?.Invoke(ingestionBuilder);
        services.AddSingleton(sp => ingestionBuilder.Build(sp));

        var retrievalBuilder = new RetrievalPipelineBuilder();
        retrieval?.Invoke(retrievalBuilder);
        services.AddSingleton(retrievalBuilder);
        services.AddSingleton(sp => retrievalBuilder.Build(sp));

        services.AddSingleton<RagPipeline>(sp =>
        {
            var r = sp.GetRequiredService<IRetriever>();
            var i = sp.GetRequiredService<IIngestor>();
            IAnswerEngine? answerEngine = sp.GetService<IAnswerEngine>();
            if (answerEngine is null && sp.GetService<IChatClient>() is not null)
            {
                answerEngine = ChatAnswerEngine.CreateFromServices(sp);
            }
            var pipelineLogger = sp.GetService<ILogger<RagPipeline>>();
            return new RagPipeline(r, i, answerEngine, pipelineLogger);
        });
        services.AddSingleton<IRagPipeline>(sp => sp.GetRequiredService<RagPipeline>());

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);
        ValidateParserClaims(services);
        WireRefinementStrategy(services);
        WireDeepResearch(services);
        WireTimeWeighting(services);
        WireTagRetrieval(services);

        // Default fallback — no-op when UseSqlitePersistence() has already registered IBm25Index.
        services.TryAddSingleton<IBm25Index>(sp => sp.GetRequiredService<InMemoryBm25Index>());

        return services;
    }

    /// <summary>
    /// Declares the claims of the two parsers <c>AddRagNETServices()</c> auto-registers, so
    /// <see cref="ValidateParserClaims"/> can see them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared here rather than beside the parsers because a source generator writes their
    /// registrations — <c>[Singleton(As = typeof(IDocumentParser), AllowMultiple = true)]</c> — and
    /// its output cannot host a claim. This call is the next best place: every route into the
    /// container goes through <see cref="AddRagNet"/>, and it runs before <c>configure</c>, which
    /// is also the order the registrations themselves run in.
    /// </para>
    /// <para>
    /// Without these two claims the guard was blind to the case a user is most likely to hit.
    /// Registering a parser that declares <c>text/plain</c> left one <i>declared</i> claimant, so
    /// nothing fired, while parser selection resolved <c>text/plain</c> to
    /// <see cref="TextDocumentParser"/> — auto-registered before anything the user adds — and the
    /// user's parser silently never ran. That is precisely the failure the guard exists to prevent,
    /// and it was reachable without a third-party package.
    /// </para>
    /// <para>
    /// The content types are copied from each <c>CanParse</c> and are exactly what it accepts —
    /// <see cref="MarkdownDocumentParser"/> answers <c>text/x-markdown</c> as well as
    /// <c>text/markdown</c>, and a claim that under-declares is a guard that under-fires. Neither
    /// call bundles anything with the parser, so neither declares a
    /// <see cref="ParserClaim.ParserOptOut"/>.
    /// </para>
    /// </remarks>
    private static void DeclareBuiltInParserClaims(IServiceCollection services)
    {
        services.AddSingleton(ParserClaim.For<TextDocumentParser>(
            "text/plain", BuiltInRegistrationMethod));
        services.AddSingleton(ParserClaim.For<MarkdownDocumentParser>(
            "text/markdown", BuiltInRegistrationMethod));
        services.AddSingleton(ParserClaim.For<MarkdownDocumentParser>(
            "text/x-markdown", BuiltInRegistrationMethod));
    }

    /// <summary>
    /// The call a user would recognise as having registered the built-in parsers. They arrive
    /// through <c>AddRagNETServices()</c>, which is generated and which nobody calls directly.
    /// </summary>
    private const string BuiltInRegistrationMethod = "AddRagNet()";

    /// <summary>
    /// Fails registration when two parsers declare a claim on the same content type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs after <c>configure</c> so it sees the final registration set whatever order the user
    /// called things in, and before the <c>Wire*</c> methods so a misconfiguration is reported
    /// rather than wired around.
    /// </para>
    /// <para>
    /// Parser selection takes the <i>first</i> registration whose <c>CanParse</c> matches, both at
    /// top level (<see cref="ParseBehavior"/>) and for email attachments. Two claimants therefore
    /// means one of them silently never runs — measured on a 3-level nested <c>.eml</c> as 2
    /// sections instead of 6. This does not pick a winner: the two parsers that collide today
    /// serve genuinely different purposes, so the error asks the user.
    /// </para>
    /// <para>
    /// Duplicate claims from the <i>same</i> parser type are not a conflict. Calling
    /// <c>AddEmailParser()</c> twice is documented as legal, and the second call declares the same
    /// claims the first did.
    /// </para>
    /// </remarks>
    private static void ValidateParserClaims(IServiceCollection services)
    {
        // Sorted rather than hashed so a container with several conflicts always reports the same
        // one, and so the claimants are listed in a stable order. Grouping is on the content type
        // case-insensitively on purpose, and deliberately stricter than the parsers themselves:
        // they do not agree with each other. QAPairsDocumentParser compares Ordinal, so TEXT/CSV
        // reaches nobody, while Rag.NET.Parsers.Email's pair compares OrdinalIgnoreCase. Grouping
        // the strict way would let a pair that differs only in case register cleanly and then
        // collide at parse time for whichever casing both happen to answer. Over-strict costs a
        // false conflict between two parsers that would never have overlapped; case-sensitive costs
        // a silent one.
        SortedDictionary<string, SortedDictionary<string, ParserClaim>>? byContentType = null;
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(ParserClaim) ||
                descriptor.ImplementationInstance is not ParserClaim claim)
            {
                continue;
            }

            byContentType ??= new SortedDictionary<string, SortedDictionary<string, ParserClaim>>(
                StringComparer.OrdinalIgnoreCase);
            if (!byContentType.TryGetValue(claim.ContentType, out var claimants))
            {
                claimants = new SortedDictionary<string, ParserClaim>(StringComparer.Ordinal);
                byContentType.Add(claim.ContentType, claimants);
            }

            // Keyed by parser type, so the same package registered twice declares one claimant.
            claimants[claim.ParserTypeName] = claim;
        }

        if (byContentType is null)
            return;

        foreach (var (contentType, claimants) in byContentType)
        {
            if (claimants.Count > 1)
                throw new InvalidOperationException(DescribeParserClaimConflict(contentType, claimants));
        }
    }

    /// <summary>
    /// Builds the startup error, naming every claimant, the call that registered it, and — where
    /// the claim declares one — the way to keep that call while dropping only its parser.
    /// </summary>
    /// <remarks>
    /// "Register only one of them" is not always advice the user can take. Some registration calls
    /// bundle a parser with a chunking strategy, so removing the call to resolve the conflict also
    /// removes something the user wanted and the conflict had nothing to do with. Those calls
    /// declare a <see cref="ParserClaim.ParserOptOut"/>, and the message repeats it verbatim so it
    /// can be pasted. Calls that register nothing but a parser declare none, and nothing is
    /// offered for them.
    /// </remarks>
    private static string DescribeParserClaimConflict(
        string contentType,
        SortedDictionary<string, ParserClaim> claimants)
    {
        var message = new StringBuilder();
        message.Append("More than one registered parser claims the content type '")
            .Append(contentType)
            .Append("'. The pipeline uses the first registered parser whose CanParse matches, so ")
            .Append("which one wins depends on registration order and the other never runs. ")
            .Append("Claimed by:");

        var anyOptOut = false;
        foreach (var claimant in claimants.Values)
        {
            message.Append("\n  - ")
                .Append(claimant.ParserTypeName)
                .Append(", registered by ")
                .Append(claimant.RegistrationMethod);

            if (claimant.ParserOptOut is not { Length: > 0 } optOut)
                continue;

            anyOptOut = true;
            message.Append("\n      to keep that registration without its parser, use ")
                .Append(optOut);
        }

        message.Append("\nRegister only one of them");
        message.Append(anyOptOut
            ? ", or keep both and opt one out of registering its parser as shown above."
            : ".");
        return message.ToString();
    }

    /// <summary>
    /// Replaces the ZeroAlloc-generated <see cref="ParseBehavior"/> singleton registration with a
    /// factory that wires <see cref="ParseBehavior.RefinementStrategy"/> from DI when an
    /// <see cref="IChunkRefinementStrategy"/> is registered.
    /// <see cref="ParseBehavior.RefinementStrategy"/> cannot use <c>[Inject]</c> because
    /// ZeroAlloc.Inject calls <c>GetRequiredService</c> for all injected properties, which
    /// would throw when no refinement strategy is configured.
    /// </summary>
    private static void WireRefinementStrategy(IServiceCollection services) =>
        services.AddSingleton<ParseBehavior>(sp => new ParseBehavior
        {
            Parsers = sp.GetRequiredService<IEnumerable<IDocumentParser>>(),
            ChunkingStrategy = sp.GetRequiredService<IChunkingStrategy>(),
            ChunkingOptions = sp.GetRequiredService<ChunkingOptions>(),
            RefinementStrategy = sp.GetService<IChunkRefinementStrategy>(),
        });

    private static void WireDeepResearch(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(DeepResearchOptions)))
            return;

        // PipelineRetriever is registered only as IRetriever by ZeroAlloc ([Singleton(As = typeof(IRetriever))]).
        // Register it by its concrete type with manually-wired [Inject] properties so the decorator can wrap it.
        // NOTE: This registers a second PipelineRetriever instance separate from the one
        // ZeroAlloc registered as IRetriever. The generated IRetriever→PipelineRetriever
        // registration is superseded by the decorator below, so the orphaned instance is
        // never used. This is a known limitation of decorating ZeroAlloc-generated
        // registrations; PipelineRetriever holds only a Pipeline<> reference so the
        // extra instance carries no cost beyond memory.
        services.AddSingleton<PipelineRetriever>(sp => new PipelineRetriever
        {
            Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
            Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
        });

        // Register as concrete type so WireTagRetrieval can resolve it for stacking
        services.AddSingleton<DeepResearchRetriever>(sp => new DeepResearchRetriever(
            sp.GetRequiredService<PipelineRetriever>(),
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<DeepResearchOptions>(),
            sp.GetService<ILogger<DeepResearchRetriever>>()));

        // Replace IRetriever with the decorator (superseded by WireTagRetrieval if both are used)
        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<DeepResearchRetriever>());
    }

    private static void WireTimeWeighting(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeWeightedOptions)))
            return;

        // DeepResearchRetriever descriptor is registered by WireDeepResearch (called above in AddRagNet).
        // Ordering is load-bearing: WireDeepResearch must run before WireTimeWeighting.
        bool hasDeepResearch = services.Any(d => d.ServiceType == typeof(DeepResearchRetriever));

        // When DeepResearch is not wired, PipelineRetriever may not be registered as its own
        // concrete type. Register it here so TimeWeightedRetriever can wrap it — same pattern
        // as WireDeepResearch.
        if (!hasDeepResearch)
        {
            services.TryAddSingleton<PipelineRetriever>(sp => new PipelineRetriever
            {
                Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
                Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
            });
        }

        services.AddSingleton<TimeWeightedRetriever>(sp =>
        {
            IRetriever inner = hasDeepResearch
                ? sp.GetRequiredService<DeepResearchRetriever>()
                : (IRetriever)sp.GetRequiredService<PipelineRetriever>();

            return new TimeWeightedRetriever(
                inner,
                sp.GetRequiredService<TimeWeightedOptions>(),
                sp.GetService<ILogger<TimeWeightedRetriever>>());
        });

        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<TimeWeightedRetriever>());
    }

    private static void WireTagRetrieval(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TagRetrievalOptions)))
            return;

        // DeepResearchRetriever and TimeWeightedRetriever descriptors are registered by their
        // respective Wire* methods (called above in AddRagNet).
        // Ordering is load-bearing: WireDeepResearch and WireTimeWeighting must run before WireTagRetrieval.
        bool hasDeepResearch = services.Any(d => d.ServiceType == typeof(DeepResearchRetriever));
        bool hasTimeWeighted = services.Any(d => d.ServiceType == typeof(TimeWeightedRetriever));

        // When neither DeepResearch nor TimeWeighted is wired, PipelineRetriever was never
        // registered as its concrete type (ZeroAlloc registers it only as IRetriever).
        // Register it here so TagRetriever can wrap it — same pattern as WireDeepResearch.
        if (!hasDeepResearch && !hasTimeWeighted)
        {
            services.TryAddSingleton<PipelineRetriever>(sp => new PipelineRetriever
            {
                Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
                Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
            });
        }

        // Stacking order (outermost first):
        // TagRetriever → TimeWeightedRetriever → DeepResearchRetriever → PipelineRetriever
        services.AddSingleton<TagRetriever>(sp =>
        {
            IRetriever inner;
            if (hasTimeWeighted)
                inner = sp.GetRequiredService<TimeWeightedRetriever>();
            else if (hasDeepResearch)
                inner = sp.GetRequiredService<DeepResearchRetriever>();
            else
                inner = sp.GetRequiredService<PipelineRetriever>();

            return new TagRetriever(
                inner,
                sp.GetRequiredService<ITagIndex>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<TagRetrievalOptions>(),
                sp.GetService<ILogger<TagRetriever>>());
        });

        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<TagRetriever>());
    }
}
