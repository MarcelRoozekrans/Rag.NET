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
    public async Task TouchAsync_RecordsTheCheck_WithoutDisturbingTheRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new SqliteContentHashStore(_dbPath);
        await sut.SetAsync(new ProviderId("p"), new EntryId("e"), etag: "etag-1", hash: "hash-1", ct);

        Assert.Null(await sut.GetCheckedAtAsync(new ProviderId("p"), new EntryId("e"), ct));

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await sut.TouchAsync(new ProviderId("p"), new EntryId("e"), ct);
        var checkedAt = await sut.GetCheckedAtAsync(new ProviderId("p"), new EntryId("e"), ct);

        Assert.NotNull(checkedAt);
        Assert.InRange(checkedAt!.Value, before, DateTimeOffset.UtcNow.AddSeconds(1));

        // The row's content did not change, and TouchAsync must not pretend otherwise.
        Assert.Equal("etag-1", await sut.GetETagAsync(new ProviderId("p"), new EntryId("e"), ct));
        Assert.Equal("hash-1", await sut.GetHashAsync(new ProviderId("p"), new EntryId("e"), ct));
    }

    /// <summary>An entry the store has never seen records nothing rather than being inserted.</summary>
    /// <remarks>
    /// A check of something never ingested is not evidence about that entry, and an inserted row
    /// with a null hash would be indistinguishable from a real one to <c>GetAllIdsAsync</c>, which
    /// drives cleanup. <c>UPDATE</c> matching no row is the correct no-op.
    /// </remarks>
    [Fact]
    public async Task TouchAsync_UnknownEntry_InsertsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new SqliteContentHashStore(_dbPath);

        await sut.TouchAsync(new ProviderId("p"), new EntryId("never-seen"), ct);

        Assert.Empty(await sut.GetAllIdsAsync(new ProviderId("p"), ct));
        Assert.Null(await sut.GetCheckedAtAsync(new ProviderId("p"), new EntryId("never-seen"), ct));
    }

    /// <summary>A database written before <c>checked_at</c> existed gains the column.</summary>
    /// <remarks>
    /// <c>CREATE TABLE IF NOT EXISTS</c> is a no-op on an existing database, so without the
    /// <c>ALTER TABLE</c> migration every <c>TouchAsync</c> against a store from an earlier
    /// version would fail on a missing column — at ingestion time, on a user's machine, not here.
    /// This builds the old schema by hand rather than trusting that path to be exercised.
    /// </remarks>
    [Fact]
    public async Task OpeningAPreMigrationDatabase_AddsCheckedAt_AndPreservesRows()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE content_hashes (
                    provider_id TEXT NOT NULL,
                    entry_id    TEXT NOT NULL,
                    etag        TEXT,
                    hash        TEXT NOT NULL,
                    updated_at  TEXT NOT NULL,
                    PRIMARY KEY (provider_id, entry_id)
                );
                INSERT INTO content_hashes VALUES ('p', 'e', 'etag-old', 'hash-old', '2026-01-01T00:00:00.0000000Z');
                """;
            await cmd.ExecuteNonQueryAsync(ct);

            // Microsoft.Data.Sqlite pools connections, so closing this one leaves a handle on the
            // file and the fixture's Dispose cannot delete it. Clearing the pool is what actually
            // releases it.
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }

        var sut = new SqliteContentHashStore(_dbPath);

        // The pre-existing row survives the migration untouched...
        Assert.Equal("hash-old", await sut.GetHashAsync(new ProviderId("p"), new EntryId("e"), ct));
        Assert.Equal("etag-old", await sut.GetETagAsync(new ProviderId("p"), new EntryId("e"), ct));

        // ...and has never been checked, which is the truth rather than an invented timestamp.
        Assert.Null(await sut.GetCheckedAtAsync(new ProviderId("p"), new EntryId("e"), ct));

        await sut.TouchAsync(new ProviderId("p"), new EntryId("e"), ct);
        Assert.NotNull(await sut.GetCheckedAtAsync(new ProviderId("p"), new EntryId("e"), ct));
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
