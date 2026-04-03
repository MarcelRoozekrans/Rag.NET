using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Security;
using ZeroAlloc.Results;
using Xunit;

namespace Rag.NET.Security.Tests;

file sealed class CapturingQuerySanitiser : IQuerySanitiser
{
    public string? LastQuery { get; private set; }
    public string Sanitise(string query) { LastQuery = query; return query + "-sanitised"; }
}

public class QuerySanitiserPipelineDecoratorTests
{
    [Fact]
    public async Task AskAsync_QuerySanitisedBeforeDelegate()
    {
        var sanitiser = new CapturingQuerySanitiser();
        var inner = Substitute.For<IRagPipeline>();
        inner.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new RagResponse { Answer = "ok", Sources = [] }));
        var sut = new QuerySanitiserPipelineDecorator(inner, [sanitiser]);

        _ = await sut.AskAsync("original query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("original query", sanitiser.LastQuery);
        _ = await inner.Received().AskAsync("original query-sanitised", Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_PassesThroughUnmodified()
    {
        var inner = Substitute.For<IRagPipeline>();
        inner.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(new IngestionResult { DocumentId = new DocumentId("d1"), ChunksStored = 1 })));
        var sut = new QuerySanitiserPipelineDecorator(inner, []);
        var meta = new DocumentMetadata { DocumentId = new DocumentId("d1"), FileName = "f.pdf" };
        _ = await sut.IngestAsync(Stream.Null, meta, cancellationToken: TestContext.Current.CancellationToken);
        _ = await inner.Received().IngestAsync(Stream.Null, meta, Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoSanitisers_QueryPassesThroughUnchanged()
    {
        var inner = Substitute.For<IRagPipeline>();
        inner.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new RagResponse { Answer = "ok", Sources = [] }));
        var sut = new QuerySanitiserPipelineDecorator(inner, []);
        _ = await sut.AskAsync("clean query", cancellationToken: TestContext.Current.CancellationToken);
        _ = await inner.Received().AskAsync("clean query", Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }
}
