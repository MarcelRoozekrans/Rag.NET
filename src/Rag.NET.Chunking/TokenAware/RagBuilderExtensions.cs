using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking.TokenAware;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="TokenAwareChunkingStrategy"/>, which splits text by token count
    /// rather than character count using the specified model's tokenizer.
    /// <see cref="Rag.NET.Models.Options.ChunkingOptions.MaxChunkSize"/> and <see cref="Rag.NET.Models.Options.ChunkingOptions.Overlap"/>
    /// are interpreted as token counts when this strategy is active.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="modelName">
    /// The model name used to select the tokenizer encoding (e.g., <c>"gpt-4"</c>, <c>"gpt-3.5-turbo"</c>).
    /// Defaults to <c>"gpt-4"</c> (cl100k_base encoding, compatible with most modern embedding models).
    /// </param>
    public static TBuilder UseTokenAwareChunking<TBuilder>(this TBuilder builder, string modelName = "gpt-4")
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IChunkingStrategy>(_ => new TokenAwareChunkingStrategy(modelName));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="TokenAwareChunkingStrategy"/> configured via <see cref="TokenAwareChunkingOptions"/>.
    /// When <see cref="TokenAwareChunkingOptions.WindowSizeTokens"/> / <see cref="TokenAwareChunkingOptions.OverlapTokens"/>
    /// are set they override <see cref="Rag.NET.Models.Options.ChunkingOptions.MaxChunkSize"/> /
    /// <see cref="Rag.NET.Models.Options.ChunkingOptions.Overlap"/>; when null those values are used as token counts.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">Configures the <see cref="TokenAwareChunkingOptions"/>.</param>
    public static TBuilder UseTokenAwareChunking<TBuilder>(this TBuilder builder, Action<TokenAwareChunkingOptions>? configure)
        where TBuilder : IRagBuilder
    {
        var options = new TokenAwareChunkingOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton<IChunkingStrategy>(_ => new TokenAwareChunkingStrategy(options));
        return builder;
    }
}
