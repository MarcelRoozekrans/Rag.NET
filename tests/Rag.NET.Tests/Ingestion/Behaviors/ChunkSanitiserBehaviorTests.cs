using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

file sealed class CapturingChunkSanitiser : IChunkSanitiser
{
    public List<string> Seen { get; } = [];
    public string Sanitise(string text, IReadOnlyDictionary<string, MetadataValue> metadata)
    {
        Seen.Add(text);
        return text.Replace("bad", "[REDACTED]", StringComparison.Ordinal);
    }
}

public class ChunkSanitiserBehaviorTests
{
    private static IngestionContext MakeContext(params string[] texts)
    {
        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("doc1"), FileName = "f.pdf" },
            GetNextBm25DocId = () => 1,
        };
        foreach (var (t, i) in texts.Select((t, i) => (t, i)))
        {
            ctx.Chunks.Add(new TextChunk
            {
                Text = t,
                DocumentId = new DocumentId("doc1"),
                ChunkIndex = i,
            });
        }
        return ctx;
    }

    private static ValueTask<IngestionResult> StubNext(IngestionContext c, CancellationToken _) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.Chunks.Count });

    [Fact]
    public async Task HandleAsync_CallsSanitiserForEachChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var sanitiser = new CapturingChunkSanitiser();
        var behavior = new ChunkSanitiserBehavior { Sanitisers = [sanitiser] };
        var ctx = MakeContext("chunk one", "chunk two");

        await behavior.HandleAsync(ctx, ct, StubNext);

        Assert.Equal(["chunk one", "chunk two"], sanitiser.Seen);
    }

    [Fact]
    public async Task HandleAsync_SanitisedTextReplacedOnContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var sanitiser = new CapturingChunkSanitiser();
        var behavior = new ChunkSanitiserBehavior { Sanitisers = [sanitiser] };
        var ctx = MakeContext("contains bad word");

        await behavior.HandleAsync(ctx, ct, StubNext);

        Assert.Equal("contains [REDACTED] word", ctx.Chunks[0].Text);
    }

    [Fact]
    public async Task HandleAsync_NoSanitisers_PassesThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var behavior = new ChunkSanitiserBehavior { Sanitisers = [] };
        var ctx = MakeContext("any text");
        var called = false;

        await behavior.HandleAsync(ctx, ct, (c, _) =>
        {
            called = true;
            return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 });
        });

        Assert.True(called);
    }

    [Fact]
    public async Task HandleAsync_MultipleSanitisers_RunInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new CapturingChunkSanitiser();   // replaces "bad" with "[REDACTED]"
        var second = new CapturingChunkSanitiser();  // receives already-sanitised text
        var behavior = new ChunkSanitiserBehavior { Sanitisers = [first, second] };
        var ctx = MakeContext("bad word");

        await behavior.HandleAsync(ctx, ct, StubNext);

        // second sees the output of first
        Assert.Equal(["bad word"], first.Seen);
        Assert.Equal(["[REDACTED] word"], second.Seen);
        Assert.Equal("[REDACTED] word", ctx.Chunks[0].Text);
    }

    [Fact]
    public async Task HandleAsync_NextIsCalled_AfterSanitisation()
    {
        var ct = TestContext.Current.CancellationToken;
        var behavior = new ChunkSanitiserBehavior { Sanitisers = [new CapturingChunkSanitiser()] };
        var ctx = MakeContext("some text");
        var nextCalled = false;

        await behavior.HandleAsync(ctx, ct, (c, _) =>
        {
            nextCalled = true;
            return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 });
        });

        Assert.True(nextCalled);
    }
}
