using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.AnswerGeneration;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseFlareTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    private sealed class CustomScorer : IConfidenceScorer
    {
        public ValueTask<double> ScoreAsync(
            string sentence, string partialAnswer, IReadOnlyList<SearchResult> context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(1.0);
    }

    [Fact]
    public void UseFlare_RegistersFlareAnswerEngine()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseFlare()).BuildServiceProvider();
        Assert.IsType<FlareAnswerEngine>(sp.GetRequiredService<IAnswerEngine>());
    }

    [Fact]
    public void UseFlare_DefaultScorer_IsSelfAssessment()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseFlare()).BuildServiceProvider();
        Assert.IsType<SelfAssessmentConfidenceScorer>(sp.GetRequiredService<IConfidenceScorer>());
    }

    [Fact]
    public void UseFlare_CustomScorer_Honored()
    {
        var scorer = new CustomScorer();
        var sp = BaseServices().AddRagNet(rag => rag.UseFlare(o => o.Scorer = scorer)).BuildServiceProvider();
        Assert.Same(scorer, sp.GetRequiredService<IConfidenceScorer>());
    }

    [Fact]
    public void UseFlare_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseFlare(o =>
            {
                o.ConfidenceThreshold = 0.8;
                o.MaxRetrievals = 5;
            }))
            .BuildServiceProvider();

        var options = sp.GetRequiredService<FlareOptions>();
        Assert.Equal(0.8, options.ConfidenceThreshold);
        Assert.Equal(5, options.MaxRetrievals);
    }

    [Theory]
    [InlineData(-0.1, 3, 15, 3)]  // threshold below 0
    [InlineData(1.5, 3, 15, 3)]   // threshold above 1
    [InlineData(0.6, -1, 15, 3)]  // negative retrievals
    [InlineData(0.6, 3, 0, 3)]    // zero sentences
    [InlineData(0.6, 3, 15, 0)]   // zero lookahead TopK
    public void UseFlare_InvalidOptions_Throws(double threshold, int maxRetrievals, int maxSentences, int lookaheadTopK)
    {
        var services = BaseServices();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => services.AddRagNet(rag => rag.UseFlare(o =>
            {
                o.ConfidenceThreshold = threshold;
                o.MaxRetrievals = maxRetrievals;
                o.MaxSentences = maxSentences;
                o.LookaheadTopK = lookaheadTopK;
            })));
    }

    [Fact]
    public void UseFlare_WithDispatchingAnswerEngine_ResolvesDispatcher()
    {
        var services = BaseServices();
        services.AddSingleton(Substitute.For<Microsoft.Extensions.Logging.ILogger<MapReduceAnswerEngine>>());
        services.AddSingleton(Substitute.For<Microsoft.Extensions.Logging.ILogger<RefineAnswerEngine>>());
        var sp = services
            .AddRagNet(rag => rag.UseFlare().UseDispatchingAnswerEngine())
            .BuildServiceProvider();

        Assert.IsType<DispatchingAnswerEngine>(sp.GetRequiredService<IAnswerEngine>());
    }
}
