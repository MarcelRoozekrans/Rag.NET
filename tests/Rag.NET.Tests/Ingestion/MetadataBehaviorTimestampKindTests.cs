using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

/// <summary>
/// Holds <c>created_at</c> and <c>updated_at</c> to a TYPED metadata value rather than a formatted
/// string.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these pin, reported as issue #435 by an external user.</b> Both keys were written
/// with <c>.ToString("O")</c>, so their <see cref="MetadataValue.Kind"/> was
/// <see cref="MetadataValueKind.String"/> and every store's date mapping was bypassed. On Azure AI
/// Search the index declares <c>dateValue</c> as a filterable
/// <c>SearchFieldDataType.DateTimeOffset</c> — it is <i>built</i> for range filtering — and the
/// value landed in <c>stringValue</c> instead. "Documents changed since X" had no supported path,
/// on a field that exists to answer exactly that.
/// </para>
/// <para>
/// <b>The instant must not move, which is the whole difficulty.</b> A <see cref="DateTime"/> with
/// <see cref="DateTimeKind.Unspecified"/> — what a sitemap's <c>lastmod</c> parses to, and what the
/// reporter's payload showed — has no offset, and <c>new DateTimeOffset(dt)</c> would silently
/// apply the machine's local offset and change the moment being recorded. The string form did not
/// do that: <c>.ToString("O")</c> emitted no suffix and the reader parsed it back with
/// <see cref="System.Globalization.DateTimeStyles.RoundtripKind"/> as Unspecified. Unspecified is
/// therefore read as UTC here, which preserves the clock reading every existing index already
/// holds. Getting this wrong would shift every timestamp by the ingesting machine's offset —
/// invisibly, and differently per machine.
/// </para>
/// </remarks>
public sealed class MetadataBehaviorTimestampKindTests
{
    [Fact]
    public async Task UpdatedAt_IsWrittenAsATypedDate_NotAString()
    {
        var chunk = await WriteAsync(updatedAt: new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc));

        Assert.True(chunk.Metadata.TryGetValue(ReservedMetadataKeys.UpdatedAt, out var value));
        Assert.Equal(MetadataValueKind.DateTimeOffset, value.Kind);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero),
            value.DateTimeOffsetValue);
    }

    [Fact]
    public async Task CreatedAt_IsWrittenAsATypedDate_NotAString()
    {
        var chunk = await WriteAsync(createdAt: new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc));

        Assert.True(chunk.Metadata.TryGetValue(ReservedMetadataKeys.CreatedAt, out var value));
        Assert.Equal(MetadataValueKind.DateTimeOffset, value.Kind);
        Assert.Equal(
            new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero),
            value.DateTimeOffsetValue);
    }

    [Fact]
    public async Task AnUnspecifiedKind_IsRecordedAsUtc_SoTheInstantDoesNotMove()
    {
        // The case the reporter's payload actually showed: "2026-03-02T00:00:00.0000000", no
        // suffix, because a sitemap's lastmod parses to Unspecified. Reading it as local time
        // would move the recorded moment by the ingesting machine's offset — invisibly, and
        // differently on a developer's laptop and a CI runner in another zone.
        //
        // HONEST LIMIT OF THIS TEST, stated so nobody credits CI with catching the defect: on a
        // machine whose local offset is zero, "treat Unspecified as UTC" and "treat it as Local"
        // are the SAME function, so no assertion here can separate them. GitHub's runners are UTC.
        // Mutating the converter to Local was caught on this author's UTC+2 machine and would pass
        // green in CI. The test is real but its guard is zone-dependent, and a reader who assumes
        // otherwise would be trusting a band that cannot catch what it names.
        var chunk = await WriteAsync(
            updatedAt: new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Unspecified));

        var value = chunk.Metadata[ReservedMetadataKeys.UpdatedAt];

        Assert.Equal(MetadataValueKind.DateTimeOffset, value.Kind);
        Assert.Equal(TimeSpan.Zero, value.DateTimeOffsetValue.Offset);
        Assert.Equal(
            new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
            value.DateTimeOffsetValue.UtcDateTime);
    }

    [Fact]
    public async Task ALocalKind_KeepsItsInstant_RatherThanItsClockReading()
    {
        // The mirror of the case above, and it must NOT be treated the same way. A Local timestamp
        // genuinely carries an offset, so the instant is what to preserve; forcing it to UTC would
        // change the moment rather than protect it.
        var local = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Local);
        var chunk = await WriteAsync(updatedAt: local);

        var value = chunk.Metadata[ReservedMetadataKeys.UpdatedAt];

        Assert.Equal(MetadataValueKind.DateTimeOffset, value.Kind);
        Assert.Equal(local.ToUniversalTime(), value.DateTimeOffsetValue.UtcDateTime);
    }

    private static async Task<TextChunk> WriteAsync(
        DateTime? createdAt = null, DateTime? updatedAt = null)
    {
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("doc1"),
            FileName = "doc1.txt",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };

        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = metadata,
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk
        {
            Text = "chunk",
            DocumentId = metadata.DocumentId,
            ChunkIndex = 0,
        });

        await new MetadataBehavior().HandleAsync(
            ctx,
            TestContext.Current.CancellationToken,
            static (c, _) => ValueTask.FromResult(
                new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        return ctx.Chunks[0];
    }
}
