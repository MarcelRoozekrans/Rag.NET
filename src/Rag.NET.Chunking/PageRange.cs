using System.Runtime.InteropServices;
using Rag.NET.Models;

namespace Rag.NET.Chunking;

/// <summary>
/// Accumulates the source-page range of a run of merged sections: the min/max of the
/// <see cref="DocumentSection.PageNumber"/> values that are present. Sections without a page
/// number simply don't contribute — a merged chunk that touches page 3 and an unpaginated
/// section still reports page 3 rather than nulling the whole range (the mixed-run policy
/// documented on <see cref="ReservedMetadataKeys.PageEnd"/>). Folding only nulls yields a null
/// range, and <see cref="WriteTo"/> then writes nothing.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct PageRange(int? Start, int? End)
{
    public static PageRange Empty => default;

    public PageRange Fold(int? pageNumber) =>
        pageNumber is not { } page
            ? this
            : new PageRange(
                Start is { } start ? Math.Min(start, page) : page,
                End is { } end ? Math.Max(end, page) : page);

    /// <summary>Writes the range as the reserved page pair, or nothing when the range is empty.</summary>
    public void WriteTo(IDictionary<string, MetadataValue> metadata) =>
        PageMetadata.Write(metadata, Start, End);
}
