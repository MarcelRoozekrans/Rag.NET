using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class TagIngestionBehaviorTests
{
    private static IngestionContext MakeCtx(Dictionary<string, MetadataValue>? tags = null) =>
        new()
        {
            Stream           = Stream.Null,
            Metadata         = new DocumentMetadata
            {
                DocumentId = new DocumentId("doc1"),
                FileName   = "doc1.pdf",
                Tags       = tags ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
            },
            GetNextBm25DocId = () => 0,
        };

    private static IEmbeddingGenerator<string, Embedding<float>> MockEmbedder(float[] vector)
    {
        var e = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        e.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
         .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vector)]));
        return e;
    }

    private static ValueTask<IngestionResult> NullNext(IngestionContext ctx, CancellationToken _) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    [Fact]
    public async Task TagsEmbeddedAndStored()
    {
        var ct      = TestContext.Current.CancellationToken;
        var index   = Substitute.For<ITagIndex>();
        var embedder = MockEmbedder([0.5f]);

        var sut = new TagIngestionBehavior { TagIndex = index, Embedder = embedder };
        await sut.HandleAsync(MakeCtx(new(StringComparer.Ordinal) { ["dept"] = "finance" }), ct, NullNext);

        index.Received(1).Add("dept", "finance", Arg.Any<ReadOnlyMemory<float>>());
    }

    [Fact]
    public async Task DuplicateTag_NotEmbeddedAgain()
    {
        var ct      = TestContext.Current.CancellationToken;
        var index   = Substitute.For<ITagIndex>();
        index.Contains("dept", "finance").Returns(true); // already present
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var sut = new TagIngestionBehavior { TagIndex = index, Embedder = embedder };
        await sut.HandleAsync(MakeCtx(new(StringComparer.Ordinal) { ["dept"] = "finance" }), ct, NullNext);

        await embedder.DidNotReceive()
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoTagIndex_NoOp()
    {
        var ct      = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var sut = new TagIngestionBehavior { TagIndex = null, Embedder = embedder };

        await sut.HandleAsync(MakeCtx(new(StringComparer.Ordinal) { ["dept"] = "finance" }), ct, NullNext);

        await embedder.DidNotReceive()
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbeddingFailure_NonFatal_NextStillCalled()
    {
        var ct      = TestContext.Current.CancellationToken;
        var index   = Substitute.For<ITagIndex>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("down"));

        var nextCalled = false;
        ValueTask<IngestionResult> Next(IngestionContext c, CancellationToken t)
        {
            nextCalled = true;
            return NullNext(c, t);
        }

        var sut = new TagIngestionBehavior { TagIndex = index, Embedder = embedder };
        await sut.HandleAsync(MakeCtx(new(StringComparer.Ordinal) { ["dept"] = "finance" }), ct, Next);

        Assert.True(nextCalled);
        index.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>());
    }
}
