using Xunit;
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.Weaviate.Tests;

/// <summary>
/// Pins <c>DeleteByDocumentIdAsync</c>'s handling of Weaviate's batch-delete counters via a
/// fake API — the live container cannot cheaply produce partial failures or documents larger
/// than the per-call deletion cap (default 10,000 objects).
/// </summary>
public class WeaviateBatchDeleteTests
{
    [Fact]
    public async Task DeleteByDocumentId_PartialFailure_Throws()
    {
        var api = new FakeBatchDeleteApi(
            Response(limit: 10_000, matches: 5, successful: 3, failed: 2));
        using var store = CreateStore(api);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.DeleteByDocumentIdAsync("doc-x", TestContext.Current.CancellationToken));

        Assert.Contains("2 of 5", exception.Message, StringComparison.Ordinal);
        Assert.Contains("doc-x", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteByDocumentId_MoreChunksThanDeletionCap_LoopsUntilBelowCap()
    {
        // Two full-cap passes followed by a tail pass: the store must call the API three
        // times — a single-pass implementation would leave objects behind.
        var api = new FakeBatchDeleteApi(
            Response(limit: 2, matches: 2, successful: 2, failed: 0),
            Response(limit: 2, matches: 2, successful: 2, failed: 0),
            Response(limit: 2, matches: 1, successful: 1, failed: 0));
        using var store = CreateStore(api);

        await store.DeleteByDocumentIdAsync("doc-big", TestContext.Current.CancellationToken);

        Assert.Equal(3, api.Calls);
    }

    [Fact]
    public async Task DeleteByDocumentId_ZeroProgressAtCap_Terminates()
    {
        // Defensive guard: a (nonsensical) full-cap pass with zero successes must terminate
        // instead of looping forever.
        var api = new FakeBatchDeleteApi(
            Response(limit: 2, matches: 2, successful: 0, failed: 0));
        using var store = CreateStore(api);

        await store.DeleteByDocumentIdAsync("doc-stuck", TestContext.Current.CancellationToken);

        Assert.Equal(1, api.Calls);
    }

    private static WeaviateVectorStore CreateStore(IWeaviateApi api) =>
        new(api, new WeaviateOptions
        {
            Endpoint = new Uri("http://localhost:8080"),
            ClassName = "TestClass",
        });

    private static WeaviateBatchDeleteResponse Response(long limit, long matches, long successful, long failed) =>
        new()
        {
            Results = new WeaviateBatchDeleteResults
            {
                Limit = limit,
                Matches = matches,
                Successful = successful,
                Failed = failed,
            },
        };
}

file sealed class FakeBatchDeleteApi : IWeaviateApi
{
    private readonly WeaviateBatchDeleteResponse[] _responses;

    public FakeBatchDeleteApi(params WeaviateBatchDeleteResponse[] responses)
    {
        _responses = responses;
    }

    public int Calls { get; private set; }

    public Task<Result<WeaviateBatchDeleteResponse, HttpError>> BatchDeleteAsync(
        WeaviateBatchDeleteRequest body,
        string? tenant = null,
        CancellationToken cancellationToken = default)
    {
        var response = _responses[Calls];
        Calls++;
        return Task.FromResult(Result<WeaviateBatchDeleteResponse, HttpError>.Success(response));
    }

    public Task<Result<WeaviateClass, HttpError>> CreateClassAsync(
        WeaviateClass body, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<WeaviateClass, HttpError>> GetClassAsync(
        string className, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteClassAsync(string className, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<IReadOnlyList<WeaviateTenant>, HttpError>> CreateTenantsAsync(
        string className, IReadOnlyList<WeaviateTenant> body, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<IReadOnlyList<WeaviateBatchObjectResult>, HttpError>> BatchObjectsAsync(
        WeaviateBatchObjectsRequest body, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<WeaviateGraphQlResponse, HttpError>> QueryAsync(
        WeaviateGraphQlRequest body, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
