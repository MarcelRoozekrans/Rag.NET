using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Raptor.Store;
using Rag.NET.Retrieval.Behaviors;

namespace Rag.NET.Raptor;

/// <summary>Extension methods for registering RAPTOR in the Rag.NET pipeline.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Enables RAPTOR — recursive abstractive tree-organized retrieval. Registers
    /// <see cref="RaptorIngestionBehavior"/> and <see cref="RaptorRetrievalBehavior"/> and places
    /// both in the pipeline: ingestion directly after <c>EmbeddingBehavior</c> (it needs the
    /// embeddings) and retrieval directly before <c>RerankingBehavior</c> (its score adjustments
    /// have to settle before reranking sees them).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placing them is the whole point of the call.</b> This method used to register both
    /// behaviours and stop, and neither type is in either default pipeline, so <c>UseRaptor()</c>
    /// on its own built no tree, threw nothing and logged nothing — the caller got a plain vector
    /// pipeline and no signal that RAPTOR was off (issue #191).
    /// </para>
    /// <para>
    /// The explicit form <c>docs/guide/raptor.md</c> teaches — naming both behaviours in
    /// <c>AddRagNet</c>'s <c>ingestion:</c> and <c>retrieval:</c> delegates — is unchanged and
    /// still wins. Those delegates run before <c>configure</c>, and <c>Add</c> is idempotent, so
    /// a caller who places RAPTOR by hand keeps their own positions and gets it exactly once.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The configured <see cref="RaptorOptions"/> or <see cref="RaptorRetrievalOptions"/>
    /// violate a documented constraint — the generated validators reject the registration at
    /// the configuring line rather than letting a bad value silently disable RAPTOR, corrupt
    /// Boost-mode ranking, or empty every Filter-mode retrieval.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <c>AddRagNet</c> has not been called, so there is no pipeline to place the behaviours in.
    /// Registering them anyway is what made the silent no-op possible.
    /// </exception>
    public static TBuilder UseRaptor<TBuilder>(
        this TBuilder builder,
        Action<RaptorOptions>? configure = null,
        Action<RaptorRetrievalOptions>? retrieval = null,
        string? leafStorePath = null)
        where TBuilder : IRagBuilder
    {
        var options = new RaptorOptions();
        configure?.Invoke(options);
        ThrowIfInvalid(new RaptorOptionsValidator().Validate(options), nameof(configure), "RAPTOR ingestion");
        ThrowIfCorpusScopeMisconfigured(options, leafStorePath, nameof(configure));

        builder.Services.AddSingleton(options);

        var retrievalOptions = new RaptorRetrievalOptions();
        retrieval?.Invoke(retrievalOptions);
        ThrowIfInvalid(new RaptorRetrievalOptionsValidator().Validate(retrievalOptions), nameof(retrieval), "RAPTOR retrieval");
        builder.Services.AddSingleton(retrievalOptions);

        if (leafStorePath is not null)
        {
            // No InitializeAsync().GetAwaiter().GetResult() here any more: the store creates its
            // own schema in its constructor (#353), so there is no sync-over-async on the path
            // that first resolves it.
            builder.Services.AddSingleton<IRaptorLeafStore>(_ => new SqliteRaptorLeafStore(leafStorePath));
        }

        builder.Services.AddSingleton<RaptorIngestionBehavior>(sp =>
            new RaptorIngestionBehavior(
                options.SummaryChatClient ?? sp.GetRequiredService<IChatClient>(),
                options.SummaryEmbedder ?? sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                options,
                sp.GetService<IRaptorLeafStore>()));

        if (leafStorePath is not null)
        {
            // The on-demand counterpart to the ingest-time growth threshold. Registered rather
            // than left for callers to construct, because it must share the ONE
            // RaptorIngestionBehavior singleton: that instance holds the debounce baseline, and a
            // rebuild resets it so ingestion continuing afterwards debounces from the rebuilt
            // state rather than a stale count.
            builder.Services.AddSingleton<RaptorTreeRebuilder>(sp =>
                new RaptorTreeRebuilder(
                    sp.GetRequiredService<RaptorIngestionBehavior>(),
                    sp.GetRequiredService<IVectorStore>()));
        }

        builder.Services.AddSingleton<RaptorRetrievalBehavior>(sp =>
            new RaptorRetrievalBehavior(sp.GetRequiredService<RaptorRetrievalOptions>()));

        builder.Services.RagIngestionPipeline(nameof(UseRaptor))
            .Add<RaptorIngestionBehavior>(after: typeof(EmbeddingBehavior));
        builder.Services.RagRetrievalPipeline(nameof(UseRaptor))
            .Add<RaptorRetrievalBehavior>(before: typeof(RerankingBehavior));

        return builder;
    }

    /// <summary>
    /// Rejects two <see cref="RaptorTreeScope.Corpus"/> misconfigurations that would otherwise fail
    /// silently rather than loudly: no leaf store to persist leaves between ingests, and
    /// <see cref="RaptorOptions.StoreLeafChunks"/> set to a value that has nothing to act on.
    /// </summary>
    /// <param name="options">The configured ingestion options.</param>
    /// <param name="leafStorePath">The leaf store path passed to <see cref="UseRaptor{TBuilder}"/>, or null.</param>
    /// <param name="configureParamName"><c>nameof(configure)</c> from the caller, for <see cref="ArgumentException.ParamName"/>.</param>
    /// <exception cref="ArgumentException">Either misconfiguration is present.</exception>
    private static void ThrowIfCorpusScopeMisconfigured(
        RaptorOptions options, string? leafStorePath, string configureParamName)
    {
        if (options.TreeScope != RaptorTreeScope.Corpus)
        {
            return;
        }

        if (leafStorePath is null)
        {
            throw new ArgumentException(
                "RaptorOptions.TreeScope is Corpus, which clusters over the whole corpus and therefore " +
                "needs somewhere to keep leaf chunks between ingests. Pass leafStorePath, or set " +
                "TreeScope to PerDocument.",
                nameof(leafStorePath));
        }

        if (!options.StoreLeafChunks)
        {
            throw new ArgumentException(
                "RaptorOptions.TreeScope is Corpus and RaptorOptions.StoreLeafChunks is false. Under " +
                "Corpus scope, leaf chunks live in the leaf store, not in the ingestion context, so " +
                "StoreLeafChunks has nothing to act on and would be silently ignored. Set " +
                "StoreLeafChunks to true, or set TreeScope to PerDocument.",
                configureParamName);
        }
    }

    /// <summary>
    /// Rejects invalid options at the line that configured them, with a stack trace pointing at
    /// the caller's lambda rather than at some later ingestion or retrieval that happens to
    /// consume the singleton — the same registration-time shape as
    /// <c>RagBuilder.UseChunkingStrategy</c> (issue #90).
    /// </summary>
    /// <param name="result">The generated validator's verdict on the configured options.</param>
    /// <param name="paramName">The caller's configuring delegate, for <see cref="ArgumentException.ParamName"/>.</param>
    /// <param name="description">What was being configured, for the failure message.</param>
    /// <exception cref="ArgumentException">The options violate a declared constraint.</exception>
    private static void ThrowIfInvalid(
        ZeroAlloc.Validation.ValidationResult result, string paramName, string description)
    {
        if (result.IsValid)
        {
            return;
        }

        // Projected by index into an array: ValidationFailure is a non-readonly struct, so
        // enumerating the span by value trips EPS06 and indexing the property result directly
        // trips HLQ013 — same shape as RagBuilder.ThrowIfInvalid.
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
}
