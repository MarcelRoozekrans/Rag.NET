using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Embeddings.Onnx;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="OnnxTokenEmbeddingGenerator"/> as the singleton
    /// <see cref="ITokenEmbeddingGenerator"/>, producing token-level embeddings from a local
    /// ONNX model — the generator that late chunking (<c>UseLateChunking</c>) pools into
    /// chunk embeddings.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">
    /// Configures the <see cref="OnnxTokenEmbeddingOptions"/>; required, and must set
    /// <see cref="OnnxTokenEmbeddingOptions.ModelPath"/> and
    /// <see cref="OnnxTokenEmbeddingOptions.TokenizerVocabPath"/> to non-empty paths.
    /// </param>
    /// <exception cref="ArgumentNullException">When <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// At registration, when <see cref="OnnxTokenEmbeddingOptions.ModelPath"/> or
    /// <see cref="OnnxTokenEmbeddingOptions.TokenizerVocabPath"/> is empty. File EXISTENCE is
    /// checked at resolution time by the <see cref="OnnxTokenEmbeddingGenerator"/> constructor
    /// (<see cref="FileNotFoundException"/>) — the same timing as the ONNX reranker
    /// registration.
    /// </exception>
    public static TBuilder UseOnnxTokenEmbeddings<TBuilder>(this TBuilder builder, Action<OnnxTokenEmbeddingOptions> configure)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OnnxTokenEmbeddingOptions { ModelPath = "", TokenizerVocabPath = "" };
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ModelPath))
            throw new ArgumentException("OnnxTokenEmbeddingOptions.ModelPath must be a non-empty path.", nameof(configure));

        if (string.IsNullOrWhiteSpace(options.TokenizerVocabPath))
            throw new ArgumentException("OnnxTokenEmbeddingOptions.TokenizerVocabPath must be a non-empty path.", nameof(configure));

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ITokenEmbeddingGenerator, OnnxTokenEmbeddingGenerator>();

        return builder;
    }
}
