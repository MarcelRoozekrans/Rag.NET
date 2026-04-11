using Rag.NET.Models;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public sealed class SqliteContentHashStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-hash-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task SetAsync_ThenGetHash_ReturnsStoredHash()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync(new ProviderId("prov-1"), new EntryId("entry-1"), etag: null, hash: "abc123", TestContext.Current.CancellationToken);
        var result = await sut.GetHashAsync(new ProviderId("prov-1"), new EntryId("entry-1"), TestContext.Current.CancellationToken);
        Assert.Equal("abc123", result);
    }

    [Fact]
    public async Task SetAsync_WithETag_GetETagReturnsIt()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync(new ProviderId("prov-1"), new EntryId("entry-1"), etag: "etag-xyz", hash: "abc123", TestContext.Current.CancellationToken);
        var result = await sut.GetETagAsync(new ProviderId("prov-1"), new EntryId("entry-1"), TestContext.Current.CancellationToken);
        Assert.Equal("etag-xyz", result);
    }

    [Fact]
    public async Task GetHashAsync_UnknownEntry_ReturnsNull()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        var result = await sut.GetHashAsync(new ProviderId("prov-1"), new EntryId("missing"), TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetETagAsync_UnknownEntry_ReturnsNull()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        var result = await sut.GetETagAsync(new ProviderId("prov-1"), new EntryId("missing"), TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllIdsAsync_ReturnsScopedIds()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync(new ProviderId("prov-1"), new EntryId("a"), null, "h1", TestContext.Current.CancellationToken);
        await sut.SetAsync(new ProviderId("prov-1"), new EntryId("b"), null, "h2", TestContext.Current.CancellationToken);
        await sut.SetAsync(new ProviderId("prov-2"), new EntryId("c"), null, "h3", TestContext.Current.CancellationToken);

        var ids = await sut.GetAllIdsAsync(new ProviderId("prov-1"), TestContext.Current.CancellationToken);

        Assert.Equal(2, ids.Count);
        Assert.Contains(new EntryId("a"), ids);
        Assert.Contains(new EntryId("b"), ids);
        Assert.DoesNotContain(new EntryId("c"), ids);
    }

    [Fact]
    public async Task RemoveAsync_EntryGone_GetHashReturnsNull()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync(new ProviderId("prov-1"), new EntryId("entry-1"), null, "abc123", TestContext.Current.CancellationToken);
        await sut.RemoveAsync(new ProviderId("prov-1"), new EntryId("entry-1"), TestContext.Current.CancellationToken);
        var result = await sut.GetHashAsync(new ProviderId("prov-1"), new EntryId("entry-1"), TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_UpdatesExistingRow()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync(new ProviderId("prov-1"), new EntryId("entry-1"), null, "hash-v1", TestContext.Current.CancellationToken);
        await sut.SetAsync(new ProviderId("prov-1"), new EntryId("entry-1"), null, "hash-v2", TestContext.Current.CancellationToken);
        var result = await sut.GetHashAsync(new ProviderId("prov-1"), new EntryId("entry-1"), TestContext.Current.CancellationToken);
        Assert.Equal("hash-v2", result);
    }

    [Fact]
    public async Task SurvivesRestart_DataPersistedToSqlite()
    {
        var sut1 = new SqliteContentHashStore(_dbPath);
        await sut1.SetAsync(new ProviderId("prov-1"), new EntryId("entry-1"), "etag-1", "hash-1", TestContext.Current.CancellationToken);

        // Simulate restart — new instance, same db file
        var sut2 = new SqliteContentHashStore(_dbPath);
        Assert.Equal("hash-1", await sut2.GetHashAsync(new ProviderId("prov-1"), new EntryId("entry-1"), TestContext.Current.CancellationToken));
        Assert.Equal("etag-1", await sut2.GetETagAsync(new ProviderId("prov-1"), new EntryId("entry-1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetHashAsync_SameEntryId_DifferentProviders_AreIndependent()
    {
        var sut = new SqliteContentHashStore(_dbPath);

        // Same entry ID, two different providers, two different hashes
        await sut.SetAsync(new ProviderId("prov-1"), new EntryId("shared-entry"), null, "hash-from-prov1", TestContext.Current.CancellationToken);
        await sut.SetAsync(new ProviderId("prov-2"), new EntryId("shared-entry"), null, "hash-from-prov2", TestContext.Current.CancellationToken);

        var hash1 = await sut.GetHashAsync(new ProviderId("prov-1"), new EntryId("shared-entry"), TestContext.Current.CancellationToken);
        var hash2 = await sut.GetHashAsync(new ProviderId("prov-2"), new EntryId("shared-entry"), TestContext.Current.CancellationToken);

        Assert.Equal("hash-from-prov1", hash1);
        Assert.Equal("hash-from-prov2", hash2);
    }
}
