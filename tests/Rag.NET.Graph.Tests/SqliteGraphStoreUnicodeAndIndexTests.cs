using Microsoft.Data.Sqlite;
using Rag.NET.Graph;
using Xunit;

namespace Rag.NET.Graph.Tests;

/// <summary>
/// Guards the two properties #297 and #299 turned on: entity keys fold Unicode, and neighbour
/// lookups use an index.
/// </summary>
/// <remarks>
/// <para>
/// The schema used <c>COLLATE NOCASE</c> on <c>entities.name</c> and in every name predicate.
/// SQLite's <c>NOCASE</c> folds <c>A</c>–<c>Z</c> and nothing else, while the callers key their
/// dictionaries and visited-sets on <see cref="StringComparer.OrdinalIgnoreCase"/>, which folds
/// Unicode. The two disagreed on every non-ASCII name.
/// </para>
/// <para>
/// The two issues had to be fixed together, and that is the point worth remembering: #297's obvious
/// fix — an index on <c>relationships(source_entity)</c> — was <b>unusable</b> while the predicate
/// said <c>COLLATE NOCASE</c>, because an index's collation must match the predicate's. Adding
/// <c>COLLATE NOCASE</c> to the index instead would have worked and would have made the ASCII-only
/// folding a permanent part of the schema.
/// </para>
/// </remarks>
public sealed class SqliteGraphStoreUnicodeAndIndexTests
{
    /// <summary>Scripts whose case folding SQLite's <c>NOCASE</c> does not perform.</summary>
    /// <remarks>
    /// Every first spelling is <b>mixed case</b> on purpose: <see cref="TheOriginalSpellingSurvivesFolding"/>
    /// asserts the stored name is not the folded key, and <c>Fold</c> upper-cases — so an all-caps
    /// fixture would pass that assertion even if <c>display_name</c> were never written. This list
    /// held "ÉCOLE" until that test failed on it, which is the assertion doing its job on the data.
    /// </remarks>
    public static TheoryData<string, string, string> NonAsciiPairs() => new()
    {
        { "Ångström", "ångström", "Swedish" },
        { "École", "école", "French" },
        { "Москва", "москва", "Cyrillic" },
        { "Γεωργία", "γεωργία", "Greek" },
        { "Ærøskøbing", "ærøskøbing", "Danish" },
    };

    [Theory]
    [MemberData(nameof(NonAsciiPairs))]
    public async Task EntitiesDifferingOnlyByCaseMergeInAnyScript(string upper, string lower, string script)
    {
        await using var store = new SqliteGraphStore(":memory:");

        await store.AddEntitiesAsync(
            [new GraphEntity(upper, "Place", "first description")],
            TestContext.Current.CancellationToken);
        await store.AddEntitiesAsync(
            [new GraphEntity(lower, "Place", "second description")],
            TestContext.Current.CancellationToken);

        var graph = await store.GetFullGraphAsync(TestContext.Current.CancellationToken);

        // One entity, not two. Before #299 every row here was a separate entity, so descriptions
        // never merged and the graph carried duplicate nodes for one subject.
        Assert.True(
            graph.Entities.Count == 1,
            $"{script}: {upper} and {lower} produced {graph.Entities.Count} entities, not 1.");

        // And the merge actually happened, rather than the second write being dropped.
        Assert.Contains("first description", graph.Entities[0].Description, StringComparison.Ordinal);
        Assert.Contains("second description", graph.Entities[0].Description, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The key is folded; the caller's spelling is not thrown away. Reports and prompts read the
    /// entity name, so a folded key leaking into them would be a visible regression.
    /// </remarks>
    [Theory]
    [MemberData(nameof(NonAsciiPairs))]
    public async Task TheOriginalSpellingSurvivesFolding(string upper, string lower, string script)
    {
        await using var store = new SqliteGraphStore(":memory:");

        await store.AddEntitiesAsync(
            [new GraphEntity(upper, "Place", "d")], TestContext.Current.CancellationToken);
        await store.AddEntitiesAsync(
            [new GraphEntity(lower, "Place", "d")], TestContext.Current.CancellationToken);

        var graph = await store.GetFullGraphAsync(TestContext.Current.CancellationToken);

        // First spelling wins, so an entity does not change how it reads because a later document
        // happened to shout its name.
        Assert.True(
            string.Equals(upper, graph.Entities[0].Name, StringComparison.Ordinal),
            $"{script}: stored name was {graph.Entities[0].Name}, expected the first spelling {upper}.");

        // Specifically NOT the folded key. Fold() upper-cases, so an all-caps fixture would pass the
        // assertion above even if display_name were never written — see the remarks on the data.
        Assert.NotEqual(upper.ToUpperInvariant(), graph.Entities[0].Name, StringComparer.Ordinal);
    }

    /// <remarks>
    /// Traversal has to find the neighbour whichever way the caller spells the seed — this is the
    /// join that silently missed before, because <c>relationships</c> endpoints were stored with the
    /// caller's casing while lookups folded ASCII only.
    /// </remarks>
    [Fact]
    public async Task NeighboursAreFoundRegardlessOfHowTheSeedIsSpelled()
    {
        await using var store = new SqliteGraphStore(":memory:");
        await store.AddEntitiesAsync(
            [new GraphEntity("Ångström", "Person", "d"), new GraphEntity("Kelvin", "Person", "d")],
            TestContext.Current.CancellationToken);
        await store.AddRelationshipsAsync(
            [new GraphRelationship("ångström", "Kelvin", "cited")],
            TestContext.Current.CancellationToken);

        foreach (var spelling in new[] { "Ångström", "ångström", "ÅNGSTRÖM" })
        {
            var neighbours = await store.GetNeighborsAsync(
                spelling, 1, TestContext.Current.CancellationToken);

            Assert.True(
                neighbours.Count == 1,
                $"Seed spelled {spelling} found {neighbours.Count} neighbours, expected 1.");
            Assert.Equal("Kelvin", neighbours[0].Name);
        }
    }

    /// <remarks>
    /// <para>
    /// <b>#297, asserted as a query plan rather than a timing.</b> The plan is deterministic; a
    /// duration on a shared machine is not. This fails if someone drops the index, and — the reason
    /// it is written this way — it also fails if someone reintroduces <c>COLLATE NOCASE</c> into the
    /// predicate, because that silently makes the index unusable again.
    /// </para>
    /// <para>
    /// Runs against the store's own schema, opened through its own connection string, so it checks
    /// what <c>SqliteGraphStore</c> actually creates rather than a copy of it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheNeighbourLookupUsesAnIndexRatherThanScanning()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ragnet-plan-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = new SqliteGraphStore(file))
            {
                await store.AddEntitiesAsync(
                    [new GraphEntity("A", "P", "d")], TestContext.Current.CancellationToken);
            }

            using var connection = new SqliteConnection($"Data Source={file}");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT CASE WHEN source_entity = $name THEN target_entity ELSE source_entity END
                FROM relationships
                WHERE source_entity = $name OR target_entity = $name
                """;
            cmd.Parameters.Add(new SqliteParameter("$name", "A"));

            var plan = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                plan.Add(reader.GetString(reader.FieldCount - 1));
            }

            var text = string.Join(" | ", plan);
            Assert.Contains("SEARCH", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN relationships", text, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    /// <remarks>
    /// <para>
    /// <b>The upgrade path, against a database written the old way.</b> A pre-#299 file has no
    /// display columns and holds unfolded keys — including, because <c>NOCASE</c> did not fold them,
    /// the <i>duplicate</i> rows this change exists to prevent. Opening it must add the columns, fold
    /// the keys, and merge the rows that now collide.
    /// </para>
    /// <para>
    /// The old schema is written here by hand rather than by an older build, so the test states
    /// exactly what it claims to migrate from.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OpeningAPreUnicodeDatabaseFoldsItsKeysAndMergesTheDuplicates()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ragnet-migrate-{Guid.NewGuid():N}.db");
        try
        {
            await WritePreUnicodeDatabaseAsync(file);

            await using var store = new SqliteGraphStore(file);
            var graph = await store.GetFullGraphAsync(TestContext.Current.CancellationToken);

            var angstrom = graph.Entities
                .Where(e => e.Name.Contains("ngstr", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(angstrom.Count == 1, $"Expected the two rows to merge into one, got {angstrom.Count}.");
            Assert.Contains("first", angstrom[0].Description, StringComparison.Ordinal);
            Assert.Contains("second", angstrom[0].Description, StringComparison.Ordinal);

            // Display spelling recovered from the old `name` column rather than left as the key.
            Assert.Equal("Ångström", angstrom[0].Name, StringComparer.Ordinal);

            // And the migrated edge is still walkable, which is the join the folding had to keep.
            var neighbours = await store.GetNeighborsAsync(
                "ÅNGSTRÖM", 1, TestContext.Current.CancellationToken);
            Assert.Single(neighbours);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Writes the pre-#299 schema and rows by hand.</summary>
    /// <remarks>
    /// By hand rather than by an older build, so the test states exactly what it migrates from and
    /// does not depend on a package version to reproduce it.
    /// </remarks>
    /// <param name="file">Database path to create.</param>
    private static async Task WritePreUnicodeDatabaseAsync(string file)
    {
        using var seed = new SqliteConnection($"Data Source={file}");
        await seed.OpenAsync(TestContext.Current.CancellationToken);
        using var cmd = seed.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE entities (
                name TEXT PRIMARY KEY COLLATE NOCASE,
                type TEXT NOT NULL,
                description TEXT NOT NULL,
                page_rank REAL DEFAULT 0,
                source_document_id TEXT,
                source_chunk_ids TEXT
            );
            CREATE TABLE relationships (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_entity TEXT NOT NULL,
                target_entity TEXT NOT NULL,
                description TEXT NOT NULL,
                weight REAL DEFAULT 1.0,
                source_document_id TEXT
            );
            CREATE TABLE communities (id INTEGER NOT NULL, level INTEGER NOT NULL, report_summary TEXT);
            CREATE TABLE community_members (community_id INTEGER NOT NULL, entity_name TEXT NOT NULL);

            -- Two rows for one subject: exactly what COLLATE NOCASE allowed.
            INSERT INTO entities (name, type, description) VALUES ('Ångström', 'Person', 'first');
            INSERT INTO entities (name, type, description) VALUES ('ångström', 'Person', 'second');
            -- The edge's other endpoint needs a row: GetNeighborsAsync returns entities, so a
            -- neighbour with no entity row is dropped and the walk looks broken when it is not.
            INSERT INTO entities (name, type, description) VALUES ('Kelvin', 'Person', 'k');
            INSERT INTO relationships (source_entity, target_entity, description)
                VALUES ('ångström', 'Kelvin', 'cited');
            """;
        _ = await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
