using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.Retry;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.DependencyInjection;

public sealed class RagBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

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

    public RagBuilder AddParser<TParser>() where TParser : class, IDocumentParser
    {
        Services.AddSingleton<IDocumentParser, TParser>();
        return this;
    }

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
