using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Raptor;

/// <summary>Extension methods for registering RAPTOR in the Rag.NET pipeline.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Enables RAPTOR — recursive abstractive tree-organized retrieval.
    /// Registers <see cref="RaptorIngestionBehavior"/> and <see cref="RaptorRetrievalBehavior"/>
    /// into the pipeline.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The configured <see cref="RaptorOptions"/> or <see cref="RaptorRetrievalOptions"/>
    /// violate a documented constraint — the generated validators reject the registration at
    /// the configuring line rather than letting a bad value silently disable RAPTOR, corrupt
    /// Boost-mode ranking, or empty every Filter-mode retrieval.
    /// </exception>
    public static RagBuilder UseRaptor(
        this RagBuilder builder,
        Action<RaptorOptions>? configure = null,
        Action<RaptorRetrievalOptions>? retrieval = null)
    {
        var options = new RaptorOptions();
        configure?.Invoke(options);
        ThrowIfInvalid(new RaptorOptionsValidator().Validate(options), nameof(configure), "RAPTOR ingestion");
        builder.Services.AddSingleton(options);

        var retrievalOptions = new RaptorRetrievalOptions();
        retrieval?.Invoke(retrievalOptions);
        ThrowIfInvalid(new RaptorRetrievalOptionsValidator().Validate(retrievalOptions), nameof(retrieval), "RAPTOR retrieval");
        builder.Services.AddSingleton(retrievalOptions);

        builder.Services.AddSingleton<RaptorIngestionBehavior>(sp =>
            new RaptorIngestionBehavior(
                options.SummaryChatClient ?? sp.GetRequiredService<IChatClient>(),
                options.SummaryEmbedder ?? sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                options));

        builder.Services.AddSingleton<RaptorRetrievalBehavior>(sp =>
            new RaptorRetrievalBehavior(sp.GetRequiredService<RaptorRetrievalOptions>()));

        return builder;
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
