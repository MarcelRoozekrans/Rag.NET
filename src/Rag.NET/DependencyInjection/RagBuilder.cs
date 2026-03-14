using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.Retry;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Fluent builder for configuring the Rag.NET pipeline services.
/// Obtain an instance via <c>services.AddRagNet(rag => ...)</c>.
/// </summary>
public sealed class RagBuilder(IServiceCollection services)
{
    /// <summary>Gets the underlying <see cref="IServiceCollection"/> for advanced registrations.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// Registers a custom chunking strategy. Optionally configures <see cref="ChunkingOptions"/>
    /// (MaxChunkSize and Overlap), which are interpreted as characters by most built-in strategies.
    /// </summary>
    /// <typeparam name="TStrategy">The <see cref="IChunkingStrategy"/> implementation to use.</typeparam>
    /// <param name="configure">Optional delegate to configure chunking options.</param>
    public RagBuilder UseChunkingStrategy<TStrategy>(Action<ChunkingOptions>? configure = null)
        where TStrategy : class, IChunkingStrategy
    {
        Services.AddSingleton<IChunkingStrategy, TStrategy>();

        if (configure is not null)
        {
            var options = new ChunkingOptions();
            configure(options);
            Services.AddSingleton(options);
        }

        return this;
    }

    /// <summary>
    /// Registers <see cref="TokenAwareChunkingStrategy"/>, which splits text by token count
    /// rather than character count using the specified model's tokenizer.
    /// <see cref="ChunkingOptions.MaxChunkSize"/> and <see cref="ChunkingOptions.Overlap"/>
    /// are interpreted as token counts when this strategy is active.
    /// </summary>
    /// <param name="modelName">
    /// The model name used to select the tokenizer encoding (e.g., <c>"gpt-4"</c>, <c>"gpt-3.5-turbo"</c>).
    /// Defaults to <c>"gpt-4"</c> (cl100k_base encoding, compatible with most modern embedding models).
    /// </param>
    public RagBuilder UseTokenAwareChunking(string modelName = "gpt-4")
    {
        Services.AddSingleton<IChunkingStrategy>(_ => new TokenAwareChunkingStrategy(modelName));
        return this;
    }

    /// <summary>
    /// Registers a document parser. Multiple parsers can be registered; the pipeline
    /// selects the first one whose <c>CanParse</c> returns <see langword="true"/> for a given content type.
    /// </summary>
    /// <typeparam name="TParser">The <see cref="IDocumentParser"/> implementation to register.</typeparam>
    public RagBuilder AddParser<TParser>() where TParser : class, IDocumentParser
    {
        Services.AddSingleton<IDocumentParser, TParser>();
        return this;
    }

    /// <summary>
    /// Registers <see cref="LlmQueryExpander"/> as the <see cref="IQueryExpander"/>.
    /// When registered, <see cref="RagPipeline"/> expands each query into
    /// <see cref="MultiQueryOptions.VariantCount"/> alternatives, fans out to the vector store
    /// in parallel, and merges deduplicated results.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseMultiQuery = false }</c>.
    /// </remarks>
    /// <param name="configure">Optional delegate to configure <see cref="MultiQueryOptions"/>.</param>
    public RagBuilder UseMultiQueryRetrieval(Action<MultiQueryOptions>? configure = null)
    {
        var options = new MultiQueryOptions();
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddSingleton<IQueryExpander, LlmQueryExpander>();
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TReranker"/> as the <see cref="IReranker"/>.
    /// When registered, <see cref="RagPipeline"/> rescores search results using
    /// the cross-encoder for higher precision ranking.
    /// </summary>
    /// <remarks>
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseReranking = false }</c>.
    /// Over-fetch control: set <c>RetrievalOptions.CandidateCount</c> (defaults to TopK * 3).
    /// </remarks>
    public RagBuilder UseReranking<TReranker>() where TReranker : class, IReranker
    {
        Services.AddSingleton<IReranker, TReranker>();
        return this;
    }

    /// <summary>
    /// Adds a Polly resilience pipeline named <c>"rag-net"</c> that wraps embedding and vector-store calls.
    /// When no <paramref name="configure"/> delegate is provided, a default exponential back-off retry
    /// (3 attempts, 1 s base delay, jitter) is applied.
    /// </summary>
    /// <param name="configure">
    /// Optional delegate to customise the <see cref="ResiliencePipelineBuilder"/>.
    /// Replaces the default retry policy when supplied.
    /// </param>
    public RagBuilder ConfigureResilience(Action<ResiliencePipelineBuilder>? configure = null)
    {
        Services.AddResiliencePipeline("rag-net", builder =>
        {
            if (configure is not null)
            {
                configure(builder);
            }
            else
            {
                builder.AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                });
            }
        });

        return this;
    }
}
