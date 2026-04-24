using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.QueryTechniques;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.QueryTechniques.ContextualCompression;

public class UseContextualCompressionExtensionsTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton<IChatClient>(Substitute.For<IChatClient>());
        return services;
    }

    private static IRagBuilder BuilderOn(IServiceCollection services) =>
        new RagBuilder(services);

    [Fact]
    public void UseContextualCompression_WithoutStoppingCriteria_ThrowsOnRegistration()
    {
        var services = BaseServices();
        var builder = BuilderOn(services);

        Assert.Throws<InvalidOperationException>(() =>
            builder.UseContextualCompression(o =>
            {
                o.KeepTopSentences = null;
                o.MaxTokensPerChunk = null;
            }));
    }

    [Fact]
    public void UseContextualCompression_NegativeKeepTopSentences_ThrowsOnRegistration()
    {
        var services = BaseServices();
        Assert.Throws<InvalidOperationException>(() =>
            BuilderOn(services).UseContextualCompression(o => o.KeepTopSentences = -1));
    }

    [Fact]
    public void UseContextualCompression_ZeroMaxTokens_ThrowsOnRegistration()
    {
        var services = BaseServices();
        Assert.Throws<InvalidOperationException>(() =>
            BuilderOn(services).UseContextualCompression(o =>
            {
                o.KeepTopSentences = null;
                o.MaxTokensPerChunk = 0;
            }));
    }

    [Fact]
    public void UseContextualCompression_ExtractiveStrategy_RegistersExtractiveCompressor()
    {
        var services = BaseServices();
        BuilderOn(services).UseContextualCompression(o => o.Strategy = ContextualCompressionStrategy.Extractive);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IContextualCompressor>();
        Assert.IsType<ExtractiveCompressor>(resolved);
    }

    [Fact]
    public void UseContextualCompression_AbstractiveStrategy_RegistersLlmAbstractiveCompressor()
    {
        var services = BaseServices();
        BuilderOn(services).UseContextualCompression(o => o.Strategy = ContextualCompressionStrategy.Abstractive);

        using var sp = services.BuildServiceProvider();
        Assert.IsType<LlmAbstractiveCompressor>(sp.GetRequiredService<IContextualCompressor>());
    }

    [Fact]
    public void UseContextualCompression_DefaultsToExtractive()
    {
        var services = BaseServices();
        BuilderOn(services).UseContextualCompression();

        using var sp = services.BuildServiceProvider();
        Assert.IsType<ExtractiveCompressor>(sp.GetRequiredService<IContextualCompressor>());
    }
}
