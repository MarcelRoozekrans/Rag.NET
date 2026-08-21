using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Rag.NET.Raptor.Store;

/// <summary>SQLite-backed implementation of <see cref="IRaptorLeafStore"/>.</summary>
public sealed class SqliteRaptorLeafStore : IRaptorLeafStore
{
    private readonly SqliteConnection _connection;

    /// <summary>Opens or creates the backing database.</summary>
    /// <param name="connectionStringOrPath">A file path, or <c>:memory:</c>.</param>
    public SqliteRaptorLeafStore(string connectionStringOrPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionStringOrPath);

        var connectionString = string.Equals(connectionStringOrPath, ":memory:", StringComparison.Ordinal)
            ? "Data Source=:memory:"
            : $"Data Source={connectionStringOrPath}";

        _connection = new SqliteConnection(connectionString);
        _connection.Open();
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS leaves (
                document_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                text TEXT NOT NULL,
                embedding BLOB NOT NULL,
                PRIMARY KEY (document_id, chunk_index)
            );
            """;
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddLeavesAsync(IReadOnlyList<RaptorLeaf> leaves, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        if (leaves.Count == 0)
        {
            return Task.CompletedTask;
        }

        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO leaves (document_id, chunk_index, text, embedding)
            VALUES ($doc, $idx, $text, $emb)
            ON CONFLICT(document_id, chunk_index)
            DO UPDATE SET text = excluded.text, embedding = excluded.embedding;
            """;

        var doc = cmd.CreateParameter();
        doc.ParameterName = "$doc";
        cmd.Parameters.Add(doc);
        var idx = cmd.CreateParameter();
        idx.ParameterName = "$idx";
        cmd.Parameters.Add(idx);
        var text = cmd.CreateParameter();
        text.ParameterName = "$text";
        cmd.Parameters.Add(text);
        var emb = cmd.CreateParameter();
        emb.ParameterName = "$emb";
        cmd.Parameters.Add(emb);

        foreach (var leaf in leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            doc.Value = leaf.DocumentId;
            idx.Value = leaf.ChunkIndex;
            text.Value = leaf.Text;
            emb.Value = ToBlob(leaf.Embedding);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RaptorLeaf>> GetAllLeavesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RaptorLeaf>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT document_id, chunk_index, text, embedding FROM leaves;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new RaptorLeaf(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                FromBlob((byte[])reader[3])));
        }

        return Task.FromResult<IReadOnlyList<RaptorLeaf>>(results);
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM leaves;";
        var scalar = cmd.ExecuteScalar();
        return Task.FromResult(Convert.ToInt32(scalar, CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM leaves WHERE document_id = $doc;";
        var doc = cmd.CreateParameter();
        doc.ParameterName = "$doc";
        doc.Value = documentId;
        cmd.Parameters.Add(doc);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBlob(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }
}
