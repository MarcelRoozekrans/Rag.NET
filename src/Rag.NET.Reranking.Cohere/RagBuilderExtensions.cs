using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Reranking.Cohere;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="CohereReranker"/> as the <see cref="IReranker"/>,
    /// using the Cohere Rerank API for reranking.
    /// Switch <see cref="CohereRerankerOptions.Model"/> to <c>rerank-v3.5</c> for multilingual workloads.
    /// </summary>
    public static TBuilder UseCohereReranking<TBuilder>(this TBuilder builder, Action<CohereRerankerOptions> configure)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CohereRerankerOptions { ApiKey = "" };
        configure(options);

        builder.Services.AddSingleton(options);
        builder.UseReranking<CohereReranker>();

        return builder;
    }
}
