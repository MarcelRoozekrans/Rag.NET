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

    /// <summary>Load the complete graph — all entities, relationships, and communities.</summary>
    Task<GraphSnapshot> GetFullGraphAsync(CancellationToken ct = default);

    /// <summary>Delete all entities and relationships originating from the given document.</summary>
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default);
}
