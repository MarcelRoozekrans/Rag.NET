using Rag.NET.Graph;
using Rag.NET.Models;

namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// Everything <see cref="LocalSearchContextBuilder"/> assembles a context out of, already fetched.
/// </summary>
/// <remarks>
/// <para>
/// <b>The builder does no I/O, on purpose.</b> Fetching and assembly were one method in the
/// behaviour this replaces, and the assembly half was never written — the parts that talked to a
/// store got built and the part that used them did not. Separating them makes the assembly a pure
/// function of its inputs, which is a thing a unit test can hold to the specification row by row.
/// </para>
/// <para>
/// Populating this is the search entry point's job, which is where the store round trips and the
/// embedding call live.
/// </para>
/// </remarks>
public sealed record LocalSearchInputs
{
    /// <summary>
    /// The entities the query mapped to, <b>in selection order</b> — most similar first.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing twice over, so this is a list and not a set. It is the order the
    /// entity table renders in, and it is the primary sort key for source chunks: the first
    /// entity's chunks come before the second's regardless of how either chunk scored.
    /// </remarks>
    public required IReadOnlyList<GraphEntity> SelectedEntities { get; init; }

    /// <summary>
    /// Every relationship with at least one endpoint among <see cref="SelectedEntities"/>.
    /// </summary>
    /// <remarks>
    /// The builder partitions these into in-network and out-of-network itself, because which is
    /// which depends on the selection and the two are ranked and capped by different rules.
    /// Handing it a pre-filtered list would move that decision somewhere it cannot be tested
    /// against the specification.
    /// </remarks>
    public IReadOnlyList<GraphRelationship> Relationships { get; init; } = [];

    /// <summary>Communities the selected entities belong to, whose reports become the first section.</summary>
    public IReadOnlyList<Community> Communities { get; init; } = [];

    /// <summary>
    /// Source chunks by chunk id, for the ids listed in <see cref="GraphEntity.SourceChunkIds"/>.
    /// </summary>
    /// <remarks>
    /// Keyed the way <c>GraphEntityExtractionBehavior</c> writes them —
    /// <c>{DocumentId}_{ChunkIndex}</c>. An entity naming an id that is not here is skipped
    /// silently, which is the honest outcome when the document has since been deleted; it is not a
    /// reason to fail a query.
    /// </remarks>
    public IReadOnlyDictionary<string, TextChunk> SourceChunks { get; init; } =
        new Dictionary<string, TextChunk>(StringComparer.Ordinal);

    /// <summary>
    /// Degree — the number of relationships an entity has — by entity name, over the <i>whole</i>
    /// graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream calls this an entity's <c>rank</c> and computes it during indexing, and a
    /// relationship's own rank is the sum of its two endpoints' degrees. That sum is what ranks
    /// relationships within each section, so these have to be real graph degrees. Counting them
    /// from <see cref="Relationships"/> instead would count only edges touching the selection,
    /// which is a different number and would reorder the table.
    /// </para>
    /// <para>
    /// An entity absent here is treated as degree 0 rather than as an error: a graph whose degrees
    /// have not been computed produces a flat ranking, not a failed query.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, int> EntityDegrees { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The conversation this query arrives in, oldest turn first.</summary>
    public IReadOnlyList<ConversationTurn> ConversationHistory { get; init; } = [];
}
