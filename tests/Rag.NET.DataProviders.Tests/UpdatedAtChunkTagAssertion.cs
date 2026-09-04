using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.DataProviders.Testing;

/// <summary>
/// Shared assertion for Phase 4.10 Task 4: proves a connector's typed
/// <c>FileEntry.UpdatedAt</c> — read off <c>FileHandle.UpdatedAt</c> — still reaches chunk
/// metadata as the reserved <c>updated_at</c> tag, via the real <see cref="MetadataBehavior"/>
/// rather than a hand-rolled stand-in for it.
/// </summary>
/// <remarks>
/// <b>This file lives in <c>tests/Rag.NET.DataProviders.Tests</c> and is <i>linked</i></b> into
/// the four connector test projects whose provider used to write <c>updated_at</c> as a plain
/// <c>entry.Metadata</c> tag (Asana, Jira, Notion, Zendesk) — the same way
/// <see cref="MetadataContract"/> is. See that type's remarks for why linking rather than a
/// project reference.
/// <para>
/// The probe deliberately does not go through <c>IngestFromProviderAsync</c> or a mocked
/// <c>IRagPipeline</c>: that path is already covered generically for every connector by
/// <c>IngestFromProviderTests.IngestFromProviderAsync_FileEntryTimestamps_ReachIngestedDocument</c>.
/// What is connector-specific, and what this assertion exists to catch per connector, is whether
/// the raw string a connector's API model actually returns parses into a non-null
/// <see cref="DocumentMetadata.UpdatedAt"/> at all — a regression there would make this assertion
/// fail on <see cref="Assert.NotNull(object?)"/> before <see cref="MetadataBehavior"/> is ever
/// reached.
/// </para>
/// </remarks>
internal static class UpdatedAtChunkTagAssertion
{
    /// <summary>
    /// Asserts <paramref name="updatedAt"/> is set and that running it through the real
    /// <see cref="MetadataBehavior"/> writes it onto a chunk as
    /// <see cref="ReservedMetadataKeys.UpdatedAt"/>, round-trip formatted.
    /// </summary>
    /// <param name="updatedAt">The connector's parsed <c>FileEntry.UpdatedAt</c>.</param>
    /// <param name="cancellationToken">Cancels the behavior invocation.</param>
    public static async Task AssertSurfacesAsChunkTagAsync(
        DateTime? updatedAt, CancellationToken cancellationToken)
    {
        Assert.True(updatedAt.HasValue, "Connector produced no typed UpdatedAt to probe.");

        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("chunk-tag-probe"),
            FileName   = "probe.md",
            UpdatedAt  = updatedAt,
        };
        var ctx = new IngestionContext
        {
            Stream           = Stream.Null,
            Metadata         = metadata,
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk { Text = "probe", DocumentId = metadata.DocumentId, ChunkIndex = 0 });

        var behavior = new MetadataBehavior();
        _ = await behavior.HandleAsync(ctx, cancellationToken,
            (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var wrote = ctx.Chunks[0].Metadata.TryGetValue(ReservedMetadataKeys.UpdatedAt, out var tag);
        Assert.True(wrote, "MetadataBehavior did not write the reserved updated_at chunk tag.");
        // Was `Assert.Equal(updatedAt.Value.ToString("O"), tag)` until issue #435. The tag is a
        // TYPED date now, so a store's date mapping applies to it — Azure AI Search's filterable
        // `dateValue` rather than `stringValue`. The string comparison could not see the change:
        // a String-kind and a DateTimeOffset-kind MetadataValue holding the same instant render
        // identically, which is why CI reported "Expected: ...Z / Actual: ...Z / Values differ".
        Assert.Equal(MetadataValueKind.DateTimeOffset, tag.Kind);
        Assert.Equal(new DateTimeOffset(updatedAt.Value), tag.DateTimeOffsetValue);
    }
}
