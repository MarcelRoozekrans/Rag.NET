using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

/// <summary>
/// End to end: a deliberate refusal raised inside the retrieval pipeline fails the query instead
/// of being swallowed by a degrading step (#948). Until #937 the failure is reported as
/// <see cref="RagError.StorageFailed"/> with the refusal as its inner exception.
/// </summary>
public class RefusalFailsQueryTests
{
    [Fact]
    public async Task RetrieveAsync_HydeRefusal_FailsTheQuery()
    {
        var refusal = new BudgetExceededException(CostWindow.Day, 1m, 1m);
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var hydeGenerator = Substitute.For<IHypotheticalDocumentGenerator>();
        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        services.AddSingleton(hydeGenerator);

        hydeGenerator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(refusal);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        services.AddRagNet();
        var pipeline = services.BuildServiceProvider().GetRequiredService<IRagPipeline>();

        var result = await pipeline.RetrieveAsync(
            "query", new RetrievalOptions { UseHyde = true }, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.StorageFailed>(result.Error);
        Assert.Same(refusal, error.Inner);
        await vectorStore.DidNotReceive().SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }
}
