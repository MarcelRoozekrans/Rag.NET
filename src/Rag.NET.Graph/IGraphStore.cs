namespace Rag.NET.Graph;

/// <summary>Abstraction for storing and querying entity-relationship graphs.</summary>
public interface IGraphStore : IAsyncDisposable
{
    Task AddEntitiesAsync(IReadOnlyList<GraphEntity> entities, CancellationToken ct = default);
    Task AddRelationshipsAsync(IReadOnlyList<GraphRelationship> relationships, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEntity>> GetNeighborsAsync(string entityName, int depth, CancellationToken ct = default);
    Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(string entityName, CancellationToken ct = default);
    Task SetCommunitiesAsync(IReadOnlyList<Community> communities, CancellationToken ct = default);
    Task<IReadOnlyList<Community>> GetCommunitiesForEntityAsync(string entityName, CancellationToken ct = default);
    Task<GraphSnapshot> GetFullGraphAsync(CancellationToken ct = default);
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default);
}
