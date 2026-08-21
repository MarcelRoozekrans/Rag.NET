# Corpus-Level RAPTOR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Rag.NET.Raptor` cluster across the whole corpus rather than within each document, keeping the per-document scope selectable, and fix the chunk-index collision that affects both.

**Architecture:** A new `Rag.NET.Raptor.Store` package persists leaf chunks with their embedding vectors — the thing `IVectorStore` cannot give back. `RaptorIngestionBehavior` gains a `TreeScope` option: under `PerDocument` it behaves exactly as today; under `Corpus` it writes leaves to that store and debounces a whole-corpus rebuild on growth, mirroring `CommunityDetectionBehavior`'s #302 design. A `RaptorTreeRebuilder` forces a rebuild on demand, going through the same code path as ingestion so the two cannot drift.

**Tech Stack:** .NET 10, C#, xunit.v3, Microsoft.Data.Sqlite, ZeroAlloc.Validation, MathNet.Numerics.

**Spec:** `docs/superpowers/specs/2026-08-20-corpus-level-raptor-design.md`

**Issues:** #331 (tree scope), #332 (chunk-index collision).

## Global Constraints

- **`TreatWarningsAsErrors=true`** is set in `Directory.Build.props`. A warning fails the build. This includes analyzer diagnostics from Meziantou.Analyzer and Roslynator.
- **Every package under `src/` must declare `<VerifiedBy>`** in its csproj — one of `unit`, `integration`, `container`, `benchmark`, `recorded`, `live`, `none`. Enforced by `tests/Rag.NET.RepoConventions.Tests/PackageVerificationTests.cs`. A `unit` value additionally wants a `<VerifiedByReason>`.
- **Commits are conventional with free scopes** (`docs/planning/CONVENTIONS.md`): `<type>(<scope>): <subject>`.
- **`main` is protected and requires a PR.** Work on a feature branch; do not commit to `main`.
- **String comparisons and dictionary construction must be explicit**: `StringComparer.Ordinal` for dictionaries keyed by string, `StringComparison.Ordinal` for comparisons, `CultureInfo.InvariantCulture` for number formatting. The analyzers fail the build otherwise.
- **New projects must be added to `Rag.NET.slnx`** — `src/` projects in the `src` block, test projects in the `tests` block.
- **Do not delete `RaptorRetrievalMode.Boost` / `Filter` or change their behaviour.** Their defects are #331's sibling spec's business (`docs/superpowers/specs/2026-08-20-raptor-real-protocol-design.md`), not this plan's.

---

### Task 1: Fix the chunk-index collision (#332)

Summary chunks at different tree levels currently receive identical `ChunkIndex` values, so two chunks share one `ChunkKey`. This lands first because it is independent of everything else, it affects the shipped per-document path today, and leaving it would corrupt the control arm that Phase 6.2.1 measures against.

**Files:**
- Modify: `src/Rag.NET.Raptor/RaptorIngestionBehavior.cs`
- Test: `tests/Rag.NET.Raptor.Tests/RaptorIngestionBehaviorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `RaptorIngestionBehavior.BuildLevelAsync` gains a `int nextChunkIndex` parameter and returns the summaries for that level; `HandleAsync` owns a running counter. No public API change.

- [ ] **Step 1: Write the failing test**

Add to `tests/Rag.NET.Raptor.Tests/RaptorIngestionBehaviorTests.cs`.

**Use the helpers the file already has — do not write new fakes.** This project uses **NSubstitute**, and the test class already holds `_chatClient` and `_embedder` substitutes plus `CreateContext(int chunkCount, int embeddingDims = 8)`, `SetupChatClient(string response)` and `SetupEmbedder(int dims)`. Read lines 1–30 and 185–235 of that file before writing anything. Every test in this plan follows that idiom.

```csharp
[Fact]
public async Task SummaryChunks_HaveUniqueChunkIndexes_AcrossEveryTreeLevel()
{
    // 24 leaves cluster into several level-1 summaries, which cluster again into
    // level-2 summaries. Depth >= 2 is the ordinary case: MaxTreeDepth defaults to null.
    SetupChatClient("a summary");
    SetupEmbedder(dims: 8);
    var ctx = CreateContext(chunkCount: 24);
    var behavior = new RaptorIngestionBehavior(_chatClient, _embedder, new RaptorOptions());

    await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(new IngestionResult()));

    var summaries = ctx.EmbeddedChunks
        .Where(c => c.Chunk.Metadata.ContainsKey("raptor_level"))
        .ToList();

    Assert.True(summaries.Count > 1, "test needs a tree with more than one summary to be meaningful");
    Assert.Contains(summaries, c => !string.Equals(c.Chunk.Metadata["raptor_level"].ToString(), "1", StringComparison.Ordinal));

    var keys = ctx.EmbeddedChunks
        .Select(c => new ChunkKey(c.Chunk.DocumentId.Value, c.Chunk.ChunkIndex))
        .ToList();

    Assert.Equal(keys.Count, keys.Distinct().Count());
}
```

- [ ] **Step 2: Run it to verify it fails**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~SummaryChunks_HaveUniqueChunkIndexes"
```

Expected: FAIL. Level 1's first summary and level 2's first summary both hold `ChunkIndex = leafCount + 0`, so `Distinct()` returns fewer keys than `keys.Count`.

If it *passes*, stop and report — the clustering produced only one level, so the test is not exercising the defect. Raise the leaf count until a second level is built.

- [ ] **Step 3: Thread a running counter through the level loop**

In `HandleAsync`, replace the tree-building loop:

```csharp
var currentLevel = new List<EmbeddedChunk>(ctx.EmbeddedChunks);
var allSummaries = new List<EmbeddedChunk>();
var level = 0;
// Summary indices continue past the leaves and keep counting across levels. They must not
// restart per level: ctx.EmbeddedChunks is not appended to until after this loop, so a
// per-level index made level 2's first summary collide with level 1's (#332).
var nextChunkIndex = ctx.EmbeddedChunks.Count;

while (currentLevel.Count > 1 && (options.MaxTreeDepth is null || level < options.MaxTreeDepth))
{
    level++;
    var summaryChunks = await BuildLevelAsync(currentLevel, ctx, level, nextChunkIndex, ct).ConfigureAwait(false);
    if (summaryChunks is null)
        break;

    nextChunkIndex += summaryChunks.Count;
    allSummaries.AddRange(summaryChunks);
    currentLevel = summaryChunks;
}
```

- [ ] **Step 4: Pass the base index down to the summary construction**

Change `BuildLevelAsync`'s signature and its call to `SummarizeClusterAsync`:

```csharp
private async Task<List<EmbeddedChunk>?> BuildLevelAsync(
    List<EmbeddedChunk> currentLevel, IngestionContext ctx, int level, int baseChunkIndex, CancellationToken ct)
```

and inside its cluster loop:

```csharp
var summaryChunk = await SummarizeClusterAsync(
    cluster.Chunks, cluster.ClusterId, ctx, level, baseChunkIndex + summaryChunks.Count, client, emb, ct)
    .ConfigureAwait(false);
```

Then in `SummarizeClusterAsync`, rename the parameter and use it directly:

```csharp
private async Task<EmbeddedChunk> SummarizeClusterAsync(
    List<EmbeddedChunk> childChunks, int clusterId, IngestionContext ctx,
    int level, int chunkIndex, IChatClient client,
    IEmbeddingGenerator<string, Embedding<float>> emb, CancellationToken ct)
```

and in the returned `TextChunk`, replace `ChunkIndex = ctx.EmbeddedChunks.Count + summaryIndex` with:

```csharp
ChunkIndex = chunkIndex,
```

- [ ] **Step 5: Run the test to verify it passes**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~SummaryChunks_HaveUniqueChunkIndexes"
```

Expected: PASS.

- [ ] **Step 6: Run the whole Raptor test project for regressions**

```
dotnet test tests/Rag.NET.Raptor.Tests
```

Expected: all PASS. If an existing test asserted a specific `ChunkIndex` value for a summary, it encoded the bug — update it to the new value and say so in the commit body.

- [ ] **Step 7: Commit**

```bash
git add src/Rag.NET.Raptor/RaptorIngestionBehavior.cs tests/Rag.NET.Raptor.Tests/RaptorIngestionBehaviorTests.cs
git commit -m "fix(raptor): summary chunks no longer collide on ChunkIndex across tree levels (#332)"
```

---

### Task 2: The `Rag.NET.Raptor.Store` package

A new package holding `IRaptorLeafStore` and its SQLite implementation together, mirroring how `Rag.NET.Graph` pairs `IGraphStore` with `SqliteGraphStore`. Nothing consumes it yet — Task 3 wires it in.

**Files:**
- Create: `src/Rag.NET.Raptor.Store/Rag.NET.Raptor.Store.csproj`
- Create: `src/Rag.NET.Raptor.Store/RaptorLeaf.cs`
- Create: `src/Rag.NET.Raptor.Store/IRaptorLeafStore.cs`
- Create: `src/Rag.NET.Raptor.Store/SqliteRaptorLeafStore.cs`
- Create: `src/Rag.NET.Raptor.Store/README.md`
- Create: `tests/Rag.NET.Raptor.Store.Tests/Rag.NET.Raptor.Store.Tests.csproj`
- Create: `tests/Rag.NET.Raptor.Store.Tests/SqliteRaptorLeafStoreTests.cs`
- Modify: `Rag.NET.slnx`
- Modify: `tests/Rag.NET.RepoConventions.Tests/PackageVerificationTests.cs` (doc comment says "71 packages"; it becomes 72)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `Rag.NET.Raptor.Store.RaptorLeaf` — `sealed record RaptorLeaf(string DocumentId, int ChunkIndex, string Text, float[] Embedding)`
  - `Rag.NET.Raptor.Store.IRaptorLeafStore : IAsyncDisposable` with `Task InitializeAsync(CancellationToken)`, `Task AddLeavesAsync(IReadOnlyList<RaptorLeaf>, CancellationToken)`, `Task<IReadOnlyList<RaptorLeaf>> GetAllLeavesAsync(CancellationToken)`, `Task<int> CountAsync(CancellationToken)`, `Task RemoveDocumentAsync(string documentId, CancellationToken)`
  - `Rag.NET.Raptor.Store.SqliteRaptorLeafStore : IRaptorLeafStore` with constructor `SqliteRaptorLeafStore(string connectionStringOrPath)`

- [ ] **Step 1: Create the project file**

`src/Rag.NET.Raptor.Store/Rag.NET.Raptor.Store.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Raptor.Store</RootNamespace>
    <PackageId>Rag.NET.Raptor.Store</PackageId>
    <Description>Persistent leaf-chunk storage for corpus-level RAPTOR clustering</Description>
    <PackageTags>$(PackageTags);raptor;storage;sqlite</PackageTags>
    <!-- integration: SqliteRaptorLeafStoreTests writes a real SQLite file, closes the store and
         reopens it, asserting the leaves and their vectors survive the round trip. Not `unit`:
         the thing under test is whether a real storage engine returns what was put in. -->
    <VerifiedBy>integration</VerifiedBy>
  </PropertyGroup>

  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.Raptor.Store.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" />
    <!-- Lifts transitive SQLitePCLRaw.lib.e_sqlite3 above 2.1.11 (GHSA-2m69-gcr7-jv3q). -->
    <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" />
  </ItemGroup>

</Project>
```

No `ProjectReference` to `Rag.NET` or `Rag.NET.Abstractions`. `RaptorLeaf` deliberately uses `string` and `float[]` rather than `DocumentId` and `ReadOnlyMemory<float>` so this package stays standalone, exactly as `Rag.NET.Graph` avoids referencing core.

- [ ] **Step 2: Write the model and the interface**

`src/Rag.NET.Raptor.Store/RaptorLeaf.cs`:

```csharp
namespace Rag.NET.Raptor.Store;

/// <summary>A leaf chunk and its embedding vector, as persisted for corpus-level clustering.</summary>
/// <remarks>
/// Deliberately built from <see cref="string"/> and <see cref="float"/>[] rather than the core
/// <c>TextChunk</c> / <c>EmbeddedChunk</c> models, so this package needs no reference to
/// <c>Rag.NET</c> — the same standalone posture <c>Rag.NET.Graph</c> keeps.
/// </remarks>
/// <param name="DocumentId">The owning document's identifier.</param>
/// <param name="ChunkIndex">The chunk's index within its document. Unique with <paramref name="DocumentId"/>.</param>
/// <param name="Text">The chunk's text, needed to summarise the cluster it lands in.</param>
/// <param name="Embedding">The chunk's embedding vector, which is what clustering runs on.</param>
public sealed record RaptorLeaf(string DocumentId, int ChunkIndex, string Text, float[] Embedding);
```

`src/Rag.NET.Raptor.Store/IRaptorLeafStore.cs`:

```csharp
namespace Rag.NET.Raptor.Store;

/// <summary>
/// Stores leaf chunks with their embedding vectors so that RAPTOR can cluster over the whole
/// corpus rather than one document at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the vector store cannot do this.</b> <c>IVectorStore</c> is <c>StoreAsync</c>,
/// <c>SearchAsync</c> and <c>DeleteByDocumentIdAsync</c> — nothing enumerates. <c>IChunkLookup</c>
/// is by key, so a caller would already need every identity, and it returns <c>TextChunk</c>
/// without the embedding that clustering actually runs on. It is also implemented by only two
/// stores (#318).
/// </para>
/// <para>
/// Written only when <c>RaptorOptions.TreeScope</c> is <c>Corpus</c>. Under <c>PerDocument</c> the
/// behaviour already holds every chunk it needs in the ingestion context, so nothing is stored and
/// nothing is paid for.
/// </para>
/// </remarks>
public interface IRaptorLeafStore : IAsyncDisposable
{
    /// <summary>Creates or migrates any backing storage the store needs.</summary>
    /// <param name="cancellationToken">Cancels the initialisation.</param>
    /// <returns>A task that completes when the store is ready to use.</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds leaves, upserting on <c>(DocumentId, ChunkIndex)</c> — re-ingesting a document
    /// replaces its rows rather than duplicating them.
    /// </summary>
    /// <param name="leaves">The leaves to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the leaves are durable.</returns>
    Task AddLeavesAsync(IReadOnlyList<RaptorLeaf> leaves, CancellationToken cancellationToken = default);

    /// <summary>Returns every stored leaf. This is the corpus that clustering runs over.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every leaf, in unspecified order.</returns>
    Task<IReadOnlyList<RaptorLeaf>> GetAllLeavesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns how many leaves are stored, without loading them.</summary>
    /// <remarks>
    /// Exists so the growth debounce can decide whether to rebuild without paying for a full load.
    /// <c>CommunityDetectionBehavior</c> records the absence of exactly this on <c>IGraphStore</c>
    /// as a cost it chose not to remove; there is no reason to repeat that here.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of stored leaves.</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes every leaf stored for a document.</summary>
    /// <param name="documentId">The document whose leaves are removed.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes when the rows are gone.</returns>
    Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Write the failing round-trip test**

`tests/Rag.NET.Raptor.Store.Tests/Rag.NET.Raptor.Store.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Raptor.Store\Rag.NET.Raptor.Store.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

`tests/Rag.NET.Raptor.Store.Tests/SqliteRaptorLeafStoreTests.cs`:

```csharp
using Rag.NET.Raptor.Store;
using Xunit;

namespace Rag.NET.Raptor.Store.Tests;

public sealed class SqliteRaptorLeafStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"raptor-leaves-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Leaves_SurviveClosingAndReopeningTheStore()
    {
        var leaves = new[]
        {
            new RaptorLeaf("doc-a", 0, "first", [0.1f, 0.2f, 0.3f]),
            new RaptorLeaf("doc-a", 1, "second", [0.4f, 0.5f, 0.6f]),
            new RaptorLeaf("doc-b", 0, "third", [0.7f, 0.8f, 0.9f]),
        };

        await using (var store = new SqliteRaptorLeafStore(_path))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await store.AddLeavesAsync(leaves, TestContext.Current.CancellationToken);
        }

        await using var reopened = new SqliteRaptorLeafStore(_path);
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, await reopened.CountAsync(TestContext.Current.CancellationToken));

        var all = await reopened.GetAllLeavesAsync(TestContext.Current.CancellationToken);
        var second = all.Single(l => l.DocumentId == "doc-a" && l.ChunkIndex == 1);
        Assert.Equal("second", second.Text);
        Assert.Equal([0.4f, 0.5f, 0.6f], second.Embedding);
    }

    [Fact]
    public async Task AddLeaves_UpsertsOnDocumentAndChunkIndex()
    {
        await using var store = new SqliteRaptorLeafStore(_path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.AddLeavesAsync([new RaptorLeaf("doc-a", 0, "original", [1f])], TestContext.Current.CancellationToken);
        await store.AddLeavesAsync([new RaptorLeaf("doc-a", 0, "replaced", [2f])], TestContext.Current.CancellationToken);

        Assert.Equal(1, await store.CountAsync(TestContext.Current.CancellationToken));
        var only = Assert.Single(await store.GetAllLeavesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("replaced", only.Text);
    }

    [Fact]
    public async Task RemoveDocument_RemovesOnlyThatDocumentsLeaves()
    {
        await using var store = new SqliteRaptorLeafStore(_path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.AddLeavesAsync(
            [new RaptorLeaf("doc-a", 0, "a", [1f]), new RaptorLeaf("doc-b", 0, "b", [2f])],
            TestContext.Current.CancellationToken);

        await store.RemoveDocumentAsync("doc-a", TestContext.Current.CancellationToken);

        var remaining = Assert.Single(await store.GetAllLeavesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("doc-b", remaining.DocumentId);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
```

- [ ] **Step 4: Add both projects to the solution, then run the tests to verify they fail**

In `Rag.NET.slnx`, add `<Project Path="src/Rag.NET.Raptor.Store/Rag.NET.Raptor.Store.csproj" />` immediately after the `Rag.NET.Raptor` line, and `<Project Path="tests/Rag.NET.Raptor.Store.Tests/Rag.NET.Raptor.Store.Tests.csproj" />` immediately after the `Rag.NET.Raptor.Tests` line.

```
dotnet test tests/Rag.NET.Raptor.Store.Tests
```

Expected: FAIL to compile — `SqliteRaptorLeafStore` does not exist yet.

- [ ] **Step 5: Implement the SQLite store**

`src/Rag.NET.Raptor.Store/SqliteRaptorLeafStore.cs`:

```csharp
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
            return Task.CompletedTask;

        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO leaves (document_id, chunk_index, text, embedding)
            VALUES ($doc, $idx, $text, $emb)
            ON CONFLICT(document_id, chunk_index)
            DO UPDATE SET text = excluded.text, embedding = excluded.embedding;
            """;

        var doc = cmd.CreateParameter(); doc.ParameterName = "$doc"; cmd.Parameters.Add(doc);
        var idx = cmd.CreateParameter(); idx.ParameterName = "$idx"; cmd.Parameters.Add(idx);
        var text = cmd.CreateParameter(); text.ParameterName = "$text"; cmd.Parameters.Add(text);
        var emb = cmd.CreateParameter(); emb.ParameterName = "$emb"; cmd.Parameters.Add(emb);

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
```

- [ ] **Step 6: Run the tests to verify they pass**

```
dotnet test tests/Rag.NET.Raptor.Store.Tests
```

Expected: 3 PASS.

- [ ] **Step 7: Write the package README**

`src/Rag.NET.Raptor.Store/README.md`. Match the tone and structure of `src/Rag.NET.Storage.Sqlite/README.md` — read it first. Cover: what the package is for, that it is only needed when `RaptorOptions.TreeScope` is `Corpus`, and the `SqliteRaptorLeafStore(path)` constructor.

- [ ] **Step 8: Update the package-count doc comment**

In `tests/Rag.NET.RepoConventions.Tests/PackageVerificationTests.cs`, the `FewestPlausiblePackages` doc comment reads "There are 71 packages under `src/` today." Change 71 to 72. Do **not** change `FewestPlausiblePackages` itself — it is a floor, not a count.

- [ ] **Step 9: Run the conventions tests, then commit**

```
dotnet test tests/Rag.NET.RepoConventions.Tests
```

Expected: PASS — the new package declares `<VerifiedBy>integration</VerifiedBy>`.

```bash
git add src/Rag.NET.Raptor.Store tests/Rag.NET.Raptor.Store.Tests Rag.NET.slnx tests/Rag.NET.RepoConventions.Tests/PackageVerificationTests.cs
git commit -m "feat(raptor): add Rag.NET.Raptor.Store for corpus-level leaf persistence (#331)"
```

---

### Task 3: `TreeScope` option and the leaf-store write path

Adds the option defaulting to `PerDocument`, so this task changes no existing behaviour, and makes `Corpus` scope persist its leaves. `Corpus` does not build a tree yet — Task 4 does that.

**Files:**
- Modify: `src/Rag.NET.Raptor/RaptorOptions.cs`
- Create: `src/Rag.NET.Raptor/RaptorTreeScope.cs`
- Modify: `src/Rag.NET.Raptor/RaptorIngestionBehavior.cs`
- Modify: `src/Rag.NET.Raptor/RagBuilderExtensions.cs`
- Modify: `src/Rag.NET.Raptor/Rag.NET.Raptor.csproj` (ProjectReference to the store package)
- Test: `tests/Rag.NET.Raptor.Tests/RaptorTreeScopeTests.cs` (new file)
- Modify: `tests/Rag.NET.Raptor.Tests/Rag.NET.Raptor.Tests.csproj` (ProjectReference to the store package)

**Interfaces:**
- Consumes: `IRaptorLeafStore`, `RaptorLeaf`, `SqliteRaptorLeafStore` from Task 2.
- Produces:
  - `Rag.NET.Raptor.RaptorTreeScope` — `enum { PerDocument, Corpus }`
  - `RaptorOptions.TreeScope` — `RaptorTreeScope`, defaulting to `RaptorTreeScope.PerDocument` in this task (Task 6 flips it to `Corpus`)
  - `RaptorIngestionBehavior` constructor gains a fifth parameter: `IRaptorLeafStore? leafStore` (null is legal and means `Corpus` scope cannot run)

- [ ] **Step 1: Add the enum**

`src/Rag.NET.Raptor/RaptorTreeScope.cs`:

```csharp
namespace Rag.NET.Raptor;

/// <summary>Controls what set of chunks RAPTOR clusters over when it builds its tree.</summary>
public enum RaptorTreeScope
{
    /// <summary>
    /// Cluster within one document's chunks, at ingestion time.
    /// </summary>
    /// <remarks>
    /// The library's original behaviour. A tree built this way cannot produce a node spanning two
    /// documents, so its summaries answer questions about one document's themes and nothing wider.
    /// Kept selectable rather than removed because it is the control arm Phase 6.2.1 differences
    /// the corpus scope against.
    /// </remarks>
    PerDocument,

    /// <summary>
    /// Cluster across every leaf chunk in the corpus, rebuilt on growth rather than per document.
    /// </summary>
    /// <remarks>
    /// What the RAPTOR paper describes. Requires an <see cref="Store.IRaptorLeafStore"/>, because
    /// the vector store cannot enumerate what it holds. Ingesting a single document no longer
    /// produces a tree immediately: summaries appear once the corpus crosses
    /// <see cref="RaptorOptions.CorpusGrowthThreshold"/> or a rebuild is requested.
    /// </remarks>
    Corpus,
}
```

- [ ] **Step 2: Add the options**

In `src/Rag.NET.Raptor/RaptorOptions.cs`, add these two properties. Note the validation attributes mirror `GraphRagOptions.CommunityDetectionGrowthThreshold` exactly — including the finite check, which the generator requires a companion method for.

```csharp
    /// <summary>What set of chunks the tree is built over. Default: <see cref="RaptorTreeScope.PerDocument"/>.</summary>
    public RaptorTreeScope TreeScope { get; set; } = RaptorTreeScope.PerDocument;

    /// <summary>
    /// Under <see cref="RaptorTreeScope.Corpus"/>, the fractional growth in stored leaves that
    /// triggers a tree rebuild. Default: 0.10 — rebuild once the corpus is 10% larger than it was
    /// at the last build. Zero or negative rebuilds on every ingest.
    /// <para>
    /// The same shape and default as <c>GraphRagOptions.CommunityDetectionGrowthThreshold</c>, and
    /// for the same reason: clustering the whole corpus once per ingested document is #300's defect,
    /// and this is the debounce that stops it recurring here.
    /// </para>
    /// </summary>
    [InclusiveBetween(0.0, 100.0)]
    [Must(nameof(CorpusGrowthThresholdIsFinite), Message =
        "CorpusGrowthThreshold must be a finite number (not NaN or infinity).")]
    public double CorpusGrowthThreshold { get; set; } = 0.10;

    /// <summary>Reports whether <see cref="CorpusGrowthThreshold"/> is a finite number.</summary>
    /// <param name="value">The <see cref="CorpusGrowthThreshold"/> under validation.</param>
    /// <returns>Whether the value is neither NaN nor infinite.</returns>
    internal bool CorpusGrowthThresholdIsFinite(double value) => double.IsFinite(value);
```

- [ ] **Step 3: Write the failing test**

`tests/Rag.NET.Raptor.Tests/RaptorTreeScopeTests.cs`.

**First, promote the shared helpers.** `CreateContext`, `SetupChatClient` and `SetupEmbedder` are private to `RaptorIngestionBehaviorTests`. Move them into a new `internal sealed class RaptorTestContext` in the test project, holding the two NSubstitute fields and those three methods, and give `CreateContext` a `documentId` parameter defaulting to the value it uses today:

```csharp
internal IngestionContext CreateContext(int chunkCount, int embeddingDims = 8, string documentId = "test-doc")
```

Update `RaptorIngestionBehaviorTests` to use it. One move, not a duplicate — every later task's tests need the same helpers, and a second copy would drift.

```csharp
[Fact]
public async Task CorpusScope_WritesLeavesToTheLeafStore_AndBuildsNoPerDocumentTree()
{
    await using var leafStore = new SqliteRaptorLeafStore(":memory:");
    await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

    var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
    var ctx = _helpers.CreateContext(chunkCount: 12);

    await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(new IngestionResult()));

    Assert.Equal(12, await leafStore.CountAsync(TestContext.Current.CancellationToken));
    Assert.DoesNotContain(ctx.EmbeddedChunks, c => c.Chunk.Metadata.ContainsKey("raptor_level"));
}

[Fact]
public async Task PerDocumentScope_WritesNothingToTheLeafStore()
{
    await using var leafStore = new SqliteRaptorLeafStore(":memory:");
    await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

    var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
    var ctx = _helpers.CreateContext(chunkCount: 12);

    await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(new IngestionResult()));

    Assert.Equal(0, await leafStore.CountAsync(TestContext.Current.CancellationToken));
    Assert.Contains(ctx.EmbeddedChunks, c => c.Chunk.Metadata.ContainsKey("raptor_level"));
}
```

- [ ] **Step 4: Run them to verify they fail**

Add `<ProjectReference Include="..\..\src\Rag.NET.Raptor.Store\Rag.NET.Raptor.Store.csproj" />` to `tests/Rag.NET.Raptor.Tests/Rag.NET.Raptor.Tests.csproj`, and to `src/Rag.NET.Raptor/Rag.NET.Raptor.csproj`.

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~RaptorTreeScopeTests"
```

Expected: FAIL to compile — the constructor has four parameters, not five.

- [ ] **Step 5: Branch on scope in the behaviour**

In `src/Rag.NET.Raptor/RaptorIngestionBehavior.cs`, change the primary constructor and `HandleAsync`'s opening:

```csharp
public sealed class RaptorIngestionBehavior(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    RaptorOptions options,
    IRaptorLeafStore? leafStore = null) : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (!options.Enabled)
            return await next(ctx, ct).ConfigureAwait(false);

        if (options.TreeScope == RaptorTreeScope.Corpus)
        {
            await PersistLeavesAsync(ctx, ct).ConfigureAwait(false);
            return await next(ctx, ct).ConfigureAwait(false);
        }

        if (ctx.EmbeddedChunks.Count < options.MinChunksForRaptor)
            return await next(ctx, ct).ConfigureAwait(false);

        // ... existing per-document tree building, unchanged ...
```

Add the persistence helper:

```csharp
    /// <summary>
    /// Copies this document's leaf chunks into the leaf store, so a later corpus-wide rebuild can
    /// read them back. <c>MinChunksForRaptor</c> is deliberately not applied: it decides whether one
    /// document is worth a tree of its own, and under corpus scope a short document still
    /// contributes its chunks to the corpus.
    /// </summary>
    private async Task PersistLeavesAsync(IngestionContext ctx, CancellationToken ct)
    {
        if (leafStore is null)
        {
            throw new InvalidOperationException(
                "RaptorOptions.TreeScope is Corpus but no IRaptorLeafStore is registered. " +
                "Register one with UseRaptor(..., leafStorePath: \"...\"), or set TreeScope to PerDocument.");
        }

        if (ctx.EmbeddedChunks.Count == 0)
            return;

        var leaves = new List<RaptorLeaf>(ctx.EmbeddedChunks.Count);
        foreach (var chunk in ctx.EmbeddedChunks)
        {
            leaves.Add(new RaptorLeaf(
                chunk.Chunk.DocumentId.Value,
                chunk.Chunk.ChunkIndex,
                chunk.Chunk.Text,
                chunk.Embedding.ToArray()));
        }

        await leafStore.AddLeavesAsync(leaves, ct).ConfigureAwait(false);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

```
dotnet test tests/Rag.NET.Raptor.Tests
```

Expected: all PASS, including Task 1's collision test.

- [ ] **Step 7: Wire the store into `UseRaptor`**

In `src/Rag.NET.Raptor/RagBuilderExtensions.cs`, add a `leafStorePath` parameter and register the store. The signature becomes:

```csharp
    public static TBuilder UseRaptor<TBuilder>(
        this TBuilder builder,
        Action<RaptorOptions>? configure = null,
        Action<RaptorRetrievalOptions>? retrieval = null,
        string? leafStorePath = null)
        where TBuilder : IRagBuilder
```

Registered after the `retrievalOptions` singleton and before the behaviour, so the factory can resolve it:

```csharp
        if (leafStorePath is not null)
        {
            builder.Services.AddSingleton<IRaptorLeafStore>(_ =>
            {
                var store = new SqliteRaptorLeafStore(leafStorePath);
                store.InitializeAsync().GetAwaiter().GetResult();
                return store;
            });
        }
```

and the behaviour's existing factory gains the fourth argument:

```csharp
        builder.Services.AddSingleton<RaptorIngestionBehavior>(sp =>
            new RaptorIngestionBehavior(
                options.SummaryChatClient ?? sp.GetRequiredService<IChatClient>(),
                options.SummaryEmbedder ?? sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                options,
                sp.GetService<IRaptorLeafStore>()));
```

`GetService`, not `GetRequiredService` — a `PerDocument` configuration registers no store and must still resolve. The `InvalidOperationException` in `PersistLeavesAsync` is what reports the misconfiguration, and it names the fix.

**Validate the combination at registration time**, matching the file's existing `ThrowIfInvalid` posture — a configuration error should surface at the line that wrote it, not at the first ingestion:

```csharp
        if (options.TreeScope == RaptorTreeScope.Corpus && leafStorePath is null)
        {
            throw new ArgumentException(
                "RaptorOptions.TreeScope is Corpus, which clusters over the whole corpus and therefore " +
                "needs somewhere to keep leaf chunks between ingests. Pass leafStorePath, or set " +
                "TreeScope to PerDocument.",
                nameof(leafStorePath));
        }
```

Place this immediately after the `RaptorOptionsValidator` call, so it runs before anything is registered.

- [ ] **Step 8: Run the full test suite, then commit**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Raptor.Tests tests/Rag.NET.Raptor.Store.Tests
```

```bash
git add src/Rag.NET.Raptor tests/Rag.NET.Raptor.Tests
git commit -m "feat(raptor): add TreeScope and persist leaves under corpus scope (#331)"
```

---

### Task 4: Corpus-wide tree building with a growth debounce

**Files:**
- Modify: `src/Rag.NET.Raptor/RaptorIngestionBehavior.cs`
- Create: `src/Rag.NET.Raptor/RaptorCorpusDocumentId.cs`
- Test: `tests/Rag.NET.Raptor.Tests/RaptorCorpusBuildTests.cs` (new file)

**Interfaces:**
- Consumes: `RaptorTreeScope`, `RaptorOptions.CorpusGrowthThreshold`, `IRaptorLeafStore` from Task 3.
- Produces:
  - `Rag.NET.Raptor.RaptorCorpusDocumentId.Value` — `const string` = `"raptor://corpus-tree"`
  - `RaptorIngestionBehavior.BuildCorpusTreeNowAsync(IngestionContext ctx, CancellationToken ct)` — `internal Task<int>`, returns the number of summaries produced, bypasses the growth threshold and resets its baseline. Task 5 calls this.

- [ ] **Step 1: Add the reserved document id**

`src/Rag.NET.Raptor/RaptorCorpusDocumentId.cs`:

```csharp
namespace Rag.NET.Raptor;

/// <summary>The document id corpus-level RAPTOR summaries are stored under.</summary>
/// <remarks>
/// A corpus summary spans many documents, so there is no real document whose id it could honestly
/// carry. A URI-shaped id rather than a plausible file name, so it cannot collide with a real
/// document and reads as synthetic wherever it surfaces — the same convention, and the same
/// reasoning, as <c>GraphProjectionRebuilder.ReportDocumentId</c> (<c>graphrag://communities</c>).
/// <para>
/// It is also what makes a rebuild cheap: deleting this id through
/// <c>IVectorStore.DeleteByDocumentIdAsync</c> removes exactly the previous tree and nothing else,
/// with no interface change and no store that has to opt in.
/// </para>
/// </remarks>
public static class RaptorCorpusDocumentId
{
    /// <summary>The reserved id value.</summary>
    public const string Value = "raptor://corpus-tree";
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Rag.NET.Raptor.Tests/RaptorCorpusBuildTests.cs`:

```csharp
[Fact]
public async Task CorpusBuild_ProducesATree_OverDocumentsTooShortForPerDocumentScope()
{
    // Each document has 2 chunks — below MinChunksForRaptor (5), so per-document scope
    // builds nothing at all. Corpus scope sees 20 chunks and must build a tree.
    await using var leafStore = new SqliteRaptorLeafStore(":memory:");
    await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

    var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0 };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);

    for (var i = 0; i < 10; i++)
    {
        var ctx = _helpers.CreateContext(chunkCount: 2, documentId: $"doc-{i}");
        await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(new IngestionResult()));
    }

    var final = _helpers.CreateContext(chunkCount: 0, documentId: "trigger");
    var summaryCount = await behavior.BuildCorpusTreeNowAsync(final, TestContext.Current.CancellationToken);

    Assert.True(summaryCount > 0, "corpus scope must build a tree over documents no single one of which qualifies");
    Assert.All(
        final.EmbeddedChunks.Where(c => c.Chunk.Metadata.ContainsKey("raptor_level")),
        c => Assert.Equal(RaptorCorpusDocumentId.Value, c.Chunk.DocumentId.Value));
}

[Fact]
public async Task CorpusSummaries_HaveUniqueChunkIndexes_AcrossEveryLevel()
{
    await using var leafStore = new SqliteRaptorLeafStore(":memory:");
    await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

    var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0 };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
    var ctx = _helpers.CreateContext(chunkCount: 24, documentId: "doc-a");
    await behavior.HandleAsync(ctx, CancellationToken.None, static (_, _) => ValueTask.FromResult(new IngestionResult()));

    var target = _helpers.CreateContext(chunkCount: 0, documentId: "trigger");
    await behavior.BuildCorpusTreeNowAsync(target, TestContext.Current.CancellationToken);

    var indexes = target.EmbeddedChunks.Select(c => c.Chunk.ChunkIndex).ToList();
    Assert.Equal(indexes.Count, indexes.Distinct().Count());
}
```

- [ ] **Step 3: Run them to verify they fail**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~RaptorCorpusBuildTests"
```

Expected: FAIL to compile — `BuildCorpusTreeNowAsync` does not exist.

- [ ] **Step 4: Implement the corpus build and the debounce**

In `RaptorIngestionBehavior`, add the debounce state and the two methods. The `ShouldBuild` shape mirrors `CommunityDetectionBehavior.ShouldDetect` deliberately — including the lock and the sentinel — so that a reader of one recognises the other.

```csharp
    private readonly object _buildGate = new();
    private int _leavesAtLastBuild = -1;

    private bool ShouldBuild(int leafCount)
    {
        lock (_buildGate)
        {
            if (_leavesAtLastBuild < 0 || options.CorpusGrowthThreshold <= 0)
            {
                _leavesAtLastBuild = leafCount;
                return true;
            }

            var required = _leavesAtLastBuild * (1 + options.CorpusGrowthThreshold);
            if (leafCount < required)
                return false;

            _leavesAtLastBuild = leafCount;
            return true;
        }
    }

    /// <summary>
    /// Clusters every stored leaf and appends the resulting summaries to <paramref name="ctx"/>,
    /// regardless of the growth threshold, resetting the threshold's baseline.
    /// </summary>
    /// <remarks>
    /// The entry point <see cref="RaptorTreeRebuilder"/> uses. Deliberately the same code path as
    /// ingestion: a rebuild that clustered its own way would be a second implementation of the
    /// thing under measurement, free to drift from the one that runs during ingest.
    /// </remarks>
    /// <param name="ctx">Receives the summary chunks.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many summaries the build produced; zero when the store holds fewer than two leaves.</returns>
    internal async Task<int> BuildCorpusTreeNowAsync(IngestionContext ctx, CancellationToken ct)
    {
        if (leafStore is null)
            throw new InvalidOperationException("Corpus tree building requires an IRaptorLeafStore.");

        var leaves = await leafStore.GetAllLeavesAsync(ct).ConfigureAwait(false);
        if (leaves.Count < 2)
            return 0;

        lock (_buildGate)
        {
            _leavesAtLastBuild = leaves.Count;
        }

        return await BuildTreeAsync(
            ctx,
            ToEmbeddedChunks(leaves),
            new DocumentId(RaptorCorpusDocumentId.Value),
            firstChunkIndex: 0,
            ct).ConfigureAwait(false);
    }
```

Extract the level loop that `HandleAsync` already runs into a private method both paths call, rather than copying it — a second copy is exactly the drift `DetectNowAsync`'s remarks warn about:

```csharp
    /// <summary>
    /// Runs the level loop over <paramref name="seed"/> and appends every summary produced to
    /// <paramref name="ctx"/>. Shared by the per-document and corpus paths so the two cannot drift.
    /// </summary>
    /// <param name="ctx">Receives the summaries.</param>
    /// <param name="seed">The level-0 chunks to cluster.</param>
    /// <param name="summaryDocumentId">
    /// The document id every summary is filed under. The per-document path passes the ingesting
    /// document's id; the corpus path passes <see cref="RaptorCorpusDocumentId.Value"/>. Explicit
    /// rather than read from <paramref name="ctx"/>, because a corpus summary filed under whichever
    /// document happened to trigger the build is a corpus-level summary attributed to one arbitrary
    /// article — the defect <c>GraphProjectionRebuilder</c>'s remarks describe.
    /// </param>
    /// <param name="firstChunkIndex">The index the first summary takes; it counts up from there across every level (#332).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many summaries were appended.</returns>
    private async Task<int> BuildTreeAsync(
        IngestionContext ctx, List<EmbeddedChunk> seed, DocumentId summaryDocumentId,
        int firstChunkIndex, CancellationToken ct)
    {
        var currentLevel = seed;
        var allSummaries = new List<EmbeddedChunk>();
        var level = 0;
        var nextChunkIndex = firstChunkIndex;

        while (currentLevel.Count > 1 && (options.MaxTreeDepth is null || level < options.MaxTreeDepth))
        {
            level++;
            var summaryChunks = await BuildLevelAsync(currentLevel, ctx, level, nextChunkIndex, ct).ConfigureAwait(false);
            if (summaryChunks is null)
                break;

            nextChunkIndex += summaryChunks.Count;
            allSummaries.AddRange(summaryChunks);
            currentLevel = summaryChunks;
        }

        ctx.EmbeddedChunks.AddRange(allSummaries);
        return allSummaries.Count;
    }
```

`BuildLevelAsync` and `SummarizeClusterAsync` take `summaryDocumentId` too, and `SummarizeClusterAsync`'s `TextChunk` sets `DocumentId = summaryDocumentId` in place of `ctx.Metadata.DocumentId`.

Task 1's loop in `HandleAsync` becomes a call passing the ingesting document's id:

```csharp
        await BuildTreeAsync(
            ctx,
            new List<EmbeddedChunk>(ctx.EmbeddedChunks),
            ctx.Metadata.DocumentId,
            firstChunkIndex: ctx.EmbeddedChunks.Count,
            ct).ConfigureAwait(false);
```

and `BuildCorpusTreeNowAsync`'s final line passes the reserved id:

```csharp
        return await BuildTreeAsync(
            ctx,
            ToEmbeddedChunks(leaves),
            new DocumentId(RaptorCorpusDocumentId.Value),
            firstChunkIndex: 0,
            ct).ConfigureAwait(false);
```

where `ToEmbeddedChunks` maps each `RaptorLeaf` back to an `EmbeddedChunk`, keeping the leaves' own document ids — clustering only reads their vectors, and their identity should not be rewritten:

```csharp
    private static List<EmbeddedChunk> ToEmbeddedChunks(IReadOnlyList<RaptorLeaf> leaves)
    {
        var result = new List<EmbeddedChunk>(leaves.Count);
        foreach (var leaf in leaves)
        {
            result.Add(new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = leaf.Text,
                    DocumentId = new DocumentId(leaf.DocumentId),
                    ChunkIndex = leaf.ChunkIndex,
                },
                Embedding = leaf.Embedding,
            });
        }

        return result;
    }
```

`firstChunkIndex: 0` is correct here and differs from the per-document path deliberately: corpus summaries are filed under `RaptorCorpusDocumentId.Value`, which holds no leaves of its own, so nothing is there to collide with.

Then, in `HandleAsync`'s `Corpus` branch, after `PersistLeavesAsync`:

```csharp
            var leafCount = await leafStore!.CountAsync(ct).ConfigureAwait(false);
            if (ShouldBuild(leafCount))
            {
                var leaves = await leafStore.GetAllLeavesAsync(ct).ConfigureAwait(false);
                if (leaves.Count > 1)
                {
                    await BuildTreeAsync(
                        ctx,
                        ToEmbeddedChunks(leaves),
                        new DocumentId(RaptorCorpusDocumentId.Value),
                        firstChunkIndex: 0,
                        ct).ConfigureAwait(false);
                }
            }

            return await next(ctx, ct).ConfigureAwait(false);
```

**Both paths file summaries under the reserved id, and that is the point of threading `summaryDocumentId` explicitly.** Reading it from `ctx` instead would have filed corpus summaries under whichever document happened to trigger the build — a corpus-level summary attributed to one arbitrary article, which is exactly what `GraphProjectionRebuilder`'s remarks record as the pre-#302 behaviour for community reports. The reserved id is also what makes Task 5's delete-then-store work: it addresses the whole tree and nothing else.

- [ ] **Step 4b: Add the non-reducing-level guard**

`SelectK` returns `k = n` for distinct points (#333), so a level can produce exactly as many
summaries as it consumed. The loop then never terminates, at one LLM call per cluster per level.
The guard goes in `BuildLevelAsync` immediately after `k` is computed — **before** any clustering or
summarisation, so a degenerate level costs nothing rather than being detected after it has been
paid for:

```csharp
        if (k <= 1)
        {
            activity?.SetTag("raptor.cluster.count", 0);
            return null;
        }

        // A level that would produce as many summaries as it consumed cannot terminate the tree
        // loop: the next level clusters the same count into the same count, forever, at one LLM
        // call per cluster per level. Detected here, before any summarisation, so a degenerate
        // level costs nothing. #333 is this exact case — GaussianMixtureModel.SelectK returns
        // k = n for distinct points because a singleton cluster's variance floors to 1e-6 and its
        // log-density then dwarfs the BIC penalty. This guard is deliberately written against the
        // symptom rather than that cause, so a future clustering regression of the same shape is
        // bounded too.
        if (k >= currentLevel.Count)
        {
            activity?.SetTag("raptor.cluster.degenerate", true);
            return null;
        }
```

`HandleAsync` already breaks when `BuildLevelAsync` returns null, so no change is needed there.

- [ ] **Step 4c: Write the termination test**

```csharp
[Fact]
public async Task TreeBuilding_Terminates_AtDefaultOptionsWithNoDepthCap()
{
    // MaxTreeDepth deliberately left at its default null. Before the non-reducing-level guard
    // this hung forever (#333). The timeout is the assertion: a regression reintroducing
    // non-termination fails here rather than wedging the suite.
    _helpers.SetupChatClient("a summary");
    _helpers.SetupEmbedder(dims: 8);
    var ctx = _helpers.CreateContext(chunkCount: 24);
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, new RaptorOptions());

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await behavior.HandleAsync(ctx, cts.Token, static (_, _) => ValueTask.FromResult(
        new IngestionResult { DocumentId = new DocumentId("test-doc"), ChunksStored = 0 }));

    Assert.False(cts.IsCancellationRequested, "tree building did not terminate at default options");
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```
dotnet test tests/Rag.NET.Raptor.Tests
```

Expected: all PASS, including the termination test.

- [ ] **Step 6: Add the debounce test**

```csharp
[Fact]
public async Task CorpusBuild_DoesNotRebuild_UntilTheCorpusGrowsPastTheThreshold()
{
    await using var leafStore = new SqliteRaptorLeafStore(":memory:");
    await leafStore.InitializeAsync(TestContext.Current.CancellationToken);

    var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0.50 };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);

    var first = _helpers.CreateContext(chunkCount: 20, documentId: "doc-0");
    await behavior.HandleAsync(first, CancellationToken.None, static (_, _) => ValueTask.FromResult(new IngestionResult()));
    var callsAfterFirst = _helpers.ChatClient.ReceivedCalls().Count();

    // One more chunk is 5% growth, well under the 50% threshold.
    var second = _helpers.CreateContext(chunkCount: 1, documentId: "doc-1");
    await behavior.HandleAsync(second, CancellationToken.None, static (_, _) => ValueTask.FromResult(new IngestionResult()));

    Assert.Equal(callsAfterFirst, _helpers.ChatClient.ReceivedCalls().Count());
    Assert.DoesNotContain(second.EmbeddedChunks, c => c.Chunk.Metadata.ContainsKey("raptor_level"));
}
```

No new fake is needed: NSubstitute records every call, so `ReceivedCalls().Count()` before and after is the assertion. A summariser that ran would have called the chat client.

- [ ] **Step 7: Run it, then commit**

```
dotnet test tests/Rag.NET.Raptor.Tests
```

```bash
git add src/Rag.NET.Raptor tests/Rag.NET.Raptor.Tests
git commit -m "feat(raptor): build the tree over the whole corpus, debounced on growth (#331)"
```

---

### Task 5: `RaptorTreeRebuilder`

**Files:**
- Create: `src/Rag.NET.Raptor/RaptorTreeRebuilder.cs`
- Modify: `src/Rag.NET.Raptor/RagBuilderExtensions.cs`
- Test: `tests/Rag.NET.Raptor.Tests/RaptorTreeRebuilderTests.cs` (new file)

**Interfaces:**
- Consumes: `RaptorIngestionBehavior.BuildCorpusTreeNowAsync`, `RaptorCorpusDocumentId.Value` from Task 4.
- Produces: `Rag.NET.Raptor.RaptorTreeRebuilder` with `public Task<int> RebuildAsync(CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Rebuild_DeletesThePreviousTreeBeforeStoringTheNewOne()
{
    var vectorStore = Substitute.For<IVectorStore>();
    await using var leafStore = new SqliteRaptorLeafStore(":memory:");
    await leafStore.InitializeAsync(TestContext.Current.CancellationToken);
    await leafStore.AddLeavesAsync(TwentyLeaves(), TestContext.Current.CancellationToken);

    var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
    var rebuilder = new RaptorTreeRebuilder(behavior, vectorStore);

    var count = await rebuilder.RebuildAsync(TestContext.Current.CancellationToken);

    Assert.True(count > 0);
    Received.InOrder(() =>
    {
        vectorStore.DeleteByDocumentIdAsync(RaptorCorpusDocumentId.Value, Arg.Any<CancellationToken>());
        vectorStore.StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>());
    });
}

private static IReadOnlyList<RaptorLeaf> TwentyLeaves()
{
    var rng = new Random(Seed: 42);
    var leaves = new List<RaptorLeaf>(20);
    for (var i = 0; i < 20; i++)
    {
        var vector = new float[8];
        for (var d = 0; d < vector.Length; d++)
            vector[d] = (float)rng.NextDouble();

        leaves.Add(new RaptorLeaf($"doc-{i / 4}", i % 4, $"leaf text {i}", vector));
    }

    return leaves;
}
```

`Received.InOrder` is the assertion that matters: the delete must precede the store, because a rebuild producing fewer summaries than last time would otherwise leave the surplus behind as orphans that retrieval could still return.

- [ ] **Step 2: Run it to verify it fails**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~RaptorTreeRebuilderTests"
```

Expected: FAIL to compile — `RaptorTreeRebuilder` does not exist.

- [ ] **Step 3: Implement the rebuilder**

Model it directly on `src/Rag.NET.GraphRag/GraphProjectionRebuilder.cs` — read that file first and follow its structure, its `IngestionContext` construction, and its XML-doc depth.

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;

namespace Rag.NET.Raptor;

/// <summary>Rebuilds the corpus-level RAPTOR tree on demand, over every stored leaf.</summary>
/// <remarks>
/// <para>
/// Ingestion debounces tree building on corpus growth
/// (<see cref="RaptorOptions.CorpusGrowthThreshold"/>), which keeps the ingest cheap and leaves the
/// tree up to that fraction stale. This type is the other half of that trade: the way to say "make
/// it current now" — after a bulk load, before measuring, or on a schedule.
/// </para>
/// <para>
/// <b>The old tree is deleted before the new one is stored.</b> Clustering is not stable across
/// runs and may return fewer summaries than last time, so without the delete the surplus would
/// remain as orphans that retrieval could still return. Deleting
/// <see cref="RaptorCorpusDocumentId.Value"/> touches nothing else.
/// </para>
/// <para>
/// Not safe to run concurrently with itself against one store: two rebuilds would interleave a
/// delete with the other's store. Callers scheduling this should serialise it.
/// </para>
/// </remarks>
/// <param name="behavior">The tree-building implementation, shared with the ingestion path.</param>
/// <param name="vectorStore">Where the summary chunks are written.</param>
public sealed class RaptorTreeRebuilder(RaptorIngestionBehavior behavior, IVectorStore vectorStore)
{
    /// <summary>Rebuilds the tree over every stored leaf and replaces the stored summaries.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many summaries the rebuild produced; zero when the corpus holds fewer than two leaves.</returns>
    public async Task<int> RebuildAsync(CancellationToken cancellationToken = default)
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(RaptorCorpusDocumentId.Value),
                FileName = RaptorCorpusDocumentId.Value,
            },
            GetNextBm25DocId = () => 0,
        };

        var count = await behavior.BuildCorpusTreeNowAsync(ctx, cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            return 0;
        }

        await vectorStore
            .DeleteByDocumentIdAsync(RaptorCorpusDocumentId.Value, cancellationToken)
            .ConfigureAwait(false);

        await vectorStore
            .StoreAsync(ctx.EmbeddedChunks, cancellationToken)
            .ConfigureAwait(false);

        return count;
    }
}
```

This mirrors `GraphProjectionRebuilder.RebuildAsync` line for line, including the early return before the delete — a rebuild that produced nothing must not delete the tree that is already there.

- [ ] **Step 4: Run the test to verify it passes**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~RaptorTreeRebuilderTests"
```

Expected: PASS.

- [ ] **Step 5: Add the baseline-reset test**

```csharp
[Fact]
public async Task Rebuild_ResetsTheGrowthBaseline_SoLaterIngestsDebounceFromTheRebuiltState()
{
    var vectorStore = Substitute.For<IVectorStore>();
    await using var leafStore = new SqliteRaptorLeafStore(":memory:");
    await leafStore.InitializeAsync(TestContext.Current.CancellationToken);
    await leafStore.AddLeavesAsync(TwentyLeaves(), TestContext.Current.CancellationToken);

    var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0.50 };
    var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
    var rebuilder = new RaptorTreeRebuilder(behavior, vectorStore);

    await rebuilder.RebuildAsync(TestContext.Current.CancellationToken);
    var callsAfterRebuild = _helpers.ChatClient.ReceivedCalls().Count();

    // The rebuild set the baseline to 20. Two more leaves is 22, under the 30 the
    // 50% threshold requires, so ingesting them must not trigger another build.
    var next = _helpers.CreateContext(chunkCount: 2, documentId: "doc-late");
    await behavior.HandleAsync(next, CancellationToken.None, static (_, _) => ValueTask.FromResult(new IngestionResult()));

    Assert.Equal(callsAfterRebuild, _helpers.ChatClient.ReceivedCalls().Count());
}
```

Without the baseline reset in `BuildCorpusTreeNowAsync`, `_leavesAtLastBuild` would still be `-1` after the rebuild, the sentinel would report "build now", and this test fails — which is the whole reason the reset is in that method.

- [ ] **Step 6: Register the rebuilder, run everything, commit**

In `RagBuilderExtensions.UseRaptor`, register `RaptorTreeRebuilder` as a singleton when a leaf store path was supplied. Follow how `Rag.NET.GraphRag/RagBuilderExtensions.cs` registers `GraphProjectionRebuilder` — including its comment about the behaviour needing to be the *same singleton instance* that ingestion uses, since that instance holds the debounce baseline.

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Raptor.Tests tests/Rag.NET.Raptor.Store.Tests
```

```bash
git add src/Rag.NET.Raptor tests/Rag.NET.Raptor.Tests
git commit -m "feat(raptor): add RaptorTreeRebuilder for on-demand corpus rebuilds (#331)"
```

---

### Task 6: Fix #333 — `SelectK` returns k=n, so clustering never reduces

Task 4's guard stops the loop; it does not make clustering work. At small counts `SelectK` still
isolates every point into its own component, so the tree is one level of near-duplicates rather
than a hierarchy. This task fixes the cause, before Task 7 makes `Corpus` the shipped default.

**Files:**
- Modify: `src/Rag.NET.Raptor/Math/GaussianMixtureModel.cs`
- Test: `tests/Rag.NET.Raptor.Tests/Math/GaussianMixtureModelTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks. `SelectK` and `Fit` are `internal`, visible to the test project.
- Produces: no signature change. `SelectK(float[][] data, int maxK, int maxIterations = 100)` keeps its shape; only what it returns changes.

- [ ] **Step 1: Write the failing test**

Add to `tests/Rag.NET.Raptor.Tests/Math/GaussianMixtureModelTests.cs`, following the existing file's style:

```csharp
[Fact]
public void SelectK_DoesNotIsolateEveryPoint_OnDistinctData()
{
    // #333: a singleton cluster's variance floors to VarianceFloor (1e-6), so its Gaussian
    // log-density at its own mean is -0.5*d*ln(2*pi*1e-6) ~ +47.9 nats at d=8. Through
    // -2*logLikelihood that is ~95.8 of BIC gain per isolated point, against a penalty of only
    // 17*ln(n) ~ 39.1 at n=10. Splitting always won, so SelectK returned k = n for every n
    // from 2 to 10, and the tree loop could never reduce its level count.
    var rng = new Random(Seed: 7);
    for (var n = 2; n <= 10; n++)
    {
        var data = new float[n][];
        for (var i = 0; i < n; i++)
        {
            data[i] = new float[8];
            for (var d = 0; d < 8; d++)
                data[i][d] = (float)rng.NextDouble();
        }

        var k = GaussianMixtureModel.SelectK(data, maxK: System.Math.Min(n, 10));

        Assert.True(k < n, $"n={n}: SelectK returned k={k}; k must be below n or no tree level can ever reduce");
    }
}

[Fact]
public void SelectK_StillSeparates_WellSeparatedClusters()
{
    // The fix must not buy termination by making SelectK useless. Two tight, far-apart blobs
    // must still be found as two clusters.
    var rng = new Random(Seed: 11);
    var data = new float[20][];
    for (var i = 0; i < 20; i++)
    {
        data[i] = new float[8];
        var offset = i < 10 ? 0.0f : 10.0f;
        for (var d = 0; d < 8; d++)
            data[i][d] = offset + (float)(rng.NextDouble() * 0.01);
    }

    var k = GaussianMixtureModel.SelectK(data, maxK: 10);

    Assert.True(k >= 2, $"SelectK returned k={k}; two well-separated blobs must yield at least 2 clusters");
}
```

- [ ] **Step 2: Run them to verify the first fails and the second passes**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~SelectK_"
```

Expected: `SelectK_DoesNotIsolateEveryPoint_OnDistinctData` FAILS at n=2 (`k=2`).
`SelectK_StillSeparates_WellSeparatedClusters` should PASS both before and after your change — it
is the guard against a fix that trivially returns 1.

If the second test fails *before* your change, stop and report: the defect is wider than #333
describes and the fix needs re-scoping.

- [ ] **Step 3: Fix the cause**

The bug is that a degenerate component is scored as an excellent one. Two changes, both in
`GaussianMixtureModel.cs`:

**(a) Raise the variance floor to something physically meaningful.** `VarianceFloor = 1e-6` on
unit-scale embedding data means a standard deviation of 0.001 — far tighter than any real cluster,
so an isolated point always wins. Scale the floor to the data instead of hard-coding it: compute the
mean per-dimension variance of the whole dataset once in `Fit`, and floor each component's variance
at a small fraction of it (start with 1/100th) rather than at an absolute constant.

**(b) Refuse to score components that own fewer than two points.** In `SelectK`, skip any `k` whose
fitted result leaves a component with `nk < 2` — such a `k` is not a real candidate, and BIC has no
way to express that. Determine `nk` from the responsibilities the fit returns.

Implement (b) first and re-run: it alone may be sufficient, and it is the smaller change. Only add
(a) if the test still fails. **Report which of the two you needed** — that answer matters more than
the code, because it tells us whether the floor is the cause or merely an amplifier.

- [ ] **Step 4: Run both tests to verify they pass**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~SelectK_"
```

Expected: both PASS.

- [ ] **Step 5: Run the full suite**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Raptor.Tests
```

Expected: all PASS. **Task 1's `SummaryChunks_HaveUniqueChunkIndexes_AcrossEveryTreeLevel` and
Task 4's tests depend on the tree reaching specific depths, so a clustering change can move them.**
If one fails because the tree now has a different shape, that is the fix working — adjust the leaf
count or the depth guard to restore a ≥2-level tree, and say exactly what moved in your report. Do
NOT weaken an assertion to make it pass.

- [ ] **Step 6: Commit**

```bash
git add src/Rag.NET.Raptor/Math/GaussianMixtureModel.cs tests/Rag.NET.Raptor.Tests/Math/GaussianMixtureModelTests.cs
git commit -m "fix(raptor): SelectK no longer isolates every point into its own cluster (#333)"
```

---

### Task 7: Flip the default, document the migration, close the issues

The breaking change lands last, once everything it depends on works.

**Files:**
- Modify: `src/Rag.NET.Raptor/RaptorOptions.cs`
- Modify: `src/Rag.NET.Raptor/Rag.NET.Raptor.csproj` (`VerifiedBy`)
- Modify: `docs/guide/raptor.md`
- Modify: `docs/reference/features.md`
- Test: `tests/Rag.NET.Raptor.Tests/RaptorTreeScopeTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3–5.
- Produces: no new API.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void TreeScope_DefaultsToCorpus()
{
    Assert.Equal(RaptorTreeScope.Corpus, new RaptorOptions().TreeScope);
}
```

- [ ] **Step 2: Run it to verify it fails**

```
dotnet test tests/Rag.NET.Raptor.Tests --filter "FullyQualifiedName~TreeScope_DefaultsToCorpus"
```

Expected: FAIL — `PerDocument` is still the default.

- [ ] **Step 3: Flip the default**

In `RaptorOptions.cs`:

```csharp
    /// <summary>What set of chunks the tree is built over. Default: <see cref="RaptorTreeScope.Corpus"/>.</summary>
    /// <remarks>
    /// <b>The default changed in v1.0 and this is a breaking change.</b> It was
    /// <see cref="RaptorTreeScope.PerDocument"/>, which cannot produce a summary spanning two
    /// documents and therefore is not the mechanism the RAPTOR paper describes (#331).
    /// <see cref="RaptorTreeScope.PerDocument"/> remains fully supported.
    /// </remarks>
    public RaptorTreeScope TreeScope { get; set; } = RaptorTreeScope.Corpus;
```

- [ ] **Step 4: Run the whole suite**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Raptor.Tests tests/Rag.NET.Raptor.Store.Tests tests/Rag.NET.RepoConventions.Tests
```

Expected: all PASS. Tests that constructed `RaptorOptions` without setting `TreeScope` and expected per-document trees now need `TreeScope = RaptorTreeScope.PerDocument` set explicitly. Fix them — the explicit value is more honest than a test that silently depended on a default.

- [ ] **Step 5: Update the RAPTOR guide**

In `docs/guide/raptor.md`:
- Replace "questions about the overall theme of **a document**" with the corpus-level description.
- Add a **Tree scope** section covering both values and when to choose `PerDocument`.
- State the behaviour change plainly: **ingesting one document no longer produces a tree immediately** — summaries appear once the corpus crosses `CorpusGrowthThreshold` or `RaptorTreeRebuilder.RebuildAsync` is called.
- Add a **Migration** section: summaries written by the previous per-document default are stale under the new default and will compete for rank. The step is to delete the RAPTOR summary chunks and re-ingest, or call `RaptorTreeRebuilder.RebuildAsync` after clearing them. There is no automatic cleanup, and say why: old summaries carry `raptor_level` and a real `DocumentId`, so a heuristic that guessed wrong would delete real data.
- Note that `Rag.NET.Raptor.Store` is required for `Corpus` scope.

- [ ] **Step 6: Update the ledger**

In `src/Rag.NET.Raptor/Rag.NET.Raptor.csproj`, raise `<VerifiedBy>unit</VerifiedBy>` to `integration`, with a comment naming what exercises it — the corpus build over a real SQLite leaf store surviving a reopen. Do **not** claim `benchmark`: that level means a measured run on a real corpus pinned in a reproduction table, and that is Phase 6.2.1's RAPTOR thread, not this one.

In `docs/reference/features.md`, update the RAPTOR row's *Exercised by* pointer.

- [ ] **Step 7: Run everything, commit, and reference both issues**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.RepoConventions.Tests
```

```bash
git add src/Rag.NET.Raptor docs/guide/raptor.md docs/reference/features.md tests/Rag.NET.Raptor.Tests
git commit -m "feat(raptor)!: cluster over the corpus by default (#331)"
```

The `!` is required — `RaptorOptions.TreeScope`'s default changes observable behaviour for every existing RAPTOR user. The commit body must carry a `BREAKING CHANGE:` paragraph describing the migration, since release-please builds the changelog from it.

---

## Notes for the executor

**What is deliberately not in this plan.** The `Boost` and `Filter` over-fetch defects — `Boost` cannot promote a summary into the result set, `Filter` under-fills — belong to `docs/superpowers/specs/2026-08-20-raptor-real-protocol-design.md` and Phase 6.2.1. Do not fix them here, and do not "improve" `RaptorRetrievalBehavior` while passing through. Measuring them as shipped is the point.

**`PerDocument` must keep working.** It is the control arm Phase 6.2.1 differences the corpus scope against. If a change makes per-document trees harder to construct in a test, that is a signal to stop and reconsider, not to delete the path.

**If the clustering produces one level where a test needs two**, raise the leaf count rather than lowering the assertion. A test that passes because the tree was trivial is the failure mode Task 1 exists to prevent.
