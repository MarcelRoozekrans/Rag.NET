using System.Runtime.InteropServices;
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
        // Both builders are registered as instances, not just as the factories that read them.
        // The instance is the seam a Use* method in another package places its behaviour through
        // (see PipelineBuilderAccessors). Only the retrieval half used to be registered, so
        // UseRaptor, UseGraphRag and UseMindMapExtraction could reach nothing and silently
        // enabled nothing — issue #191. Build runs lazily on first resolution, so a Use* method
        // running later in configure still changes what the container composes.
        var ingestionBuilder = new IngestionPipelineBuilder();
        ingestion?.Invoke(ingestionBuilder);
        services.AddSingleton(ingestionBuilder);
        services.AddSingleton(sp => ingestionBuilder.Build(sp));

        var retrievalBuilder = new RetrievalPipelineBuilder();
        retrieval?.Invoke(retrievalBuilder);
        services.AddSingleton(retrievalBuilder);
        services.AddSingleton(sp => retrievalBuilder.Build(sp));

        // Answer-engine decorations are applied here rather than by the packages that add them,
        // so an audit or trace decorator wraps the engine that is actually used whichever order
        // the calls were made in — including a chat client registered after AddRagNet returned.
        var answerEngineDecorations = new AnswerEngineDecorationBuilder();
        services.AddSingleton(answerEngineDecorations);
        services.AddSingleton(sp => new ComposedAnswerEngine(ComposeAnswerEngine(sp, answerEngineDecorations)));

        services.AddSingleton<RagPipeline>(BuildPipeline);
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

    /// <summary>Builds the pipeline, after checking that every decoration the user asked for applies.</summary>
    /// <param name="serviceProvider">The provider the pipeline is resolved from.</param>
    /// <returns>The pipeline.</returns>
    /// <remarks>
    /// The composition check runs first and throws: a container whose <c>ConfigureResilience</c>,
    /// <c>UseCostBudgeting</c> or <c>UseFallbackChain</c> call decorated nothing is misconfigured,
    /// and the whole point of issue #195 is that it must not keep working quietly. This is the one
    /// place every entry point passes through, and the last moment at which the registrations made
    /// after <c>AddRagNet</c> returned are all visible.
    /// </remarks>
    private static RagPipeline BuildPipeline(IServiceProvider serviceProvider)
    {
        serviceProvider.GetService<CompositionClaimRegistry>()?.Validate(serviceProvider);

        return new RagPipeline(
            serviceProvider.GetRequiredService<IRetriever>(),
            serviceProvider.GetRequiredService<IIngestor>(),
            serviceProvider.GetRequiredService<ComposedAnswerEngine>().Engine,
            serviceProvider.GetService<ILogger<RagPipeline>>());
    }

    /// <summary>Picks the answer engine and applies every decoration registered against it.</summary>
    /// <param name="serviceProvider">The provider to resolve from.</param>
    /// <param name="decorations">The decorations collected during registration.</param>
    /// <returns>
    /// The decorated engine, or <see langword="null"/> when neither an <see cref="IAnswerEngine"/>
    /// nor an <see cref="IChatClient"/> is registered — a retrieval-only pipeline, which stays one.
    /// </returns>
    private static IAnswerEngine? ComposeAnswerEngine(
        IServiceProvider serviceProvider,
        AnswerEngineDecorationBuilder decorations)
    {
        IAnswerEngine? answerEngine = serviceProvider.GetService<IAnswerEngine>();
        if (answerEngine is null && serviceProvider.GetService<IChatClient>() is not null)
        {
            answerEngine = ChatAnswerEngine.CreateFromServices(serviceProvider);
        }

        return decorations.Apply(answerEngine, serviceProvider);
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

    /// <summary>
    /// Declares services shared by every named pipeline — an embedding model, an
    /// <see cref="Microsoft.Extensions.AI.IChatClient"/>, anything expensive enough that one per
    /// pipeline would be wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses the same <see cref="RagBuilder"/> and the same <c>Use*</c> methods as <c>AddRagNet</c>,
    /// because <see cref="RagBuilder"/> is a wrapper over an <see cref="IServiceCollection"/> and
    /// nothing about those methods is tied to a pipeline being registered.
    /// </para>
    /// <para>
    /// <b>It deliberately does not register a pipeline.</b> Sharing a model is not the same as
    /// running a pipeline in the root container: one that did would build its own stores alongside
    /// every child's.
    /// </para>
    /// <para>
    /// Four types hold an ONNX <c>InferenceSession</c> — the embedding generator, the token
    /// embedding generator, the SPLADE encoder and the reranker — and MiniLM alone is roughly 90 MB.
    /// Five named pipelines each calling <c>UseOnnxEmbeddings</c> would load it five times, which is
    /// the concrete reason this exists (#342).
    /// </para>
    /// <para>
    /// <b>It is never required.</b> A named pipeline already inherits the host's own singleton
    /// registrations on the collection, so <c>services.AddEmbeddingGenerator(…)</c> reaches it
    /// without this. What declaring a type here adds is that it <i>replaces</i> whatever the named
    /// block registered — "one of these for every pipeline" — which is the difference between
    /// sharing a 90 MB model and letting each pipeline choose its own.
    /// </para>
    /// <para>
    /// <b>Only singleton, non-keyed, closed-generic registrations cross into a child</b>, whether
    /// declared here or inherited from the host. A transient, a scoped registration, a keyed one, or
    /// an open generic such as <c>IOptions&lt;&gt;</c> or <c>ILogger&lt;&gt;</c> stays in the root
    /// and is never reachable from a named pipeline's provider; a named pipeline that needs one must
    /// register it in its own <c>AddRagNet(name, …)</c> block instead. See the "Named pipelines"
    /// section of the architecture guide for the full contract, including precedence and how
    /// forwarding behaves for a type with more than one root registration.
    /// </para>
    /// </remarks>
    /// <param name="services">The root service collection.</param>
    /// <param name="configure">Registers the shared services, using the usual <c>Use*</c> methods.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddRagNetShared(
        this IServiceCollection services, Action<RagBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Snapshot around the callback so only what it declared is forwarded. The root collection
        // also holds the host's own logging, configuration and HttpClients; forwarding those would
        // make every child depend on the host's container shape.
        var before = services.Count;
        configure(new RagBuilder(services));

        var declared = new List<ServiceDescriptor>();
        for (var i = before; i < services.Count; i++)
        {
            declared.Add(services[i]);
        }

        var shared = FindOrAddSharedServiceTypes(services);
        shared.AddRange(declared);
        return services;
    }

    /// <summary>
    /// Gets the collection's <see cref="SharedServiceTypes"/>, adding it on first use.
    /// </summary>
    /// <remarks>
    /// Held as a singleton <i>instance</i> so both <c>AddRagNetShared</c> and <c>AddRagNet(name, …)</c>
    /// see the same object at registration time, before any provider exists — and so a named block
    /// declared before the shared block still forwards correctly.
    /// </remarks>
    /// <param name="services">The root service collection.</param>
    /// <returns>The single instance for this collection.</returns>
    private static SharedServiceTypes FindOrAddSharedServiceTypes(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(SharedServiceTypes)
                && descriptor.ImplementationInstance is SharedServiceTypes existing)
            {
                return existing;
            }
        }

        var created = new SharedServiceTypes();
        services.AddSingleton(created);
        return created;
    }

    /// <summary>
    /// Registers a named RAG pipeline with its own service provider, reached through
    /// <see cref="IRagPipelineFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The named block composes into its own <see cref="IServiceCollection"/>, so its vector store,
    /// BM25 index, caches and behaviours are separate from every other name's. Every <c>Use*</c>
    /// method works unchanged, because they all operate on a collection.
    /// </para>
    /// <para>
    /// Service types declared through <see cref="AddRagNetShared"/> are forwarded to the root
    /// provider rather than registered again, so one embedding model serves every pipeline.
    /// </para>
    /// <para>
    /// The unnamed <c>AddRagNet</c> is unaffected and still registers into the root container (#342).
    /// </para>
    /// </remarks>
    /// <param name="services">The root service collection.</param>
    /// <param name="name">The pipeline's name, passed later to <see cref="IRagPipelineFactory.Get"/>.</param>
    /// <param name="configure">Configures this pipeline, with the usual <c>Use*</c> methods.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddRagNet(
        this IServiceCollection services, string name, Action<RagBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var shared = FindOrAddSharedServiceTypes(services);
        var named = FindOrAddNamedCollections(services);
        if (named.ContainsKey(name))
        {
            throw new ArgumentException(
                $"A RAG pipeline named '{name}' is already registered.", nameof(name));
        }

        var inner = new ServiceCollection();
        _ = inner.AddRagNet(configure);
        named[name] = new NamedPipelineRegistration(inner, shared);

        services.TryAddSingleton<IRagPipelineFactory>(sp => BuildFactory(named, services, sp));
        return services;
    }

    /// <summary>
    /// Gets the collection's name-to-registration map, adding it on first use. Mirrors
    /// <see cref="FindOrAddSharedServiceTypes"/>: held as a singleton instance so every
    /// <c>AddRagNet(name, …)</c> call sees the same map, regardless of call order.
    /// </summary>
    /// <param name="services">The root service collection.</param>
    /// <returns>The single instance for this collection.</returns>
    private static Dictionary<string, NamedPipelineRegistration> FindOrAddNamedCollections(
        IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(Dictionary<string, NamedPipelineRegistration>)
                && descriptor.ImplementationInstance is Dictionary<string, NamedPipelineRegistration> existing)
            {
                return existing;
            }
        }

        var created = new Dictionary<string, NamedPipelineRegistration>(StringComparer.Ordinal);
        services.AddSingleton(created);
        return created;
    }

    /// <summary>Applies shared-service forwarding and constructs the factory.</summary>
    /// <remarks>
    /// Forwarding happens here, not at registration: the descriptors close over the root provider,
    /// which only exists once the container is built. See <see cref="ForwardSharedServices"/> for
    /// how each entry is forwarded.
    /// </remarks>
    /// <param name="named">Each name's registration.</param>
    /// <param name="rootProvider">The root provider forwarded services resolve from.</param>
    /// <returns>The factory.</returns>
    private static RagPipelineFactory BuildFactory(
        Dictionary<string, NamedPipelineRegistration> named,
        IServiceCollection rootServices,
        IServiceProvider rootProvider)
    {
        var collections = new Dictionary<string, IServiceCollection>(StringComparer.Ordinal);
        var shared = new List<Type>();
        foreach (var (name, registration) in named)
        {
            collections[name] = registration.Services;

            // Every registration points at the same SharedServiceTypes instance, so one pass fills
            // this; it needs no resolution, only the declared types, so it is safe here.
            if (shared.Count == 0)
            {
                shared.AddRange(registration.Shared.Entries.Select(e => e.ServiceType));
            }
        }

        // Forwarding is deferred to the first Get(name) rather than done here, and that is a
        // correctness requirement rather than a lazy-loading preference. This method IS the factory
        // delegate for IRagPipelineFactory: resolving root singletons from inside it re-enters the
        // container for a service that is still being constructed, and a root singleton that
        // depends on IRagPipelineFactory then deadlocks outright (#396). Resolving at Get(name)
        // happens after this factory is fully constructed, so the same registration resolves
        // normally. It also means Contains(name) no longer constructs anything.
        return new RagPipelineFactory(
            collections,
            name =>
            {
                var registration = named[name];
                ForwardSharedServices(registration, rootProvider);
                ForwardAmbientRootServices(registration, rootServices, rootProvider);
            },
            shared);
    }

    /// <summary>
    /// Forwards the host's own root singletons into a named pipeline, for any service type the
    /// pipeline did not register itself (#390).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the named form saw less than the unnamed one for the same registrations, which
    /// is the bug #390 reported. <c>AddRagNet(rag => …)</c> registers into the caller's own
    /// collection, so an embedder registered there is simply visible; a named pipeline builds a
    /// child container and saw only what <c>AddRagNetShared</c> declared. The canonical
    /// <c>Microsoft.Extensions.AI</c> pattern — <c>services.AddChatClient(…)</c>,
    /// <c>services.AddEmbeddingGenerator(…)</c> — writes straight to the root collection, outside
    /// that block, and this library's own <c>Rag.NET.Hosting</c> package registers the same way.
    /// </para>
    /// <para>
    /// <b>The child wins, unlike <see cref="ForwardSharedServices"/>.</b> A declared-shared type
    /// replaces whatever the child registered, because declaring it shared says "one of these for
    /// every pipeline". An ambient root service is the opposite: it is a default the pipeline may
    /// override, so a type the child already registers is left alone. That is also what keeps
    /// pipelines isolated — the child collection is built by <c>inner.AddRagNet(configure)</c> and
    /// therefore already registers every service type this library owns, so another pipeline's
    /// store, chunker or behaviours can never arrive here.
    /// </para>
    /// <para>
    /// <b>Two types are excluded by name, and one of them would otherwise deadlock.</b> This runs
    /// <i>inside</i> the factory that resolves <see cref="IRagPipelineFactory"/>, so resolving that
    /// type from the root here would re-enter the factory being constructed.
    /// <see cref="SharedServiceTypes"/> and the name-to-registration map are registration-time
    /// bookkeeping that no pipeline resolves.
    /// </para>
    /// <para>
    /// <b>Forwarding is eager, and that is a real cost.</b> Each forwarded type is resolved from
    /// the root now, because an instance descriptor is the only shape that keeps ownership with the
    /// root — see <see cref="ForwardSharedServices"/> for why a factory descriptor would make a
    /// child dispose what it does not own. So resolving <see cref="IRagPipelineFactory"/> now
    /// constructs the host's eligible root singletons, not just the shared ones. Open generics,
    /// keyed registrations and non-singletons are skipped, which excludes the bulk of a host's
    /// container — <c>ILogger&lt;&gt;</c> and <c>IOptions&lt;&gt;</c> among them.
    /// </para>
    /// </remarks>
    /// <param name="registration">The name's composed collection.</param>
    /// <param name="rootServices">The root collection, read for what the host registered.</param>
    /// <param name="rootProvider">The root provider forwarded services resolve from.</param>
    private static void ForwardAmbientRootServices(
        NamedPipelineRegistration registration,
        IServiceCollection rootServices,
        IServiceProvider rootProvider)
    {
        var childTypes = new HashSet<Type>();
        foreach (var descriptor in registration.Services)
        {
            childTypes.Add(descriptor.ServiceType);
        }

        var forwarded = new HashSet<Type>();
        foreach (var descriptor in rootServices)
        {
            var serviceType = descriptor.ServiceType;
            if (descriptor.IsKeyedService
                || descriptor.Lifetime != ServiceLifetime.Singleton
                || serviceType.IsGenericTypeDefinition
                || serviceType == typeof(IRagPipelineFactory)
                || serviceType == typeof(SharedServiceTypes)
                || serviceType == typeof(Dictionary<string, NamedPipelineRegistration>)
                || childTypes.Contains(serviceType)
                || !forwarded.Add(serviceType))
            {
                continue;
            }

            // Every instance for the type, for the same reason ForwardSharedServices takes them
            // all: a multi-registered service type would otherwise arrive here truncated.
            List<object>? instances = null;
            foreach (var instance in rootProvider.GetServices(serviceType))
            {
                if (instance is not null)
                {
                    (instances ??= []).Add(instance);
                }
            }

            if (instances is null)
            {
                continue;
            }

            foreach (ref readonly var instance in CollectionsMarshal.AsSpan(instances))
            {
                registration.Services.Add(ServiceDescriptor.Singleton(serviceType, instance));
            }
        }
    }

    /// <summary>Forwards one named pipeline's declared-shared services from the root provider.</summary>
    /// <remarks>
    /// <para>
    /// Skips any entry that is an open generic (<c>IOptions&lt;&gt;</c> can never be resolved by
    /// <c>GetRequiredService(closedType)</c> — <c>AddHttpClient()</c> alone declares three), is
    /// keyed, or whose lifetime is not <see cref="ServiceLifetime.Singleton"/>. The child never
    /// needs <c>IOptions&lt;&gt;</c> itself, only the already-constructed <c>IHttpClientFactory</c>
    /// instance it backs, so skipping is correct rather than a workaround. A non-singleton is
    /// skipped for a different reason: forwarding it as a resolved instance would freeze what is
    /// meant to vary (a transient <c>HttpClient</c>, a scoped snapshot) into one shared value for
    /// every pipeline forever. <see cref="SharedServiceTypes.AddRange"/> already traced a warning
    /// for these at declaration time.
    /// </para>
    /// <para>
    /// For everything else, resolves <em>every</em> instance the root registered for that type —
    /// not just one — and replaces the child's own registrations with all of them.
    /// <c>ServiceCollectionDescriptorExtensions.Replace</c> removes only the first matching
    /// descriptor, so a multi-registered type (<c>IDocumentParser</c>: <c>TextDocumentParser</c>
    /// and <c>MarkdownDocumentParser</c> both claim it) would silently lose one of the child's own
    /// registrations while forwarding only the last root one. <c>RemoveAll</c> plus one
    /// <c>Add</c> per instance avoids both halves of that bug.
    /// </para>
    /// <para>
    /// Registers each resolved value as an <b>instance</b> descriptor, not a factory delegate. A
    /// factory-based descriptor — <c>ServiceDescriptor.Singleton(type, sp =>
    /// sp.GetRequiredService(type))</c> — has the concrete <c>ServiceProvider</c> capture whatever
    /// the factory call site returns for disposal in the container that ran it, because the
    /// engine cannot know the instance is owned elsewhere. That would make the first child to be
    /// disposed dispose the shared instance as a side effect, and a second child dispose it again
    /// — exactly the failure <see cref="RagPipelineFactory"/>'s "a child never owns what it
    /// forwards" guarantee exists to prevent. An instance descriptor has no factory call site, so
    /// the engine excludes it from disposal capture entirely: ownership stays with the root, which
    /// is the only container that resolved it through a real call site.
    /// </para>
    /// </remarks>
    /// <param name="registration">The name's composed collection and its declared-shared types.</param>
    /// <param name="rootProvider">The root provider forwarded services resolve from.</param>
    private static void ForwardSharedServices(
        NamedPipelineRegistration registration, IServiceProvider rootProvider)
    {
        foreach (var entry in registration.Shared.Entries)
        {
            if (entry.ServiceType.IsGenericTypeDefinition || entry.IsKeyed
                || entry.Lifetime != ServiceLifetime.Singleton)
            {
                continue;
            }

            List<object>? instances = null;
            foreach (var instance in rootProvider.GetServices(entry.ServiceType))
            {
                if (instance is not null)
                {
                    (instances ??= []).Add(instance);
                }
            }

            if (instances is null)
            {
                continue;
            }

            registration.Services.RemoveAll(entry.ServiceType);
            foreach (ref readonly var instance in CollectionsMarshal.AsSpan(instances))
            {
                registration.Services.Add(ServiceDescriptor.Singleton(entry.ServiceType, instance));
            }
        }
    }

    /// <summary>One named pipeline's composed collection and the shared types it forwards.</summary>
    /// <param name="Services">The inner collection this name composed into.</param>
    /// <param name="Shared">The shared-type registry, read at build time so call order does not matter.</param>
    private sealed record NamedPipelineRegistration(IServiceCollection Services, SharedServiceTypes Shared);
}
