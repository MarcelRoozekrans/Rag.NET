using System.Globalization;
using System.Runtime.InteropServices;
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
        Migrate();
    }

    private void CreateTables()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entities (
                name TEXT PRIMARY KEY,
                display_name TEXT,
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
                source_display TEXT,
                target_display TEXT,
                description TEXT NOT NULL,
                weight REAL DEFAULT 1.0,
                source_document_id TEXT,
                source_chunk_ids TEXT
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

            -- #297. Without these, every neighbour lookup is a full scan of `relationships` --
            -- ~45,000 scans of 147,021 rows over one MultiHop-RAG query pass.
            --
            -- Plain (BINARY) indexes, which only became usable once the queries stopped saying
            -- COLLATE NOCASE. While they did, an index like this was IGNORED: EXPLAIN QUERY PLAN
            -- reported SCAN with it present, because an index's collation must match the
            -- predicate's. Adding COLLATE NOCASE to the index instead would have worked and would
            -- have cemented ASCII-only folding into the schema, which is #299's correctness bug.
            CREATE INDEX IF NOT EXISTS ix_relationships_source ON relationships(source_entity);
            CREATE INDEX IF NOT EXISTS ix_relationships_target ON relationships(target_entity);
            CREATE INDEX IF NOT EXISTS ix_community_members_entity ON community_members(entity_name);
            CREATE INDEX IF NOT EXISTS ix_community_members_community ON community_members(community_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reduces an entity name to the key the store compares and joins on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Case folding happens here, in .NET, and no longer in SQL (#299).</b> The schema used
    /// <c>COLLATE NOCASE</c>, which folds <c>A</c>-<c>Z</c> and nothing else, while the callers key
    /// their dictionaries and visited-sets on <see cref="StringComparer.OrdinalIgnoreCase"/>, which
    /// folds Unicode. The two disagreed on every non-ASCII name: <c>Ångström</c> and <c>ångström</c>
    /// were one entity in memory and two rows in the graph, so merging half-worked on any corpus
    /// that was not English.
    /// </para>
    /// <para>
    /// <b><see cref="string.ToUpperInvariant"/> rather than <c>ToLowerInvariant</c>.</b> Upper-casing
    /// is the documented choice for canonical comparison keys — lower-casing loses distinctions in
    /// some scripts and does not round-trip — and it is the direction
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> itself uses, which is the comparer this has to
    /// agree with. Invariant, so the Turkish dotless-i does not make the key culture-dependent.
    /// </para>
    /// <para>
    /// The original casing is not lost: it is kept in <c>display_name</c> and returned to callers, so
    /// reports and prompts still read <c>Angela Merkel</c> rather than a folded key.
    /// </para>
    /// </remarks>
    /// <param name="name">The entity name as the caller wrote it.</param>
    /// <returns>The comparison key.</returns>
    private static string Fold(string name) => name.ToUpperInvariant();

    /// <summary>
    /// Brings a database written before #299 up to the current schema and key convention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things changed and both are invisible to <c>CREATE TABLE IF NOT EXISTS</c>, which does
    /// nothing to a table that already exists: the display columns are new, and entity keys are now
    /// folded by <see cref="Fold"/> rather than compared with <c>COLLATE NOCASE</c>. An untouched old
    /// file would fail on the first query mentioning <c>display_name</c>, and — worse — its unfolded
    /// keys would silently never match a folded lookup.
    /// </para>
    /// <para>
    /// <b>Folding can collide, and the collision is the bug being fixed.</b> A file written before
    /// this may hold <c>Ångström</c> and <c>ångström</c> as two rows, because <c>NOCASE</c> did not
    /// fold them. Folding makes them one key, so the rows are merged the same way
    /// <see cref="AddEntitiesAsync"/>'s <c>ON CONFLICT</c> merges them — descriptions joined, first
    /// spelling kept for display — rather than one silently overwriting the other.
    /// </para>
    /// <para>
    /// Cheap on the common path: an in-memory store and any file already written by this version
    /// need no row rewriting, and the check is one pass over the names.
    /// </para>
    /// </remarks>
    private void Migrate()
    {
        EnsureColumn("entities", "display_name");
        EnsureColumn("relationships", "source_display");
        EnsureColumn("relationships", "target_display");
        EnsureColumn("relationships", "source_chunk_ids");
        FoldExistingKeys();
    }

    /// <summary>Adds a nullable TEXT column when the table does not already have it.</summary>
    /// <param name="table">Table to inspect.</param>
    /// <param name="column">Column that must exist.</param>
    private void EnsureColumn(string table, string column)
    {
        using var check = _connection.CreateCommand();
        // Table and column names are literals from this file, never caller input.
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT";
        alter.ExecuteNonQuery();
    }

    /// <summary>Rewrites any keys that predate <see cref="Fold"/>, merging rows that now collide.</summary>
    private void FoldExistingKeys()
    {
        var entities = ReadEntitiesForMigration(out var needsFolding);
        if (!needsFolding)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();

        using (var clear = _connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM entities";
            clear.ExecuteNonQuery();
        }

        // Re-inserted through the normal upsert, so colliding rows merge by the same rule new writes
        // use rather than by a second, migration-only definition of "merge".
        foreach (ref readonly var entity in CollectionsMarshal.AsSpan(entities))
        {
            UpsertEntity(entity);
        }

        using (var rels = _connection.CreateCommand())
        {
            rels.CommandText = """
                UPDATE relationships
                SET source_display = COALESCE(source_display, source_entity),
                    target_display = COALESCE(target_display, target_entity)
                """;
            rels.ExecuteNonQuery();
        }

        FoldColumn("relationships", "source_entity");
        FoldColumn("relationships", "target_entity");
        FoldColumn("community_members", "entity_name");

        transaction.Commit();
    }

    /// <summary>Reads every entity, reporting whether any key predates <see cref="Fold"/>.</summary>
    /// <param name="needsFolding">Set when at least one stored key is not already folded.</param>
    /// <returns>The entities, carrying their display spelling as their name.</returns>
    private List<GraphEntity> ReadEntitiesForMigration(out bool needsFolding)
    {
        var entities = new List<GraphEntity>();
        needsFolding = false;

        using var read = _connection.CreateCommand();
        read.CommandText =
            "SELECT name, COALESCE(display_name, name), type, description, page_rank, source_document_id, source_chunk_ids FROM entities";
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var stored = reader.GetString(0);
            if (!string.Equals(stored, Fold(stored), StringComparison.Ordinal))
            {
                needsFolding = true;
            }

            entities.Add(new GraphEntity(reader.GetString(1), reader.GetString(2), reader.GetString(3))
            {
                PageRankScore = reader.GetDouble(4),
                SourceDocumentId = reader.IsDBNull(5) ? null : reader.GetString(5),
                SourceChunkIds = reader.IsDBNull(6) || string.IsNullOrEmpty(reader.GetString(6))
                    ? []
                    : reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries),
            });
        }

        return entities;
    }

    /// <summary>Rewrites one text column through <see cref="Fold"/>, row by row.</summary>
    /// <remarks>
    /// In .NET rather than SQL because SQLite's <c>upper()</c> folds ASCII only — the very limitation
    /// this migration exists to escape, and an easy way to write a migration that does nothing.
    /// </remarks>
    /// <param name="table">Table to rewrite.</param>
    /// <param name="column">Column holding entity names.</param>
    private void FoldColumn(string table, string column)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        using (var read = _connection.CreateCommand())
        {
            read.CommandText = $"SELECT DISTINCT {column} FROM {table}";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                _ = values.Add(reader.GetString(0));
            }
        }

        foreach (var value in values)
        {
            var folded = Fold(value);
            if (string.Equals(value, folded, StringComparison.Ordinal))
            {
                continue;
            }

            using var update = _connection.CreateCommand();
            update.CommandText = $"UPDATE {table} SET {column} = $folded WHERE {column} = $original";
            update.Parameters.Add(new SqliteParameter("$folded", folded));
            update.Parameters.Add(new SqliteParameter("$original", value));
            update.ExecuteNonQuery();
        }
    }

    public Task AddEntitiesAsync(IReadOnlyList<GraphEntity> entities, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        using var transaction = _connection.BeginTransaction();

        foreach (var entity in entities)
        {
            ct.ThrowIfCancellationRequested();
            UpsertEntity(entity);
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    /// <summary>Inserts or merges one entity, inside whatever transaction the caller holds.</summary>
    /// <remarks>
    /// Extracted so the migration in <see cref="FoldExistingKeys"/> re-inserts through exactly this
    /// rule rather than a second, migration-only definition of "merge" that could drift from it.
    /// Opens no transaction of its own for the same reason: both callers already have one.
    /// </remarks>
    /// <param name="entity">The entity to write.</param>
    private void UpsertEntity(GraphEntity entity)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entities (name, display_name, type, description, page_rank, source_document_id, source_chunk_ids)
            VALUES ($name, $displayName, $type, $description, $pageRank, $sourceDocumentId, $sourceChunkIds)
            ON CONFLICT(name) DO UPDATE SET
                display_name = COALESCE(entities.display_name, $displayName),
                type = $type,
                description = entities.description || char(10) || $description,
                page_rank = $pageRank,
                source_document_id = COALESCE($sourceDocumentId, entities.source_document_id),
                source_chunk_ids = COALESCE($sourceChunkIds, entities.source_chunk_ids)
            """;

        // The key is folded; the caller's spelling is kept for display. First spelling wins on merge
        // (COALESCE above), so an entity does not change how it reads because a later document
        // happened to shout its name.
        cmd.Parameters.Add(new SqliteParameter("$name", Fold(entity.Name)));
        cmd.Parameters.Add(new SqliteParameter("$displayName", entity.Name));
        cmd.Parameters.Add(new SqliteParameter("$type", entity.Type));
        cmd.Parameters.Add(new SqliteParameter("$description", entity.Description));
        cmd.Parameters.Add(new SqliteParameter("$pageRank", SqliteType.Real) { Value = entity.PageRankScore });
        cmd.Parameters.Add(new SqliteParameter("$sourceDocumentId", entity.SourceDocumentId ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("$sourceChunkIds", entity.SourceChunkIds.Count > 0
            ? string.Join(",", entity.SourceChunkIds)
            : (object)DBNull.Value));
        cmd.ExecuteNonQuery();
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
            cmd.CommandText = "UPDATE entities SET page_rank = $pageRank WHERE name = $name";
            cmd.Parameters.Add(new SqliteParameter("$pageRank", SqliteType.Real) { Value = score });
            cmd.Parameters.Add(new SqliteParameter("$name", Fold(name)));
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
                INSERT INTO relationships (source_entity, target_entity, source_display, target_display, description, weight, source_document_id, source_chunk_ids)
                VALUES ($source, $target, $sourceDisplay, $targetDisplay, $description, $weight, $sourceDocumentId, $sourceChunkIds)
                """;
            // Folded, because these are joined against entities.name and walked by
            // GetNeighborsAsync. An unfolded endpoint is an edge to an entity that cannot be found.
            //
            // The caller's spelling is kept alongside, per endpoint, because reads must not hand
            // back the folded key — an existing test caught exactly that, expecting "Microsoft" and
            // receiving "MICROSOFT".
            //
            // Resolving the spelling by joining to entities.display_name was tried first and does
            // not work: an endpoint need not be an entity. In that same test "GraphRAG" appears only
            // as a relationship target and has no entities row, so the join found nothing and fell
            // back to the folded key. The relationship has to carry its own.
            cmd.Parameters.Add(new SqliteParameter("$source", Fold(rel.SourceEntity)));
            cmd.Parameters.Add(new SqliteParameter("$target", Fold(rel.TargetEntity)));
            cmd.Parameters.Add(new SqliteParameter("$sourceDisplay", rel.SourceEntity));
            cmd.Parameters.Add(new SqliteParameter("$targetDisplay", rel.TargetEntity));
            cmd.Parameters.Add(new SqliteParameter("$description", rel.Description));
            cmd.Parameters.Add(new SqliteParameter("$weight", SqliteType.Real) { Value = rel.Weight });
            cmd.Parameters.Add(new SqliteParameter("$sourceDocumentId", rel.SourceDocumentId ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$sourceChunkIds", rel.SourceChunkIds.Count > 0
                ? string.Join(",", rel.SourceChunkIds)
                : (object)DBNull.Value));
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
                    SELECT CASE WHEN source_entity = $name THEN target_entity ELSE source_entity END AS neighbor
                    FROM relationships
                    WHERE source_entity = $name OR target_entity = $name
                    """;
                cmd.Parameters.Add(new SqliteParameter("$name", Fold(node)));

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
            SELECT COALESCE(source_display, source_entity),
                   COALESCE(target_display, target_entity),
                   description, weight, source_document_id, source_chunk_ids
            FROM relationships
            WHERE source_entity = $name OR target_entity = $name
            """;
        cmd.Parameters.Add(new SqliteParameter("$name", Fold(entityName)));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadRelationship(reader));
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
                memberCmd.Parameters.Add(new SqliteParameter("$entityName", Fold(member)));
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
            WHERE cm.entity_name = $name
            """;
        cmd.Parameters.Add(new SqliteParameter("$name", Fold(entityName)));

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
            cmd.CommandText = "SELECT COALESCE(display_name, name), type, description, page_rank, source_document_id, source_chunk_ids FROM entities";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entities.Add(ReadEntity(reader));
            }
        }

        var relationships = new List<GraphRelationship>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COALESCE(source_display, source_entity),
                       COALESCE(target_display, target_entity),
                       description, weight, source_document_id, source_chunk_ids
                FROM relationships
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                relationships.Add(ReadRelationship(reader));
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

    /// <inheritdoc/>
    /// <remarks>
    /// One query with an <c>IN</c> list over the folded names, rather than the interface default's
    /// whole-graph read. The names are folded here because that is what the primary key holds; the
    /// rows come back with their display spelling, as every other read does.
    /// </remarks>
    public Task<IReadOnlyList<GraphEntity>> GetEntitiesAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var found = new List<GraphEntity>(names.Count);
        if (names.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<GraphEntity>>(found);
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT COALESCE(display_name, name), type, description, page_rank, source_document_id, source_chunk_ids " +
            "FROM entities WHERE name IN (" + BindNames(cmd, names) + ")";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            found.Add(ReadEntity(reader));
        }

        return Task.FromResult<IReadOnlyList<GraphEntity>>(found);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Two grouped counts summed, rather than one query per name. An entity's degree is the number
    /// of relationships naming it at <i>either</i> end, and the two indexes added in #297 cover the
    /// two halves separately — a single predicate over both columns cannot use both indexes.
    /// </para>
    /// <para>
    /// Names absent from the relationships table are reported as 0 rather than omitted, so a caller
    /// ranking by degree does not have to distinguish "no edges" from "not asked about".
    /// </para>
    /// </remarks>
    public Task<IReadOnlyDictionary<string, int>> GetDegreesAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var degrees = new Dictionary<string, int>(names.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            degrees[names[i]] = 0;
        }

        if (names.Count > 0)
        {
            AddDegrees(degrees, "source_entity", names, ct);
            AddDegrees(degrees, "target_entity", names, ct);
        }

        return Task.FromResult<IReadOnlyDictionary<string, int>>(degrees);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One query, so an edge between two selected entities is returned once — the default's
    /// per-name loop finds it twice and relies on de-duplication to notice.
    /// </remarks>
    public Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var result = new List<GraphRelationship>();
        if (names.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<GraphRelationship>>(result);
        }

        using var cmd = _connection.CreateCommand();
        var bound = BindNames(cmd, names);
        cmd.CommandText =
            "SELECT COALESCE(source_display, source_entity), " +
            "COALESCE(target_display, target_entity), " +
            "description, weight, source_document_id, source_chunk_ids " +
            "FROM relationships " +
            "WHERE source_entity IN (" + bound + ") OR target_entity IN (" + bound + ")";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            result.Add(ReadRelationship(reader));
        }

        return Task.FromResult<IReadOnlyList<GraphRelationship>>(result);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One query for the communities, then one member read each — rather than the default's query
    /// per name followed by a member read per community per name.
    /// </remarks>
    public Task<IReadOnlyList<Community>> GetCommunitiesForEntitiesAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var result = new List<Community>();
        if (names.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Community>>(result);
        }

        var found = new List<(int Id, int Level, string? Report)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT DISTINCT c.id, c.level, c.report_summary FROM communities c " +
                "INNER JOIN community_members cm ON cm.community_id = c.id " +
                "WHERE cm.entity_name IN (" + BindNames(cmd, names) + ")";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                found.Add((reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        for (var i = 0; i < found.Count; i++)
        {
            result.Add(new Community(
                found[i].Id, found[i].Level, LoadCommunityMembers(found[i].Id), found[i].Report));
        }

        return Task.FromResult<IReadOnlyList<Community>>(result);
    }

    /// <summary>Adds degrees counted over one endpoint column into the running totals.</summary>
    /// <param name="degrees">Totals by entity name, pre-seeded with every requested name.</param>
    /// <param name="column">The endpoint column to group by.</param>
    /// <param name="names">Entity names, in any casing.</param>
    /// <param name="ct">Cancels the read.</param>
    private void AddDegrees(
        Dictionary<string, int> degrees, string column, IReadOnlyList<string> names, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();

        // `column` is one of two literals from this file, never caller input.
        cmd.CommandText =
            "SELECT " + column + ", COUNT(*) FROM relationships WHERE " + column + " IN (" +
            BindNames(cmd, names) + ") GROUP BY " + column;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            // Keyed on the folded name the row holds; the dictionary compares case-insensitively,
            // so a caller asking about "Ångström" finds the count stored under the folded key.
            var name = reader.GetString(0);
            degrees[name] = (degrees.TryGetValue(name, out var running) ? running : 0) + reader.GetInt32(1);
        }
    }

    /// <summary>Binds folded entity names as numbered parameters and returns the IN list.</summary>
    /// <remarks>
    /// Parameterised rather than interpolated: entity names come from a language model by way of
    /// extraction, so they are the least trustworthy strings in the system. Names are folded to
    /// match the primary key, and duplicates are left in — SQLite ignores them and de-duplicating
    /// here would cost more than it saves.
    /// </remarks>
    /// <param name="cmd">Command to bind on.</param>
    /// <param name="names">Entity names, in any casing.</param>
    /// <returns>A comma-separated parameter list for an <c>IN</c> clause.</returns>
    private static string BindNames(SqliteCommand cmd, IReadOnlyList<string> names)
    {
        var placeholders = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var parameter = "$n" + i.ToString(CultureInfo.InvariantCulture);
            placeholders[i] = parameter;
            cmd.Parameters.Add(new SqliteParameter(parameter, Fold(names[i])));
        }

        return string.Join(",", placeholders);
    }

    private GraphEntity? LoadEntity(string name)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(display_name, name), type, description, page_rank, source_document_id, source_chunk_ids FROM entities WHERE name = $name";
        cmd.Parameters.Add(new SqliteParameter("$name", Fold(name)));

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

    /// <summary>Materialises one relationship from the shared six-column projection.</summary>
    /// <remarks>
    /// Shared so the two call sites cannot drift in what they select or what they read back — the
    /// kind of drift that adds a column to one query and leaves the other silently returning
    /// relationships with no provenance, which local search would read as relationships that came
    /// from nowhere.
    /// </remarks>
    /// <param name="reader">Positioned on a row of
    /// <c>source_display, target_display, description, weight, source_document_id, source_chunk_ids</c>.</param>
    /// <returns>The relationship.</returns>
    private static GraphRelationship ReadRelationship(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3))
        {
            SourceDocumentId = reader.IsDBNull(4) ? null : reader.GetString(4),
            SourceChunkIds = reader.IsDBNull(5) || string.IsNullOrEmpty(reader.GetString(5))
                ? []
                : reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries),
        };

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
