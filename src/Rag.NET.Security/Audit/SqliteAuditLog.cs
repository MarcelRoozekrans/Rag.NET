using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rag.NET.Security;

/// <summary>
/// Persists audit events to a SQLite database. Writes are fire-and-forget (errors logged, never thrown).
/// Tables are created lazily on first write.
/// </summary>
public sealed partial class SqliteAuditLog : IAuditLog, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteAuditLog> _logger;
    private bool _initialised;

    public SqliteAuditLog(AuditLogOptions options, ILogger<SqliteAuditLog>? logger = null)
    {
        _connectionString = $"Data Source={options.DatabasePath}";
        _logger = logger ?? NullLogger<SqliteAuditLog>.Instance;
    }

    public async ValueTask LogRetrievalAsync(AuditRetrievalEvent ev, CancellationToken ct = default)
    {
        try
        {
            var conn = new SqliteConnection(_connectionString);
            await using (conn.ConfigureAwait(false))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await EnsureTablesAsync(conn, ct).ConfigureAwait(false);
                var cmd = conn.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
                    cmd.CommandText =
                        "INSERT INTO retrieval_events (request_id, timestamp, caller_roles, chunks, query) " +
                        "VALUES ($rid, $ts, $roles, $chunks, $query)";
                    cmd.Parameters.AddWithValue("$rid", ev.RequestId);
                    cmd.Parameters.AddWithValue("$ts", ev.Timestamp.ToString("O"));
                    cmd.Parameters.AddWithValue("$roles", JsonSerializer.Serialize(ev.CallerRoles));
                    cmd.Parameters.AddWithValue("$chunks", JsonSerializer.Serialize(ev.Chunks));
                    cmd.Parameters.AddWithValue("$query", (object?)ev.Query ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogWriteFailed(_logger, ex); }
    }

    public async ValueTask LogAnswerAsync(AuditAnswerEvent ev, CancellationToken ct = default)
    {
        try
        {
            var conn = new SqliteConnection(_connectionString);
            await using (conn.ConfigureAwait(false))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await EnsureTablesAsync(conn, ct).ConfigureAwait(false);
                var cmd = conn.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
                    cmd.CommandText =
                        "INSERT INTO answer_events (request_id, timestamp, answer) " +
                        "VALUES ($rid, $ts, $answer)";
                    cmd.Parameters.AddWithValue("$rid", ev.RequestId);
                    cmd.Parameters.AddWithValue("$ts", ev.Timestamp.ToString("O"));
                    cmd.Parameters.AddWithValue("$answer", (object?)ev.Answer ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogWriteFailed(_logger, ex); }
    }

    private async ValueTask EnsureTablesAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (_initialised) return;
        var cmd = conn.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS retrieval_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    request_id TEXT NOT NULL,
                    timestamp  TEXT NOT NULL,
                    caller_roles TEXT NOT NULL,
                    chunks     TEXT NOT NULL,
                    query      TEXT
                );
                CREATE TABLE IF NOT EXISTS answer_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    request_id TEXT NOT NULL,
                    timestamp  TEXT NOT NULL,
                    answer     TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        _initialised = true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [LoggerMessage(EventId = 1577581389, EventName = "log_write_failed", Level = LogLevel.Warning, Message = "SqliteAuditLog failed to write audit event.")]
    private static partial void LogWriteFailed(ILogger logger, Exception ex);
}
