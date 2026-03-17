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
        await sut.SetAsync("prov-1", "entry-1", etag: null, hash: "abc123", TestContext.Current.CancellationToken);
        var result = await sut.GetHashAsync("prov-1", "entry-1", TestContext.Current.CancellationToken);
        Assert.Equal("abc123", result);
    }

    [Fact]
    public async Task SetAsync_WithETag_GetETagReturnsIt()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync("prov-1", "entry-1", etag: "etag-xyz", hash: "abc123", TestContext.Current.CancellationToken);
        var result = await sut.GetETagAsync("prov-1", "entry-1", TestContext.Current.CancellationToken);
        Assert.Equal("etag-xyz", result);
    }

    [Fact]
    public async Task GetHashAsync_UnknownEntry_ReturnsNull()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        var result = await sut.GetHashAsync("prov-1", "missing", TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllIdsAsync_ReturnsScopedIds()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync("prov-1", "a", null, "h1", TestContext.Current.CancellationToken);
        await sut.SetAsync("prov-1", "b", null, "h2", TestContext.Current.CancellationToken);
        await sut.SetAsync("prov-2", "c", null, "h3", TestContext.Current.CancellationToken);

        var ids = await sut.GetAllIdsAsync("prov-1", TestContext.Current.CancellationToken);

        Assert.Equal(2, ids.Count);
        Assert.Contains("a", ids);
        Assert.Contains("b", ids);
        Assert.DoesNotContain("c", ids);
    }

    [Fact]
    public async Task RemoveAsync_EntryGone_GetHashReturnsNull()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync("prov-1", "entry-1", null, "abc123", TestContext.Current.CancellationToken);
        await sut.RemoveAsync("prov-1", "entry-1", TestContext.Current.CancellationToken);
        var result = await sut.GetHashAsync("prov-1", "entry-1", TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_UpdatesExistingRow()
    {
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync("prov-1", "entry-1", null, "hash-v1", TestContext.Current.CancellationToken);
        await sut.SetAsync("prov-1", "entry-1", null, "hash-v2", TestContext.Current.CancellationToken);
        var result = await sut.GetHashAsync("prov-1", "entry-1", TestContext.Current.CancellationToken);
        Assert.Equal("hash-v2", result);
    }

    [Fact]
    public async Task SurvivesRestart_DataPersistedToSqlite()
    {
        var sut1 = new SqliteContentHashStore(_dbPath);
        await sut1.SetAsync("prov-1", "entry-1", "etag-1", "hash-1", TestContext.Current.CancellationToken);

        // Simulate restart — new instance, same db file
        var sut2 = new SqliteContentHashStore(_dbPath);
        Assert.Equal("hash-1", await sut2.GetHashAsync("prov-1", "entry-1", TestContext.Current.CancellationToken));
        Assert.Equal("etag-1", await sut2.GetETagAsync("prov-1", "entry-1", TestContext.Current.CancellationToken));
    }
}
