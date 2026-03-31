using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Reranking.Cohere;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="CohereReranker"/> as the <see cref="IReranker"/>,
    /// using the Cohere Rerank API for reranking.
    /// Switch <see cref="CohereRerankerOptions.Model"/> to <c>rerank-v3.5</c> for multilingual workloads.
    /// </summary>
    public static RagBuilder UseCohereReranking(this RagBuilder builder, Action<CohereRerankerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CohereRerankerOptions { ApiKey = "" };
        configure(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IReranker, CohereReranker>();

        return builder;
    }
}
