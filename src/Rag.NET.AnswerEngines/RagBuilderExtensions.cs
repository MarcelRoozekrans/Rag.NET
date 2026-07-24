using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerEngines;

/// <summary>Extension methods for registering advanced answer engines in the Rag.NET pipeline.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="MapReduceAnswerEngine"/> as the <see cref="IAnswerEngine"/>.
    /// Executes one LLM call per source chunk in parallel (map), filters "not found" responses,
    /// then combines surviving partials in a single reduce call.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// </remarks>
    public static TBuilder UseMapReduceAnswerEngine<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IAnswerEngine>(sp => MapReduceAnswerEngine.CreateFromServices(sp));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="RefineAnswerEngine"/> as the <see cref="IAnswerEngine"/>.
    /// Generates an initial answer from the first source chunk, then iteratively refines it
    /// with each subsequent chunk.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// </remarks>
    public static TBuilder UseRefineAnswerEngine<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IAnswerEngine>(sp => RefineAnswerEngine.CreateFromServices(sp));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="DispatchingAnswerEngine"/> as the <see cref="IAnswerEngine"/>.
    /// Routes answer generation to <see cref="MapReduceAnswerEngine"/>, <see cref="RefineAnswerEngine"/>,
    /// <see cref="FlareAnswerEngine"/> (when <see cref="UseFlare"/> was called), or
    /// <see cref="ChatAnswerEngine"/> based on <c>RagOptions.SynthesisStrategy</c> at call time.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// </remarks>
    public static TBuilder UseDispatchingAnswerEngine<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IAnswerEngine>(sp =>
        {
            var chatEngine = ChatAnswerEngine.CreateFromServices(sp);
            var mapReduceEngine = MapReduceAnswerEngine.CreateFromServices(sp);
            var refineEngine = RefineAnswerEngine.CreateFromServices(sp);
            // FLARE is optional: only wired when UseFlare() registered its options + scorer.
            var flareEngine = sp.GetService<FlareOptions>() is not null
                ? FlareAnswerEngine.CreateFromServices(sp)
                : null;
            return new DispatchingAnswerEngine(chatEngine, mapReduceEngine, refineEngine, flareEngine);
        });
        return builder;
    }

    /// <summary>
    /// Registers <see cref="FlareAnswerEngine"/> as the <see cref="IAnswerEngine"/> along with its
    /// <see cref="IConfidenceScorer"/> (default: <see cref="SelfAssessmentConfidenceScorer"/>).
    /// FLARE generates the answer sentence-by-sentence and performs lookahead retrievals when a
    /// sentence scores below <see cref="FlareOptions.ConfidenceThreshold"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> (unless <see cref="FlareOptions.ChatClient"/> is set) and
    /// <c>IRetriever</c> (registered by <c>AddRagNet</c>) in DI. Combine with
    /// <see cref="UseDispatchingAnswerEngine"/> to select FLARE per call via
    /// <c>RagOptions.SynthesisStrategy = SynthesisStrategy.Flare</c>.
    /// This registration of <see cref="IConfidenceScorer"/> supersedes any earlier one
    /// (standard last-wins container semantics, matching the other <c>Use*</c> extensions);
    /// <see cref="FlareOptions.Scorer"/> is the supported way to plug in a custom scorer.
    /// </remarks>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">Optional delegate to configure <see cref="FlareOptions"/>.</param>
    public static TBuilder UseFlare<TBuilder>(this TBuilder builder, Action<FlareOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var options = new FlareOptions();
        configure?.Invoke(options);
        ValidateFlareOptions(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IConfidenceScorer>(sp =>
            options.Scorer ?? new SelfAssessmentConfidenceScorer(
                options.ChatClient ?? sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILogger<SelfAssessmentConfidenceScorer>>()));
        builder.Services.AddSingleton<IAnswerEngine>(sp => FlareAnswerEngine.CreateFromServices(sp));
        return builder;
    }

    private static void ValidateFlareOptions(FlareOptions options)
    {
        if (options.ConfidenceThreshold is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(FlareOptions)}.{nameof(FlareOptions.ConfidenceThreshold)} ({options.ConfidenceThreshold}) must be between 0 and 1.");
        }

        if (options.MaxRetrievals < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(FlareOptions)}.{nameof(FlareOptions.MaxRetrievals)} ({options.MaxRetrievals}) must be non-negative.");
        }

        if (options.MaxSentences < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(FlareOptions)}.{nameof(FlareOptions.MaxSentences)} ({options.MaxSentences}) must be at least 1.");
        }

        if (options.LookaheadTopK < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(FlareOptions)}.{nameof(FlareOptions.LookaheadTopK)} ({options.LookaheadTopK}) must be at least 1.");
        }
    }
}
