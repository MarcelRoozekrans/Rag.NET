using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Reranking.Onnx;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="OnnxReranker"/> as the <see cref="IReranker"/>,
    /// using a local ONNX cross-encoder model for reranking.
    /// </summary>
    public static TBuilder UseOnnxReranking<TBuilder>(this TBuilder builder, Action<OnnxRerankerOptions> configure)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OnnxRerankerOptions { ModelPath = "", VocabPath = "" };
        configure(options);

        builder.Services.AddSingleton(options);
        builder.UseReranking<OnnxReranker>();

        return builder;
    }
}
