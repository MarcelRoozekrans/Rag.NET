using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Mediator.Handlers;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Mediator;

public class MediatorHandlerTests
{
    [Fact]
    public async Task IngestCommandHandler_DelegatesToIngestor()
    {
        var ingestor = Substitute.For<IIngestor>();
        var expected = Result<IngestionResult, RagError>.Success(
            new IngestionResult { DocumentId = new DocumentId("doc-1"), ChunksStored = 2 });
        ingestor.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));

        var handler = new IngestCommandHandler(ingestor);
        var cmd = new IngestCommand(new MemoryStream(),
            new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" });

        var result = await handler.Handle(cmd, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.ChunksStored);
    }

    [Fact]
    public async Task RetrieveQueryHandler_DelegatesToRetriever()
    {
        var retriever = Substitute.For<IRetriever>();
        var chunks = new List<SearchResult>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success(chunks)));

        var handler = new RetrieveQueryHandler(retriever);
        var query = new RetrieveQuery("my query");

        var result = await handler.Handle(query, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task DeleteCommandHandler_DelegatesToIngestor()
    {
        var ingestor = Substitute.For<IIngestor>();
        ingestor.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new DeleteCommandHandler(ingestor);
        var cmd = new DeleteCommand(new DocumentId("doc-1"));

        var result = await handler.Handle(cmd, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        await ingestor.Received(1).DeleteAsync(Arg.Is<string>(s => s == "doc-1"), Arg.Any<CancellationToken>());
    }
}
