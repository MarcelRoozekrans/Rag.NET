using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class MetadataBehaviorCreatedAtTests
{
    private static IngestionContext MakeCtx(DocumentMetadata metadata)
    {
        var ctx = new IngestionContext
        {
            Stream           = Stream.Null,
            Metadata         = metadata,
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk
        {
            Text       = "hello",
            DocumentId = metadata.DocumentId,
            ChunkIndex = 0,
        });
        return ctx;
    }

    private static ValueTask<IngestionResult> NullNext(IngestionContext ctx, CancellationToken _) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    [Fact]
    public async Task CreatedAt_SerializedIntoChunkMetadata()
    {
        var ct        = TestContext.Current.CancellationToken;
        var createdAt = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var ctx = MakeCtx(new DocumentMetadata
        {
            DocumentId = new DocumentId("doc1"),
            FileName   = "doc1.txt",
            CreatedAt  = createdAt,
        });

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, NullNext);

        Assert.True(ctx.Chunks[0].Metadata.TryGetValue("created_at", out var value));
        Assert.Equal(createdAt.ToString("O"), value);
    }

    [Fact]
    public async Task CreatedAt_ExistingTagPreservedViaTryAdd()
    {
        var ct  = TestContext.Current.CancellationToken;
        var ctx = MakeCtx(new DocumentMetadata
        {
            DocumentId = new DocumentId("doc1"),
            FileName   = "doc1.txt",
            CreatedAt  = DateTime.UtcNow,
            Tags       = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["created_at"] = "2020-01-01T00:00:00.0000000Z",
            },
        });

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, NullNext);

        // Tags are copied first; TryAdd on "created_at" from CreatedAt property then does nothing
        Assert.Equal<MetadataValue>("2020-01-01T00:00:00.0000000Z", ctx.Chunks[0].Metadata["created_at"]);
    }
}
