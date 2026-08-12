namespace Rag.NET.Graph;

/// <summary>
/// How entity names compare anywhere in the graph: case-insensitively, because that is how the
/// store that holds them compares.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the answer was once given three different times in three different
/// places, and one of them was wrong.</b> <see cref="SqliteGraphStore"/> declares
/// <c>entities.name</c> as <c>TEXT PRIMARY KEY COLLATE NOCASE</c> and matches relationship
/// endpoints with <c>COLLATE NOCASE</c>, so "Google" and "google" are one entity there and an edge
/// between them is an edge it will traverse. The Leiden clusterer and PageRank matched endpoints to
/// names with <see cref="StringComparer.Ordinal"/>, and so silently dropped every edge whose
/// endpoint was spelled with different casing from the entity it named — edges the store held and
/// would happily walk. Over a sixty-article corpus that cost the clustering most of its structure.
/// </para>
/// <para>
/// <b>Case-insensitive is the correct direction rather than merely the convenient one.</b> The
/// store is the authority on what an entity <i>is</i>: a snapshot always arrives from
/// <see cref="IGraphStore.GetFullGraphAsync"/>, whose <c>NOCASE</c> primary key means it cannot
/// contain two entities differing only in case — <c>AddEntitiesAsync</c> has merged them into one
/// row long before any algorithm sees them. Making the comparison ordinal everywhere instead would
/// mean changing the schema so those rows stay apart, which changes what is <i>stored</i> rather
/// than what is <i>clustered</i>, splits entities the extractor has been merging since the package
/// shipped, and needs a migration. The algorithms were the ones disagreeing with the data.
/// </para>
/// </remarks>
public static class GraphNames
{
    /// <summary>
    /// Gets the comparer to use for entity names — keys of any name-indexed lookup, and any
    /// comparison of a relationship endpoint against an entity name.
    /// </summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
}
