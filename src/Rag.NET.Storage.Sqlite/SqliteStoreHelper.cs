using Microsoft.Data.Sqlite;

namespace Rag.NET.Storage;

/// <summary>
/// Shared SQLite plumbing for the stores in this assembly: opening a connection, and reading,
/// writing and creating the <c>rag_metadata</c> table that each store stamps its collection name
/// into.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>Rag</c> in <see cref="ReadRagMetadata"/>, <see cref="WriteRagMetadata"/> and
/// <see cref="EnsureRagMetadataTable"/> is load-bearing — do not "tidy" it away.</b> These are
/// extension methods on <see cref="SqliteConnection"/>, which is Microsoft's type, not ours. C#
/// resolves an instance method before it ever considers an extension method, so if
/// <c>Microsoft.Data.Sqlite</c> ever ships an instance <c>ReadMetadata</c>, every call site here
/// would silently rebind to it: no compiler error, no warning, different behaviour. The
/// <c>Rag</c> prefix makes that collision essentially impossible, and it names the table these
/// methods actually touch.
/// </para>
/// <para>
/// <see cref="OpenConnection"/> deliberately stays a plain static: its first parameter is a
/// <see cref="string"/> path, and it is a factory rather than an operation on a receiver.
/// </para>
/// </remarks>
internal static class SqliteStoreHelper
{
    /// <summary>Opens an unpooled connection to the SQLite database at <paramref name="dbPath"/>.</summary>
    internal static SqliteConnection OpenConnection(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        return conn;
    }

    /// <summary>Reads <paramref name="key"/> from <c>rag_metadata</c>, or <c>null</c> when absent.</summary>
    internal static string? ReadRagMetadata(this SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM rag_metadata WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Inserts or replaces <paramref name="key"/> in <c>rag_metadata</c>.</summary>
    internal static void WriteRagMetadata(this SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO rag_metadata (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Creates the <c>rag_metadata</c> key/value table when it does not already exist.</summary>
    internal static void EnsureRagMetadataTable(this SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rag_metadata (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
