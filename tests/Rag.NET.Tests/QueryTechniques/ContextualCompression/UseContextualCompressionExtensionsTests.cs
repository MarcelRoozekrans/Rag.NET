using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques;
using Rag.NET.QueryTechniques.ContextualCompression;
using Rag.NET.Retrieval.Behaviors;
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

    [Fact]
    public async Task UseContextualCompression_AskAsync_InvokesCompressor()
    {
        // Build a full DI container (AddRagNet) with required dependencies stubbed so
        // the default ChatAnswerEngine factory in ServiceCollectionExtensions runs and
        // resolves IContextualCompressor from DI.
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var chatClient = Substitute.For<IChatClient>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 0.5 },
            });
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        services.AddSingleton(chatClient);

        services.AddRagNet(); // registers the default pipeline
        BuilderOn(services).UseContextualCompression(o => o.KeepTopSentences = 1);

        // Replace the registered compressor with a spy so we can verify it was invoked.
        var spyCompressor = Substitute.For<IContextualCompressor>();
#pragma warning disable EPS06
        spyCompressor.CompressAsync(Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<IReadOnlyList<SearchResult>>(ci.Arg<IReadOnlyList<SearchResult>>()));
#pragma warning restore EPS06
        services.RemoveAll<IContextualCompressor>();
        services.AddSingleton(spyCompressor);

        using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.AskAsync("q", new RagOptions(), TestContext.Current.CancellationToken);

        await spyCompressor.Received(1).CompressAsync(
            Arg.Any<IReadOnlyList<SearchResult>>(), "q", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UseContextualCompression_WithDispatching_DefaultStrategy_InvokesCompressor()
    {
        // Verifies the DispatchingAnswerEngine's ChatAnswerEngine branch receives IContextualCompressor.
        // SynthesisStrategy.Default routes to ChatAnswerEngine which should invoke the compressor.
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var chatClient = Substitute.For<IChatClient>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 0.5 },
            });
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        services.AddSingleton(chatClient);
        services.AddLogging();

        services.AddRagNet();
        var builder = BuilderOn(services);
        builder.UseContextualCompression(o => o.KeepTopSentences = 1);
        builder.UseDispatchingAnswerEngine();

        var spyCompressor = Substitute.For<IContextualCompressor>();
#pragma warning disable EPS06
        spyCompressor.CompressAsync(Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<IReadOnlyList<SearchResult>>(ci.Arg<IReadOnlyList<SearchResult>>()));
#pragma warning restore EPS06
        services.RemoveAll<IContextualCompressor>();
        services.AddSingleton(spyCompressor);

        using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.AskAsync(
            "q",
            new RagOptions { SynthesisStrategy = SynthesisStrategy.Default },
            TestContext.Current.CancellationToken);

        await spyCompressor.Received(1).CompressAsync(
            Arg.Any<IReadOnlyList<SearchResult>>(), "q", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UseContextualCompressionInRetrieval_WithoutBaseRegistration_Throws()
    {
        var services = BaseServices();
        services.AddRagNet(); // registers RetrievalPipelineBuilder
        var builder = BuilderOn(services);

        Assert.Throws<InvalidOperationException>(() => builder.UseContextualCompressionInRetrieval());
    }

    [Fact]
    public void UseContextualCompressionInRetrieval_AddsBehaviorToPipelineBuilder()
    {
        var services = BaseServices();
        services.AddRagNet(); // registers RetrievalPipelineBuilder in DI
        var builder = BuilderOn(services);
        builder.UseContextualCompression();

        builder.UseContextualCompressionInRetrieval();

        var pipelineBuilder = services
            .First(d => d.ServiceType == typeof(RetrievalPipelineBuilder))
            .ImplementationInstance as RetrievalPipelineBuilder;
        Assert.NotNull(pipelineBuilder);
        var types = pipelineBuilder.GetBehaviorTypes();
        Assert.Contains(typeof(ContextualCompressionRetrievalBehavior), types);

        // Strengthen: compression must sit BEFORE the retrieval guard so guards see compressed text.
        var idxCompress = types.ToList().IndexOf(typeof(ContextualCompressionRetrievalBehavior));
        var idxGuard = types.ToList().IndexOf(typeof(RetrievalGuardBehavior));
        Assert.InRange(idxCompress, 0, idxGuard - 1);
    }

    [Fact]
    public void UseContextualCompressionInRetrieval_WithoutAddRagNet_Throws()
    {
        var services = BaseServices();
        var builder = BuilderOn(services);
        builder.UseContextualCompression(); // IContextualCompressor registered, but no AddRagNet
        Assert.Throws<InvalidOperationException>(() => builder.UseContextualCompressionInRetrieval());
    }

    [Fact]
    public void UseContextualCompressionInRetrieval_CalledTwice_IsIdempotent()
    {
        var services = BaseServices();
        services.AddRagNet();
        var builder = BuilderOn(services);
        builder.UseContextualCompression();

        builder.UseContextualCompressionInRetrieval();
        builder.UseContextualCompressionInRetrieval(); // second call

        var pipelineBuilder = services
            .First(d => d.ServiceType == typeof(RetrievalPipelineBuilder))
            .ImplementationInstance as RetrievalPipelineBuilder;
        Assert.NotNull(pipelineBuilder);
        var types = pipelineBuilder.GetBehaviorTypes();
        Assert.Equal(1, types.Count(t => t == typeof(ContextualCompressionRetrievalBehavior)));
    }
}
