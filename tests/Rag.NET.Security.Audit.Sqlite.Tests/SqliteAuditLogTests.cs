using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

/// <summary>
/// Integration-style unit tests for <see cref="SqliteAuditLog"/> that use real SQLite databases.
/// Each test uses a unique temp-file path; the connection pool is cleared in Dispose so the
/// file lock is released before deletion (Windows requires this).
/// </summary>
public sealed class SqliteAuditLogTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteAuditLogTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rag-audit-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        // Release all pooled connections so the file is no longer locked on Windows.
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private SqliteAuditLog CreateSut() =>
        new(new AuditLogOptions { DatabasePath = _dbPath });

    private static AuditRetrievalEvent MakeRetrievalEvent(string? query = "test query") =>
        new()
        {
            RequestId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            CallerRoles = ["hr", "user"],
            Chunks =
            [
                new AuditChunkRef { DocumentId = "doc1", ChunkIndex = 0, Score = 0.9 }
            ],
            Query = query,
        };

    private static AuditAnswerEvent MakeAnswerEvent(string? answer = "Some answer") =>
        new()
        {
            RequestId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            Answer = answer,
        };

    private long CountRows(string table)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task LogRetrievalAsync_WritesRowToDatabase()
    {
        await using var sut = CreateSut();
        var ev = MakeRetrievalEvent();

        await sut.LogRetrievalAsync(ev, TestContext.Current.CancellationToken);

        Assert.Equal(1L, CountRows("retrieval_events"));
    }

    [Fact]
    public async Task LogAnswerAsync_WritesRowToDatabase()
    {
        await using var sut = CreateSut();
        var ev = MakeAnswerEvent();

        await sut.LogAnswerAsync(ev, TestContext.Current.CancellationToken);

        Assert.Equal(1L, CountRows("answer_events"));
    }

    [Fact]
    public async Task LogRetrievalAsync_TablesCreatedLazily()
    {
        // DB file does not exist before the first write; tables should be created on first call
        Assert.False(File.Exists(_dbPath));
        await using var sut = CreateSut();

        await sut.LogRetrievalAsync(MakeRetrievalEvent(), TestContext.Current.CancellationToken);

        // If tables were not created the COUNT queries would throw
        Assert.True(CountRows("retrieval_events") >= 0);
        Assert.True(CountRows("answer_events") >= 0);
    }

    [Fact]
    public async Task LogRetrievalAsync_CallerRolesSerializedAsJson()
    {
        await using var sut = CreateSut();
        var ev = MakeRetrievalEvent();

        await sut.LogRetrievalAsync(ev, TestContext.Current.CancellationToken);

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT caller_roles FROM retrieval_events LIMIT 1";
        var raw = (string)cmd.ExecuteScalar()!;

        // Must be a valid JSON array containing exactly the stored roles
        var parsed = JsonSerializer.Deserialize<string[]>(raw);
        Assert.NotNull(parsed);
        Assert.Equal(ev.CallerRoles.Count, parsed.Length);
        Assert.Equal(ev.CallerRoles, parsed);
    }

    [Fact]
    public async Task LogRetrievalAsync_NullQuery_StoredAsNull()
    {
        await using var sut = CreateSut();
        var ev = MakeRetrievalEvent(query: null);

        await sut.LogRetrievalAsync(ev, TestContext.Current.CancellationToken);

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT query FROM retrieval_events LIMIT 1";
        var value = cmd.ExecuteScalar();

        // Should be DBNull, not the string "null"
        Assert.Equal(DBNull.Value, value);
    }

    [Fact]
    public async Task LogRetrievalAsync_CancelledToken_ThrowsOce()
    {
        await using var sut = CreateSut();
        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.LogRetrievalAsync(MakeRetrievalEvent(), cancelled).AsTask());
    }

    [Fact]
    public async Task LogRetrievalAsync_CorruptDatabase_SwallowsException()
    {
        // Use an invalid path that cannot be opened as a SQLite database
        var sut = new SqliteAuditLog(new AuditLogOptions { DatabasePath = "/\0/invalid" });
        await using (sut.ConfigureAwait(false))
        {
            // Must not throw — errors are swallowed and logged internally
            var exception = await Record.ExceptionAsync(
                () => sut.LogRetrievalAsync(MakeRetrievalEvent(), TestContext.Current.CancellationToken).AsTask());

            Assert.Null(exception);
        }
    }
}
