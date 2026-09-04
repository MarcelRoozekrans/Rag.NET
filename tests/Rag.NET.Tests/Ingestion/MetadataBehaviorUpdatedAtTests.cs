using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

/// <summary>
/// Phase 4.10 Task 2: mirrors <see cref="MetadataBehaviorCreatedAtTests"/> exactly, for
/// <see cref="DocumentMetadata.UpdatedAt"/> → <c>updated_at</c>.
/// </summary>
public class MetadataBehaviorUpdatedAtTests
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
    public async Task UpdatedAt_SerializedIntoChunkMetadata()
    {
        var ct        = TestContext.Current.CancellationToken;
        var updatedAt = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var ctx = MakeCtx(new DocumentMetadata
        {
            DocumentId = new DocumentId("doc1"),
            FileName   = "doc1.txt",
            UpdatedAt  = updatedAt,
        });

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, NullNext);

        Assert.True(ctx.Chunks[0].Metadata.TryGetValue(ReservedMetadataKeys.UpdatedAt, out var value));
        // Was `Assert.Equal(updatedAt.ToString("O"), value)` until issue #435: the key is written
        // as a TYPED date now, so the stores' date mappings apply and Azure AI Search's filterable
        // `dateValue` receives it instead of `stringValue`. This test pinned the defect.
        Assert.Equal(MetadataValueKind.DateTimeOffset, value.Kind);
        Assert.Equal(new DateTimeOffset(updatedAt), value.DateTimeOffsetValue);
    }

    [Fact]
    public async Task UpdatedAt_Absent_NoTagWritten()
    {
        var ct  = TestContext.Current.CancellationToken;
        var ctx = MakeCtx(new DocumentMetadata
        {
            DocumentId = new DocumentId("doc1"),
            FileName   = "doc1.txt",
        });

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, NullNext);

        Assert.False(
            ctx.Chunks[0].Metadata.TryGetValue(ReservedMetadataKeys.UpdatedAt, out var fabricated),
            $"UpdatedAt was not set; chunk metadata must not fabricate '{ReservedMetadataKeys.UpdatedAt}', but found '{fabricated}'.");
    }

    [Fact]
    public async Task UpdatedAt_ExistingTagPreservedViaTryAdd()
    {
        var ct  = TestContext.Current.CancellationToken;
        var ctx = MakeCtx(new DocumentMetadata
        {
            DocumentId = new DocumentId("doc1"),
            FileName   = "doc1.txt",
            UpdatedAt  = DateTime.UtcNow,
            Tags       = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["updated_at"] = "2020-01-01T00:00:00.0000000Z",
            },
        });

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, NullNext);

        // Tags are copied first; TryAdd on "updated_at" from UpdatedAt property then does nothing
        Assert.Equal<MetadataValue>("2020-01-01T00:00:00.0000000Z", ctx.Chunks[0].Metadata["updated_at"]);
    }
}
