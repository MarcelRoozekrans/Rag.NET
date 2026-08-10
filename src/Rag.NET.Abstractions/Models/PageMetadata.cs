namespace Rag.NET.Models;

/// <summary>
/// Writes the reserved <see cref="ReservedMetadataKeys.Page"/>/<see cref="ReservedMetadataKeys.PageEnd"/>
/// pair into chunk metadata, from <see cref="DocumentSection.PageNumber"/>. The single place that
/// upholds the pair's invariant — both keys written as numbers, or neither — so the built-in
/// chunking strategies (and any custom <c>IChunkingStrategy</c> that wants its chunks findable by
/// page) cannot drift on it independently.
/// </summary>
public static class PageMetadata
{
    /// <summary>
    /// Metadata for a chunk cut from a single section: <c>page</c> and <c>page_end</c> both set
    /// to <paramref name="pageNumber"/> when the section has one (a chunk entirely on page 3 is
    /// <c>page: 3, page_end: 3</c>, never a lone <c>page</c>), an empty dictionary — the same
    /// value <see cref="TextChunk.Metadata"/> defaults to — when the source has no page concept.
    /// </summary>
    public static IDictionary<string, MetadataValue> ForPage(int? pageNumber)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        Write(metadata, pageNumber, pageNumber);
        return metadata;
    }

    /// <summary>
    /// Writes <c>page</c> = <paramref name="pageStart"/> and <c>page_end</c> =
    /// <paramref name="pageEnd"/> into <paramref name="metadata"/>, or writes nothing when
    /// <paramref name="pageStart"/> is <see langword="null"/> — absence, not a null value, is how
    /// "no page" is represented. A <see langword="null"/> <paramref name="pageEnd"/> alongside a
    /// non-null start falls back to the start, keeping the both-or-neither invariant.
    /// </summary>
    public static void Write(IDictionary<string, MetadataValue> metadata, int? pageStart, int? pageEnd)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (pageStart is not { } start)
            return;

        metadata[ReservedMetadataKeys.Page] = start;
        metadata[ReservedMetadataKeys.PageEnd] = pageEnd ?? start;
    }
}
