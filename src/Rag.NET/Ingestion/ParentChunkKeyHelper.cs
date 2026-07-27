using Rag.NET.Models;

namespace Rag.NET.Ingestion;

internal static class ParentChunkKeyHelper
{
    /// <summary>Alias of the canonical constant — see <see cref="ReservedMetadataKeys.ParentKey"/>.</summary>
    internal const string ParentKeyMetadata = ReservedMetadataKeys.ParentKey;

    internal static string GetParentKey(string documentId, int parentChunkIndex)
        => $"{documentId}:{parentChunkIndex}";

    internal static int FindParentIndex(IReadOnlyList<(int start, int end)> parentBoundaries, int childStart)
    {
        for (int i = 0; i < parentBoundaries.Count; i++)
        {
            if (childStart >= parentBoundaries[i].start && childStart <= parentBoundaries[i].end)
                return i;
        }

        // Fallback: assign to last parent
        return parentBoundaries.Count - 1;
    }
}
