using System.Globalization;
using Rag.NET.Models;

namespace Rag.NET.GraphRag;

/// <summary>
/// Formats and parses the chunk ids the graph records provenance with —
/// <c>{DocumentId}_{ChunkIndex}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The format was a string interpolation inside <c>GraphEntityExtractionBehavior</c>, written once
/// and never read back, because nothing resolved a provenance id to a chunk. Local search does, so
/// there is now a parser — and a parser and a formatter that live apart drift, which for an
/// identity format means a lookup that silently finds nothing.
/// </para>
/// <para>
/// <b>Document ids containing underscores parse correctly.</b> The split is on the <i>last</i>
/// underscore, and a chunk index is an integer with no underscore in it, so
/// <c>my_doc_3</c> resolves to <c>("my_doc", 3)</c> rather than <c>("my", "doc_3")</c>. Negative
/// indices — which <c>GraphEntityExtractionBehavior</c> assigns to synthetic entity and
/// relationship chunks — round-trip too.
/// </para>
/// </remarks>
internal static class GraphChunkId
{
    /// <summary>Formats a chunk's provenance id.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <returns>The id.</returns>
    internal static string Format(TextChunk chunk) =>
        Format(chunk.DocumentId.Value, chunk.ChunkIndex);

    /// <summary>Formats a provenance id from its parts.</summary>
    /// <param name="documentId">Owning document.</param>
    /// <param name="chunkIndex">Position within it.</param>
    /// <returns>The id.</returns>
    internal static string Format(string documentId, int chunkIndex) =>
        documentId + "_" + chunkIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses a provenance id back to a chunk identity.</summary>
    /// <remarks>
    /// Returns <see langword="false"/> rather than throwing for anything unparseable. Graph rows
    /// are written by extraction runs that may predate this format, and one malformed id is a
    /// chunk that does not reach the context — not a query that fails.
    /// </remarks>
    /// <param name="id">The provenance id.</param>
    /// <param name="key">The parsed identity.</param>
    /// <returns>Whether the id parsed.</returns>
    internal static bool TryParse(string? id, out ChunkKey key)
    {
        key = default;

        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var separator = id.LastIndexOf('_');
        if (separator <= 0 || separator == id.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(
                id.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return false;
        }

        key = new ChunkKey(id[..separator], index);
        return true;
    }
}
