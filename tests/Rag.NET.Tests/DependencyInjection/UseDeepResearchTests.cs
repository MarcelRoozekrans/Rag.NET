using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseDeepResearchTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UseDeepResearch_IRetrieverIsDeepResearchRetriever()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseDeepResearch()).BuildServiceProvider();
        Assert.IsType<DeepResearchRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseDeepResearch_DefaultOptions_MaxDepthIsThree()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseDeepResearch()).BuildServiceProvider();
        Assert.Equal(3, sp.GetRequiredService<DeepResearchOptions>().MaxDepth);
    }

    [Fact]
    public void UseDeepResearch_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseDeepResearch(new DeepResearchOptions { MaxDepth = 1 }))
            .BuildServiceProvider();
        Assert.Equal(1, sp.GetRequiredService<DeepResearchOptions>().MaxDepth);
    }

    [Fact]
    public void WithoutUseDeepResearch_IRetrieverIsPipelineRetriever()
    {
        var sp = BaseServices().AddRagNet().BuildServiceProvider();
        Assert.IsType<PipelineRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public async Task UseDeepResearch_RetrieveAsync_InvokesVectorStoreThroughDecorator()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var chatClient = Substitute.For<IChatClient>();

        // Embedder returns a dummy embedding for the query
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
                .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));

        // Vector store returns an empty result set (no matches)
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                   .Returns(Array.Empty<SearchResult>());

        // LLM says sufficient on first pass
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), ct)
                  .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                      "{\"sufficient\":true,\"subQueries\":[]}")));

        var services = new ServiceCollection();
        services.AddSingleton(embedder);
        services.AddSingleton(vectorStore);
        services.AddSingleton(chatClient);

        var sp = services.AddRagNet(rag => rag.UseDeepResearch()).BuildServiceProvider();
        var retriever = sp.GetRequiredService<IRetriever>();

        var result = await retriever.RetrieveAsync("test query", null, ct);

        Assert.True(result.IsSuccess);
        // Vector store was called by the underlying PipelineRetriever
        await vectorStore.Received(1).SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct);
    }
}
