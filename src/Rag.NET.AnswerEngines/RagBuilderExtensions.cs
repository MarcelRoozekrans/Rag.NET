using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;

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
    /// or <see cref="ChatAnswerEngine"/> based on <c>RagOptions.SynthesisStrategy</c> at call time.
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
            return new DispatchingAnswerEngine(chatEngine, mapReduceEngine, refineEngine);
        });
        return builder;
    }
}
