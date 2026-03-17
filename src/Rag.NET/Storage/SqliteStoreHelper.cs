using Microsoft.Data.Sqlite;

namespace Rag.NET.Storage;

internal static class SqliteStoreHelper
{
    internal static SqliteConnection OpenConnection(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        return conn;
    }

    internal static string? ReadMetadata(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM rag_metadata WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    internal static void WriteMetadata(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO rag_metadata (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    internal static void EnsureMetadataTable(SqliteConnection conn)
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
