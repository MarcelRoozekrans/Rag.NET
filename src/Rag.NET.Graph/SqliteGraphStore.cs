using Microsoft.Data.Sqlite;

namespace Rag.NET.Graph;

/// <summary>SQLite-backed implementation of <see cref="IGraphStore"/>.</summary>
public sealed class SqliteGraphStore : IGraphStore
{
    private readonly SqliteConnection _connection;

    public SqliteGraphStore(string connectionStringOrPath)
    {
        var connectionString = string.Equals(connectionStringOrPath, ":memory:", StringComparison.Ordinal)
            ? "Data Source=:memory:"
            : $"Data Source={connectionStringOrPath}";

        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        CreateTables();
    }

    private void CreateTables()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entities (
                name TEXT PRIMARY KEY COLLATE NOCASE,
                type TEXT NOT NULL,
                description TEXT NOT NULL,
                page_rank REAL DEFAULT 0,
                source_document_id TEXT,
                source_chunk_ids TEXT
            );

            CREATE TABLE IF NOT EXISTS relationships (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_entity TEXT NOT NULL,
                target_entity TEXT NOT NULL,
                description TEXT NOT NULL,
                weight REAL DEFAULT 1.0,
                source_document_id TEXT
            );

            CREATE TABLE IF NOT EXISTS communities (
                id INTEGER NOT NULL,
                level INTEGER NOT NULL,
                report_summary TEXT
            );

            CREATE TABLE IF NOT EXISTS community_members (
                community_id INTEGER NOT NULL,
                entity_name TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Task AddEntitiesAsync(IReadOnlyList<GraphEntity> entities, CancellationToken ct = default)
    {
        using var transaction = _connection.BeginTransaction();

        foreach (var entity in entities)
        {
            ct.ThrowIfCancellationRequested();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO entities (name, type, description, page_rank, source_document_id, source_chunk_ids)
                VALUES ($name, $type, $description, $pageRank, $sourceDocumentId, $sourceChunkIds)
                ON CONFLICT(name) DO UPDATE SET
                    type = $type,
                    description = entities.description || char(10) || $description,
                    page_rank = $pageRank,
                    source_document_id = COALESCE($sourceDocumentId, entities.source_document_id),
                    source_chunk_ids = COALESCE($sourceChunkIds, entities.source_chunk_ids)
                """;
            cmd.Parameters.Add(new SqliteParameter("$name", entity.Name));
            cmd.Parameters.Add(new SqliteParameter("$type", entity.Type));
            cmd.Parameters.Add(new SqliteParameter("$description", entity.Description));
            cmd.Parameters.Add(new SqliteParameter("$pageRank", SqliteType.Real) { Value = entity.PageRankScore });
            cmd.Parameters.Add(new SqliteParameter("$sourceDocumentId", entity.SourceDocumentId ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$sourceChunkIds", entity.SourceChunkIds.Count > 0
                ? string.Join(",", entity.SourceChunkIds)
                : (object)DBNull.Value));
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// An <c>UPDATE</c> of the one column, not an upsert: a name with no row is a name the caller
    /// computed a score for and the store never had, and inventing an entity with an empty
    /// description to hold a number would be worse than ignoring it.
    /// </remarks>
    public Task SetPageRankScoresAsync(
        IReadOnlyDictionary<string, double> scores, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scores);

        using var transaction = _connection.BeginTransaction();

        foreach (var (name, score) in scores)
        {
            ct.ThrowIfCancellationRequested();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE entities SET page_rank = $pageRank WHERE name = $name COLLATE NOCASE";
            cmd.Parameters.Add(new SqliteParameter("$pageRank", SqliteType.Real) { Value = score });
            cmd.Parameters.Add(new SqliteParameter("$name", name));
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task AddRelationshipsAsync(IReadOnlyList<GraphRelationship> relationships, CancellationToken ct = default)
    {
        using var transaction = _connection.BeginTransaction();

        foreach (var rel in relationships)
        {
            ct.ThrowIfCancellationRequested();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO relationships (source_entity, target_entity, description, weight, source_document_id)
                VALUES ($source, $target, $description, $weight, $sourceDocumentId)
                """;
            cmd.Parameters.Add(new SqliteParameter("$source", rel.SourceEntity));
            cmd.Parameters.Add(new SqliteParameter("$target", rel.TargetEntity));
            cmd.Parameters.Add(new SqliteParameter("$description", rel.Description));
            cmd.Parameters.Add(new SqliteParameter("$weight", SqliteType.Real) { Value = rel.Weight });
            cmd.Parameters.Add(new SqliteParameter("$sourceDocumentId", rel.SourceDocumentId ?? (object)DBNull.Value));
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphEntity>> GetNeighborsAsync(string entityName, int depth, CancellationToken ct = default)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entityName };
        var frontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entityName };
        var result = new List<GraphEntity>();

        for (int i = 0; i < depth; i++)
        {
            ct.ThrowIfCancellationRequested();
            var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in frontier)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    SELECT CASE WHEN source_entity = $name COLLATE NOCASE THEN target_entity ELSE source_entity END AS neighbor
                    FROM relationships
                    WHERE source_entity = $name COLLATE NOCASE OR target_entity = $name COLLATE NOCASE
                    """;
                cmd.Parameters.Add(new SqliteParameter("$name", node));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var neighbor = reader.GetString(0);
                    if (visited.Add(neighbor))
                    {
                        nextFrontier.Add(neighbor);
                    }
                }
            }

            foreach (var neighbor in nextFrontier)
            {
                var entity = LoadEntity(neighbor);
                if (entity is not null)
                {
                    result.Add(entity);
                }
            }

            frontier = nextFrontier;
        }

        return Task.FromResult<IReadOnlyList<GraphEntity>>(result);
    }

    public Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(string entityName, CancellationToken ct = default)
    {
        var result = new List<GraphRelationship>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT source_entity, target_entity, description, weight, source_document_id
            FROM relationships
            WHERE source_entity = $name COLLATE NOCASE OR target_entity = $name COLLATE NOCASE
            """;
        cmd.Parameters.Add(new SqliteParameter("$name", entityName));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new GraphRelationship(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3))
            {
                SourceDocumentId = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return Task.FromResult<IReadOnlyList<GraphRelationship>>(result);
    }

    public Task SetCommunitiesAsync(IReadOnlyList<Community> communities, CancellationToken ct = default)
    {
        using var transaction = _connection.BeginTransaction();

        using (var deleteMembers = _connection.CreateCommand())
        {
            deleteMembers.CommandText = "DELETE FROM community_members";
            deleteMembers.ExecuteNonQuery();
        }

        using (var deleteCommunities = _connection.CreateCommand())
        {
            deleteCommunities.CommandText = "DELETE FROM communities";
            deleteCommunities.ExecuteNonQuery();
        }

        foreach (var community in communities)
        {
            ct.ThrowIfCancellationRequested();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO communities (id, level, report_summary)
                VALUES ($id, $level, $reportSummary)
                """;
            cmd.Parameters.Add(new SqliteParameter("$id", SqliteType.Integer) { Value = community.Id });
            cmd.Parameters.Add(new SqliteParameter("$level", SqliteType.Integer) { Value = community.Level });
            cmd.Parameters.Add(new SqliteParameter("$reportSummary", community.ReportSummary ?? (object)DBNull.Value));
            cmd.ExecuteNonQuery();

            foreach (var member in community.MemberEntities)
            {
                using var memberCmd = _connection.CreateCommand();
                memberCmd.CommandText = """
                    INSERT INTO community_members (community_id, entity_name)
                    VALUES ($communityId, $entityName)
                    """;
                memberCmd.Parameters.Add(new SqliteParameter("$communityId", SqliteType.Integer) { Value = community.Id });
                memberCmd.Parameters.Add(new SqliteParameter("$entityName", member));
                memberCmd.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Community>> GetCommunitiesForEntityAsync(string entityName, CancellationToken ct = default)
    {
        var result = new List<Community>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.level, c.report_summary
            FROM communities c
            INNER JOIN community_members cm ON cm.community_id = c.id
            WHERE cm.entity_name = $name COLLATE NOCASE
            """;
        cmd.Parameters.Add(new SqliteParameter("$name", entityName));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var communityId = reader.GetInt32(0);
            var level = reader.GetInt32(1);
            var reportSummary = reader.IsDBNull(2) ? null : reader.GetString(2);
            var members = LoadCommunityMembers(communityId);
            result.Add(new Community(communityId, level, members, reportSummary));
        }

        return Task.FromResult<IReadOnlyList<Community>>(result);
    }

    public Task<GraphSnapshot> GetFullGraphAsync(CancellationToken ct = default)
    {
        var entities = new List<GraphEntity>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, type, description, page_rank, source_document_id, source_chunk_ids FROM entities";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entities.Add(ReadEntity(reader));
            }
        }

        var relationships = new List<GraphRelationship>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT source_entity, target_entity, description, weight, source_document_id FROM relationships";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                relationships.Add(new GraphRelationship(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDouble(3))
                {
                    SourceDocumentId = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        var communityMap = new Dictionary<int, (int Level, string? ReportSummary)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, level, report_summary FROM communities";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                communityMap[reader.GetInt32(0)] = (reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        var communities = new List<Community>();
        foreach (var (id, (level, reportSummary)) in communityMap)
        {
            var members = LoadCommunityMembers(id);
            communities.Add(new Community(id, level, members, reportSummary));
        }

        return Task.FromResult(new GraphSnapshot(entities, relationships, communities));
    }

    public Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default)
    {
        using var transaction = _connection.BeginTransaction();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM entities WHERE source_document_id = $docId";
            cmd.Parameters.Add(new SqliteParameter("$docId", documentId));
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM relationships WHERE source_document_id = $docId";
            cmd.Parameters.Add(new SqliteParameter("$docId", documentId));
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }

    private GraphEntity? LoadEntity(string name)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name, type, description, page_rank, source_document_id, source_chunk_ids FROM entities WHERE name = $name COLLATE NOCASE";
        cmd.Parameters.Add(new SqliteParameter("$name", name));

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEntity(reader) : null;
    }

    private static GraphEntity ReadEntity(SqliteDataReader reader)
    {
        var sourceChunkIds = reader.IsDBNull(5) || string.IsNullOrEmpty(reader.GetString(5))
            ? []
            : reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries);

        return new GraphEntity(reader.GetString(0), reader.GetString(1), reader.GetString(2))
        {
            PageRankScore = reader.GetDouble(3),
            SourceDocumentId = reader.IsDBNull(4) ? null : reader.GetString(4),
            SourceChunkIds = sourceChunkIds,
        };
    }

    private List<string> LoadCommunityMembers(int communityId)
    {
        var members = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT entity_name FROM community_members WHERE community_id = $id";
        cmd.Parameters.Add(new SqliteParameter("$id", SqliteType.Integer) { Value = communityId });

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            members.Add(reader.GetString(0));
        }

        return members;
    }
}
