using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// The single definition of whether a chunk satisfies a metadata filter, shared by every
/// retrieval arm.
/// </summary>
/// <remarks>
/// <para>
/// This is public rather than internal on purpose. Implementing <see cref="IBm25Index"/> obliges
/// an implementer to honour <c>RetrievalOptions.MetadataFilter</c>, and shipping that obligation
/// without shipping the semantics would leave each implementer to reimplement typed equality by
/// guesswork — against a dense arm whose implementation they cannot read.
/// </para>
/// <para>
/// It exists because the matching used to be a private static inside <c>InMemoryVectorStore</c>.
/// Duplicating it into the BM25 indexes would have let the arms disagree about what matches, so a
/// filtered query would return different chunks depending on which arm found them — a new defect
/// of the same family as the one this was extracted to fix (#350).
/// </para>
/// </remarks>
public static class MetadataFilterMatcher
{
    /// <summary>
    /// Whether <paramref name="chunk"/> satisfies every pair in <paramref name="filter"/>.
    /// </summary>
    /// <param name="chunk">The chunk whose <see cref="TextChunk.Metadata"/> is tested.</param>
    /// <param name="filter">
    /// The required pairs. <see langword="null"/> or empty means no filtering, matching
    /// <c>RetrievalOptions.MetadataFilter</c>'s documented behaviour.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when every key is present and equal by <see cref="MetadataValue"/>
    /// equality — typed, so a Number <c>3</c> does not match a String <c>"3"</c>, and ordinal for
    /// strings. AND semantics across pairs.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="chunk"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Keys are compared using <paramref name="chunk"/>'s own <c>Metadata</c> dictionary's
    /// comparer — a caller-supplied dictionary (e.g. built with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>) governs key lookup here too. Values are
    /// compared by <see cref="MetadataValue"/> equality, which is typed and ordinal for strings
    /// regardless of the dictionary's key comparer.
    /// </remarks>
    public static bool Matches(TextChunk chunk, IDictionary<string, MetadataValue>? filter)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (filter is null || filter.Count == 0)
            return true;

        foreach (var (key, value) in filter)
        {
            // Typed equality: a Number 3 filter does not match a String "3" value.
            if (!chunk.Metadata.TryGetValue(key, out var actual) || actual != value)
                return false;
        }

        return true;
    }
}
