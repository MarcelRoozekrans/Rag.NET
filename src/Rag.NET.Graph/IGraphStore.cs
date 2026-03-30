namespace Rag.NET.Graph;

/// <summary>Abstraction for storing and querying entity-relationship graphs.</summary>
public interface IGraphStore : IAsyncDisposable
{
    /// <summary>Add or merge entities into the store. Duplicate names merge descriptions.</summary>
    Task AddEntitiesAsync(IReadOnlyList<GraphEntity> entities, CancellationToken ct = default);

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
