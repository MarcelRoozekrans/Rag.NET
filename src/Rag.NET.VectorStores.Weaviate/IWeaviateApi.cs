using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.Weaviate;

/// <summary>
/// Minimal Weaviate REST + GraphQL API surface: REST for schema management and batch
/// writes, a single GraphQL POST for search (the Linear POST-with-<c>[Body]</c> GraphQL
/// pattern; no dedicated GraphQL client dependency).
/// </summary>
[ZeroAllocRestClient]
internal interface IWeaviateApi
{
    [Post("/v1/schema")]
    Task<Result<WeaviateClass, ZeroAlloc.Rest.HttpError>> CreateClassAsync(
        [Body] WeaviateClass body,
        CancellationToken cancellationToken = default);

    /// <summary>Exists-probe: 200 with the class definition, or 404 when missing.</summary>
    [Get("/v1/schema/{className}")]
    Task<Result<WeaviateClass, ZeroAlloc.Rest.HttpError>> GetClassAsync(
        string className,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a class (and all its objects). Weaviate answers 200 with an empty body —
    /// hence the plain <see cref="Task"/> return instead of a deserialized
    /// <c>Result&lt;T&gt;</c> envelope.
    /// </summary>
    [Delete("/v1/schema/{className}")]
    Task DeleteClassAsync(
        string className,
        CancellationToken cancellationToken = default);

    [Post("/v1/schema/{className}/tenants")]
    Task<Result<IReadOnlyList<WeaviateTenant>, ZeroAlloc.Rest.HttpError>> CreateTenantsAsync(
        string className,
        [Body] IReadOnlyList<WeaviateTenant> body,
        CancellationToken cancellationToken = default);

    [Post("/v1/batch/objects")]
    Task<Result<IReadOnlyList<WeaviateBatchObjectResult>, ZeroAlloc.Rest.HttpError>> BatchObjectsAsync(
        [Body] WeaviateBatchObjectsRequest body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete-by-filter. NOTE: despite older docs referring to a <c>POST /v1/batch/delete</c>
    /// endpoint, that path does not exist on current Weaviate (404 verified against 1.39) —
    /// batch deletion is <c>DELETE /v1/batch/objects</c> with a JSON body. Multi-tenant
    /// deletes carry the tenant as a query parameter.
    /// </summary>
    [Delete("/v1/batch/objects")]
    Task<Result<WeaviateBatchDeleteResponse, ZeroAlloc.Rest.HttpError>> BatchDeleteAsync(
        [Body] WeaviateBatchDeleteRequest body,
        [Query(Name = "tenant")] string? tenant = null,
        CancellationToken cancellationToken = default);

    [Post("/v1/graphql")]
    Task<Result<WeaviateGraphQlResponse, ZeroAlloc.Rest.HttpError>> QueryAsync(
        [Body] WeaviateGraphQlRequest body,
        CancellationToken cancellationToken = default);
}
