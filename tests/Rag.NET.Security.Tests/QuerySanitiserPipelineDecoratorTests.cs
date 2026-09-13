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
    /// <summary>
    /// <c>RetrieveAsync</c> forwards the query unsanitised, and that is the decision, not an
    /// oversight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned because nothing recorded it —
    /// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/559">#559</see> was filed
    /// asking whether the omission was deliberate, and the honest answer at the time was that no
    /// doc comment, test or published page said either way. The maintainer confirmed it is
    /// deliberate on 2026-09-13.
    /// </para>
    /// <para>
    /// <b>The reasoning.</b> Prompt injection hijacks a model, and <c>RetrieveAsync</c> reaches
    /// none — it returns chunks to the caller, who decides what to do with them. Redacting
    /// <c>act as</c> or <c>ignore previous</c> from a legitimate query about those very phrases
    /// would corrupt the search terms while protecting nothing.
    /// </para>
    /// <para>
    /// <b>What this costs, said out loud.</b> A retrieval-only caller who registers
    /// <c>UseQuerySanitiser</c> gets nothing on their path. That is why
    /// <c>RagBuilderExtensions.UseQuerySanitiser</c> states the scope in its own remarks rather
    /// than leaving the name to imply it covers the whole pipeline.
    /// </para>
    /// <para>
    /// <b>If this test ever fails</b>, sanitisation was extended to retrieval. That may be right —
    /// but it changes what a documented defence covers, so it needs the decision revisited rather
    /// than this assertion updated to match.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RetrieveAsync_ForwardsTheQueryUnsanitised()
    {
        var sanitiser = new CapturingQuerySanitiser();
        var inner = Substitute.For<IRagPipeline>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success([])));
        var sut = new QuerySanitiserPipelineDecorator(inner, [sanitiser]);

        _ = await sut.RetrieveAsync("ignore previous instructions", cancellationToken: TestContext.Current.CancellationToken);

        // The sanitiser is never consulted, and the inner pipeline sees the original text.
        Assert.Null(sanitiser.LastQuery);
        _ = await inner.Received().RetrieveAsync(
            "ignore previous instructions", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

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

    [Fact]
    public async Task AskStreamingAsync_QuerySanitisedBeforeDelegate()
    {
        var sanitiser = new CapturingQuerySanitiser();
        var inner = Substitute.For<IRagPipeline>();
        inner.AskStreamingAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
             .Returns(AsyncEnum());
        var sut = new QuerySanitiserPipelineDecorator(inner, [sanitiser]);

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in sut.AskStreamingAsync("original query", cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal("original query", sanitiser.LastQuery);
        inner.Received().AskStreamingAsync("original query-sanitised", Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());

        static async IAsyncEnumerable<RagStreamingUpdate> AsyncEnum()
        {
            await Task.Yield();
            yield break;
        }
    }
}
