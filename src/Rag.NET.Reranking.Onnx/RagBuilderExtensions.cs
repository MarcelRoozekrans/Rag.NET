using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Reranking.Onnx;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="OnnxReranker"/> as the <see cref="IReranker"/>,
    /// using a local ONNX cross-encoder model for reranking.
    /// </summary>
    public static RagBuilder UseOnnxReranking(this RagBuilder builder, Action<OnnxRerankerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OnnxRerankerOptions { ModelPath = "" };
        configure(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IReranker, OnnxReranker>();

        return builder;
    }
}
