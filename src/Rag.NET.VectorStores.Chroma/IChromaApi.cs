using System.Text.Json;
using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.Chroma;

/// <summary>
/// Minimal Chroma REST v2 API surface, verified against the live
/// <c>chromadb/chroma:latest</c> image (server version 1.0.0, Rust implementation,
/// OpenAPI at <c>/openapi.json</c>). Collections are addressed by UUID for record
/// operations; the create/get/delete collection endpoints accept the collection
/// <em>name</em> in the <c>{collectionId}</c> path segment (verified: <c>GET
/// .../collections/{name}</c> answers 200 with the collection including its UUID, and
/// 404 when missing).
/// </summary>
[ZeroAllocRestClient]
internal interface IChromaApi
{
    /// <summary>
    /// Creates a collection (409 on duplicate). With <c>get_or_create: true</c> in the
    /// body, answers 200 with the existing collection instead — used for race-free
    /// name-to-UUID resolution.
    /// </summary>
    [Post("/api/v2/tenants/{tenant}/databases/{database}/collections")]
    Task<Result<ChromaCollectionInfo, ZeroAlloc.Rest.HttpError>> CreateCollectionAsync(
        string tenant,
        string database,
        [Body] ChromaCreateCollectionRequest body,
        CancellationToken cancellationToken = default);

    /// <summary>By-name lookup: 200 with the collection (including its UUID), or 404 when missing.</summary>
    [Get("/api/v2/tenants/{tenant}/databases/{database}/collections/{name}")]
    Task<Result<ChromaCollectionInfo, ZeroAlloc.Rest.HttpError>> GetCollectionAsync(
        string tenant,
        string database,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection (and all its records) by name. Unlike Weaviate, Chroma is NOT
    /// idempotent here: deleting a missing collection answers 404 (verified) — hence the
    /// <c>Result</c> envelope instead of a plain <see cref="Task"/>.
    /// </summary>
    [Delete("/api/v2/tenants/{tenant}/databases/{database}/collections/{name}")]
    Task<Result<JsonElement, ZeroAlloc.Rest.HttpError>> DeleteCollectionAsync(
        string tenant,
        string database,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Upsert by record id (200 with an empty <c>{}</c> body; 404 when the collection UUID is stale).</summary>
    [Post("/api/v2/tenants/{tenant}/databases/{database}/collections/{collectionId}/upsert")]
    Task<Result<JsonElement, ZeroAlloc.Rest.HttpError>> UpsertRecordsAsync(
        string tenant,
        string database,
        string collectionId,
        [Body] ChromaUpsertRequest body,
        CancellationToken cancellationToken = default);

    [Post("/api/v2/tenants/{tenant}/databases/{database}/collections/{collectionId}/query")]
    Task<Result<ChromaQueryResponse, ZeroAlloc.Rest.HttpError>> QueryAsync(
        string tenant,
        string database,
        string collectionId,
        [Body] ChromaQueryRequest body,
        CancellationToken cancellationToken = default);

    [Post("/api/v2/tenants/{tenant}/databases/{database}/collections/{collectionId}/delete")]
    Task<Result<ChromaDeleteRecordsResponse, ZeroAlloc.Rest.HttpError>> DeleteRecordsAsync(
        string tenant,
        string database,
        string collectionId,
        [Body] ChromaDeleteRecordsRequest body,
        CancellationToken cancellationToken = default);
}
