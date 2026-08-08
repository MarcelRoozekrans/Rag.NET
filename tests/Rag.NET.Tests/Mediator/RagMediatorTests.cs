using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Mediator;
using Rag.NET.Mediator.DependencyInjection;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Mediator;

/// <summary>
/// Exercises <see cref="IRagMediator"/> end to end through a real DI container built by
/// <c>UseMediator()</c> — the generated <c>IMediator</c> it wraps is internal to
/// Rag.NET.Mediator, so this is the only place these three dispatch paths can be verified
/// from outside that assembly. Each test asserts the call actually reached the registered
/// handler's dependency, not merely that <see cref="IRagMediator"/> resolved from DI.
/// </summary>
public class RagMediatorTests
{
    private static IRagMediator BuildMediator(IIngestor ingestor, IRetriever retriever)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ingestor);
        services.AddSingleton(retriever);
        new RagBuilder(services).UseMediator();
        return services.BuildServiceProvider().GetRequiredService<IRagMediator>();
    }

    [Fact]
    public async Task Send_IngestCommand_ReachesIngestorAndReturnsItsResult()
    {
        var ingestor = Substitute.For<IIngestor>();
        var expected = Result<IngestionResult, RagError>.Success(
            new IngestionResult { DocumentId = new DocumentId("doc-1"), ChunksStored = 3 });
        ingestor.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));
        var mediator = BuildMediator(ingestor, Substitute.For<IRetriever>());

        using var stream = new MemoryStream();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };
        var result = await mediator.Send(new IngestCommand(stream, metadata), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.ChunksStored);
        _ = await ingestor.Received(1).IngestAsync(stream, metadata, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_RetrieveQuery_ReachesRetrieverAndReturnsItsResult()
    {
        var retriever = Substitute.For<IRetriever>();
        var chunk = new SearchResult
        {
            Score = 0.5,
            Chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
        };
        var expected = Result<IReadOnlyList<SearchResult>, RagError>.Success([chunk]);
        retriever.RetrieveAsync("my query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));
        var mediator = BuildMediator(Substitute.For<IIngestor>(), retriever);

        var result = await mediator.Send(new RetrieveQuery("my query"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal("hello", result.Value[0].Chunk.Text);
        _ = await retriever.Received(1).RetrieveAsync("my query", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_DeleteCommand_ReachesIngestorDelete()
    {
        var ingestor = Substitute.For<IIngestor>();
        ingestor.DeleteAsync("doc-1", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var mediator = BuildMediator(ingestor, Substitute.For<IRetriever>());

        var result = await mediator.Send(new DeleteCommand(new DocumentId("doc-1")), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        await ingestor.Received(1).DeleteAsync("doc-1", Arg.Any<CancellationToken>());
    }
}
