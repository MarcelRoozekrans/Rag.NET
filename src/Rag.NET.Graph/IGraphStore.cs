namespace Rag.NET.Graph;

/// <summary>Abstraction for storing and querying entity-relationship graphs.</summary>
public interface IGraphStore : IAsyncDisposable
{
    /// <summary>Add or merge entities into the store. Duplicate names merge descriptions.</summary>
    /// <remarks>
    /// <b>Merging means appending, so this is not the way to update an entity you already read.</b>
    /// Re-adding a row against itself concatenates its description onto a copy of itself. Callers
    /// holding entities that came out of <see cref="GetFullGraphAsync"/> and wanting to change one
    /// field want a targeted writer such as <see cref="SetPageRankScoresAsync"/>.
    /// </remarks>
    Task AddEntitiesAsync(IReadOnlyList<GraphEntity> entities, CancellationToken ct = default);

    /// <summary>Set PageRank scores by entity name, leaving every other column untouched.</summary>
    /// <param name="scores">Scores by entity name; names not in the store are ignored.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <remarks>
    /// Exists because the alternative was <see cref="AddEntitiesAsync"/>, and community detection
    /// using it to persist scores appended every entity's description to itself once per run —
    /// which, as an ingestion behavior running per document, doubled them per document.
    /// </remarks>
    Task SetPageRankScoresAsync(
        IReadOnlyDictionary<string, double> scores, CancellationToken ct = default);

    /// <summary>Add relationships between entities.</summary>
    Task AddRelationshipsAsync(IReadOnlyList<GraphRelationship> relationships, CancellationToken ct = default);

    /// <summary>Get entities reachable from the given entity within the specified hop depth.</summary>
    Task<IReadOnlyList<GraphEntity>> GetNeighborsAsync(string entityName, int depth, CancellationToken ct = default);

    /// <summary>Get all relationships where the given entity is source or target.</summary>
    Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(string entityName, CancellationToken ct = default);

    /// <summary>Replace all community assignments with the provided list.</summary>
    Task SetCommunitiesAsync(IReadOnlyList<Community> communities, CancellationToken ct = default);

    /// <summary>Get all communities that contain the given entity.</summary>
    Task<IReadOnlyList<Community>> GetCommunitiesForEntityAsync(string entityName, CancellationToken ct = default);

    /// <summary>Fetches entities by name, skipping names the store does not hold.</summary>
    /// <remarks>
    /// <para>
    /// GraphRAG's local search selects entities by searching their embeddings and then needs the
    /// <i>merged</i> entity behind each hit — the union of its source chunks and the description
    /// assembled across every document that mentioned it. The embedded chunk carries only the view
    /// one document's extraction produced, so it cannot stand in for the row.
    /// </para>
    /// <para>
    /// <b>The default implementation loads the entire graph and filters it, and stores should
    /// override it.</b> That is correct at any size and ruinous past a few thousand entities — a
    /// per-query whole-graph read is exactly the cost #300 removed from ingestion. It is a default
    /// rather than a required member so that implementations outside this repository keep
    /// compiling and keep working, slowly, rather than breaking.
    /// </para>
    /// </remarks>
    /// <param name="names">Entity names, in any casing.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The entities that exist, in unspecified order.</returns>
    async Task<IReadOnlyList<GraphEntity>> GetEntitiesAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var graph = await GetFullGraphAsync(ct).ConfigureAwait(false);
        var found = new List<GraphEntity>(names.Count);

        for (var i = 0; i < graph.Entities.Count; i++)
        {
            if (wanted.Contains(graph.Entities[i].Name))
            {
                found.Add(graph.Entities[i]);
            }
        }

        return found;
    }

    /// <summary>Counts each named entity's relationships — its degree in the whole graph.</summary>
    /// <remarks>
    /// <para>
    /// Upstream calls this an entity's <c>rank</c>, and a relationship's rank is the sum of its two
    /// endpoints' degrees — the value local search ranks its relationship table by. It has to be a
    /// whole-graph degree: counting only edges that touch the selected entities gives a different
    /// number and a different table.
    /// </para>
    /// <para>
    /// The default answers one query per name via <see cref="GetRelationshipsAsync"/>. Correct, and
    /// worth overriding with a single grouped count.
    /// </para>
    /// </remarks>
    /// <param name="names">Entity names, in any casing.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Degree by entity name; a name with no relationships maps to 0.</returns>
    async Task<IReadOnlyDictionary<string, int>> GetDegreesAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var degrees = new Dictionary<string, int>(names.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            var relationships = await GetRelationshipsAsync(names[i], ct).ConfigureAwait(false);
            degrees[names[i]] = relationships.Count;
        }

        return degrees;
    }

    /// <summary>Gets every relationship touching any of the named entities, without duplicates.</summary>
    /// <remarks>
    /// The batch form matters because local search asks about ten to twenty entities at once, and
    /// an edge between two of them is one relationship rather than two. The default loops
    /// <see cref="GetRelationshipsAsync"/> and de-duplicates on the endpoints and description,
    /// which a store with a real <c>IN</c> clause should replace.
    /// </remarks>
    /// <param name="names">Entity names, in any casing.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The relationships, each once.</returns>
    async Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var seen = new HashSet<(string, string, string)>();
        var found = new List<GraphRelationship>();

        for (var i = 0; i < names.Count; i++)
        {
            var relationships = await GetRelationshipsAsync(names[i], ct).ConfigureAwait(false);
            for (var j = 0; j < relationships.Count; j++)
            {
                var rel = relationships[j];
                if (seen.Add((rel.SourceEntity, rel.TargetEntity, rel.Description)))
                {
                    found.Add(rel);
                }
            }
        }

        return found;
    }

    /// <summary>Gets every community containing any of the named entities, without duplicates.</summary>
    /// <remarks>
    /// A community holding several of the selected entities is returned once. Which one, and how
    /// many of them it holds, is the caller's ordering key — local search ranks reports by exactly
    /// that count — so returning the community twice would double it.
    /// </remarks>
    /// <param name="names">Entity names, in any casing.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The communities, each once.</returns>
    async Task<IReadOnlyList<Community>> GetCommunitiesForEntitiesAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var byId = new Dictionary<int, Community>();
        for (var i = 0; i < names.Count; i++)
        {
            var communities = await GetCommunitiesForEntityAsync(names[i], ct).ConfigureAwait(false);
            for (var j = 0; j < communities.Count; j++)
            {
                _ = byId.TryAdd(communities[j].Id, communities[j]);
            }
        }

        return byId.Values.ToList();
    }

    /// <summary>Load the complete graph — all entities, relationships, and communities.</summary>
    Task<GraphSnapshot> GetFullGraphAsync(CancellationToken ct = default);

    /// <summary>Delete all entities and relationships originating from the given document.</summary>
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default);
}
