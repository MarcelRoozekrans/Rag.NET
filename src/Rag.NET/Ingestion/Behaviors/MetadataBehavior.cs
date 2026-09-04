using System.Runtime.InteropServices;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class MetadataBehavior : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        foreach (ref var chunk in CollectionsMarshal.AsSpan(ctx.Chunks))
        {
            foreach (var tag in ctx.Metadata.Tags)
                chunk.Metadata.TryAdd(tag.Key, tag.Value);
            chunk.Metadata.TryAdd(ReservedMetadataKeys.DocumentId, (string)ctx.Metadata.DocumentId);
            chunk.Metadata.TryAdd(ReservedMetadataKeys.FileName,   ctx.Metadata.FileName);
            if (ctx.Metadata.CreatedAt is { } createdAt)
            {
                chunk.Metadata.TryAdd(ReservedMetadataKeys.CreatedAt, AsTypedDate(createdAt));
            }
            if (ctx.Metadata.UpdatedAt is { } updatedAt)
            {
                chunk.Metadata.TryAdd(ReservedMetadataKeys.UpdatedAt, AsTypedDate(updatedAt));
            }
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts a document timestamp to the typed metadata value the stores' date mappings expect,
    /// without moving the instant it records.
    /// </summary>
    /// <param name="timestamp">The <see cref="DocumentMetadata.CreatedAt"/> or
    /// <see cref="DocumentMetadata.UpdatedAt"/> value.</param>
    /// <returns>A <see cref="MetadataValueKind.DateTimeOffset"/> value.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why typed at all (issue #435).</b> These keys used to be written with
    /// <c>.ToString("O")</c>, so their kind was <see cref="MetadataValueKind.String"/> and every
    /// store's date mapping was bypassed. Azure AI Search declares <c>dateValue</c> as a filterable
    /// <c>DateTimeOffset</c> — a field built for range filtering — and the value landed in
    /// <c>stringValue</c>, leaving "documents changed since X" with no supported path.
    /// </para>
    /// <para>
    /// <b>Why <see cref="DateTimeKind.Unspecified"/> is read as UTC rather than local.</b> A
    /// sitemap's <c>lastmod</c>, and most provider timestamps, parse to Unspecified — no offset,
    /// just a clock reading. <c>new DateTimeOffset(dt)</c> would apply the ingesting machine's
    /// local offset and silently change the moment recorded, differently on a laptop and a CI
    /// runner. The string form never did that: <c>"O"</c> emitted no suffix and the reader parsed
    /// it back as Unspecified. Treating it as UTC preserves the clock reading every existing index
    /// already holds, so old rows and new rows mean the same thing.
    /// </para>
    /// <para>
    /// <b>Local is not treated that way, deliberately.</b> A Local timestamp genuinely carries an
    /// offset, so its instant is what to preserve; forcing it to UTC would change the moment rather
    /// than protect it. The two cases look symmetrical and are not.
    /// </para>
    /// </remarks>
    private static MetadataValue AsTypedDate(DateTime timestamp) =>
        timestamp.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc))
            : new DateTimeOffset(timestamp);
}
