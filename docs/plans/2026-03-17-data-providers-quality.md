# Data Providers Quality — Docs, Tests, Benchmarks

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Close the three gaps identified by the post-merge audit: missing RSS doc example, missing test coverage for error resilience and URL normalisation, and zero benchmark coverage for the new provider ingestion path.

**Architecture:** Pure additions — one doc section, three new test methods across two existing test files, one new benchmark file. Nothing is modified in source code.

**Tech Stack:** C# / xUnit v3 / NSubstitute / BenchmarkDotNet. All new code lives in the worktree at `.worktrees/docs-tests-benchmarks`.

---

### Task 1: Add `RssDataProvider` doc example

**Files:**
- Modify: `docs/guide/ingestion.md` (between the `SitemapDataProvider` and `WebCrawlerDataProvider` headings)

**Step 1: Locate insertion point**

Open `docs/guide/ingestion.md`. Find the line `### \`WebCrawlerDataProvider\`` (around line 248). Insert the new section immediately before it.

**Step 2: Insert the RSS section**

Add exactly this text between the Sitemap block and the WebCrawler heading:

```markdown
### `RssDataProvider`

```csharp
var provider = new RssDataProvider("https://example.com/feed.rss", httpClient);
await pipeline.IngestFromProviderAsync(provider, "blog-feed", hashStore: hashStore);
```

Supports RSS 2.0 and Atom feeds. `Id` is the `<guid>` or `<link>` element; `ETag` is `<pubDate>` / `<updated>` — so unchanged posts are automatically skipped on subsequent runs.
```

**Step 3: Verify the file renders correctly**

Check that the four provider sections (LocalFiles, Sitemap, RSS, WebCrawler, GitHub) all appear in order with no blank section headings.

**Step 4: Commit**

```bash
cd .worktrees/docs-tests-benchmarks
git add docs/guide/ingestion.md
git commit -m "docs: add RssDataProvider usage example to ingestion guide"
```

---

### Task 2: Test — `IngestFromProviderAsync` error resilience

**Files:**
- Modify: `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs` (append one test)

**Context:** `ProcessEntryAsync` wraps the entire per-file work in `catch (Exception ex) when (ex is not OperationCanceledException)`. A throw from `IngestAsync` is caught, appended to `result.Errors` as `"{entryId}: {message}"`, and the entry counts as skipped. The next entry is still processed. This is the error resilience guarantee — it must be tested.

**Step 1: Write the failing test**

Append this test to the `IngestFromProviderTests` class:

```csharp
[Fact]
public async Task IngestFromProviderAsync_IngestThrows_AppendsToErrorsAndContinues()
{
    // id-1 throws, id-2 succeeds — both must be attempted
    _pipeline.IngestAsync(
            Arg.Any<Stream>(),
            Arg.Is<DocumentMetadata>(m => string.Equals(m.DocumentId, "id-1", StringComparison.Ordinal)),
            Arg.Any<IngestionOptions?>(),
            Arg.Any<IProgress<IngestionProgress>?>(),
            Arg.Any<CancellationToken>())
        .Returns(Task.FromException<IngestionResult>(new InvalidOperationException("simulated failure")));

    _pipeline.IngestAsync(
            Arg.Any<Stream>(),
            Arg.Is<DocumentMetadata>(m => string.Equals(m.DocumentId, "id-2", StringComparison.Ordinal)),
            Arg.Any<IngestionOptions?>(),
            Arg.Any<IProgress<IngestionProgress>?>(),
            Arg.Any<CancellationToken>())
        .Returns(new IngestionResult { DocumentId = "id-2", ChunksStored = 1 });

    var provider = MakeProvider(
        ("id-1", "fail.txt", "hello", null),
        ("id-2", "ok.txt",   "world", null));

    var result = await _pipeline.IngestFromProviderAsync(provider, "prov",
        cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal(1, result.Ingested);                                     // id-2 ingested
    Assert.Equal(1, result.Skipped);                                      // id-1 failure counted as skipped
    Assert.Single(result.Errors);
    Assert.Contains("id-1", result.Errors[0], StringComparison.Ordinal); // error message names the entry
}
```

**Step 2: Run to verify it fails**

```bash
cd .worktrees/docs-tests-benchmarks
dotnet test tests/Rag.NET.Tests --no-build -q --filter "IngestFromProviderAsync_IngestThrows"
```

Expected: compile error (test doesn't exist yet). After adding: PASS immediately (logic is already in source). If it fails, check NSubstitute's `Task.FromException<T>` syntax.

**Step 3: Run all IngestFromProvider tests to verify no regressions**

```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "FullyQualifiedName~IngestFromProviderTests"
```

Expected: 7 tests passing (6 existing + 1 new).

**Step 4: Commit**

```bash
git add tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs
git commit -m "test: verify IngestFromProviderAsync appends errors and continues on per-file failure"
```

---

### Task 3: Tests — `WebCrawlerDataProvider` URL normalisation and HTTP errors

**Files:**
- Modify: `tests/Rag.NET.DataProviders.Web.Tests/WebCrawlerDataProviderTests.cs` (append two tests)

**Context:**
- `ExtractLinks` strips fragments via `new UriBuilder(uri) { Fragment = string.Empty }` — so `/page#section1` and `/page#section2` both normalise to `https://example.com/page`. The `visited` set then deduplicates them.
- HTTP errors (`HttpRequestException`) from `GetStringAsync` are caught per-page and silently skipped. (In .NET 5+, `GetStringAsync` throws `HttpRequestException` for non-2xx responses.) The `FakeHttpMessageHandler` returns `HttpStatusCode.NotFound` for unknown URLs, which triggers this path.

**Step 1: Write the two failing tests**

Append both tests to `WebCrawlerDataProviderTests`:

```csharp
[Fact]
public async Task GetFilesAsync_FragmentUrls_TreatedAsSamePage()
{
    // Two anchors pointing to /page with different fragments both normalise to /page
    var responses = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SeedUrl] = """
            <html><body>
              <a href="/page#section1">S1</a>
              <a href="/page#section2">S2</a>
            </body></html>
            """,
        ["https://example.com/page"] = "<html><body>Page content</body></html>",
    };
    var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(responses),
        new WebCrawlerOptions { RespectRobotsTxt = false });

    var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
        .ToListAsync(TestContext.Current.CancellationToken);

    // seed + /page (both fragment variants deduplicated → only one /page entry)
    Assert.Equal(2, entries.Count);
    Assert.Contains(entries, e => string.Equals(e.Id, SeedUrl, StringComparison.Ordinal));
    Assert.Contains(entries, e => string.Equals(e.Id, "https://example.com/page", StringComparison.Ordinal));
}

[Fact]
public async Task GetFilesAsync_HttpError_SkipsPageAndContinues()
{
    // /exists is in the fake server; /missing is not → 404 → HttpRequestException → silently skipped
    var responses = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SeedUrl] = """
            <html><body>
              <a href="/exists">Exists</a>
              <a href="/missing">Missing</a>
            </body></html>
            """,
        ["https://example.com/exists"] = "<html><body>Exists page</body></html>",
        // /missing intentionally absent → FakeHttpMessageHandler returns 404
    };
    var sut = new WebCrawlerDataProvider(SeedUrl, MakeClient(responses),
        new WebCrawlerOptions { RespectRobotsTxt = false });

    var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
        .ToListAsync(TestContext.Current.CancellationToken);

    Assert.Equal(2, entries.Count); // seed + /exists; /missing is skipped
    Assert.DoesNotContain(entries, e => e.Id.Contains("missing", StringComparison.Ordinal));
}
```

**Step 2: Run to verify**

```bash
cd .worktrees/docs-tests-benchmarks
dotnet test tests/Rag.NET.DataProviders.Web.Tests --no-build -q
```

Expected: all 8 tests passing (6 existing + 2 new).

**Step 3: Commit**

```bash
git add tests/Rag.NET.DataProviders.Web.Tests/WebCrawlerDataProviderTests.cs
git commit -m "test: add WebCrawlerDataProvider fragment deduplication and HTTP error skip tests"
```

---

### Task 4: Benchmarks — `ProviderIngestionBenchmarks`

**Files:**
- Create: `benchmarks/Rag.NET.Benchmarks/ProviderIngestionBenchmarks.cs`
- No new project references needed — `Rag.NET.Benchmarks.csproj` already references `Rag.NET` core which contains `LocalFilesDataProvider`, `SqliteContentHashStore`, and `RagPipelineExtensions`.

**Context:** Three benchmark scenarios:
1. **NoStore** — baseline; every file is read and ingested (NoOp pipeline), no hash overhead.
2. **WarmStore_AllSkipped** — all files have matching ETags in the store; every entry is skipped at ETag level (cheapest path, no content fetch).
3. **ColdStore_AllNew** — store is empty; every file is read, SHA-256 hashed, and ingested (most expensive path).

`[IterationSetup]` and `[IterationCleanup]` ensure the cold-store DB starts empty on every iteration so results are reproducible.

The `NoOpRagPipeline` is a private nested class that completes all operations without doing real work (same pattern as `NoOpVectorStore` in `PipelineBenchmarks.cs`).

**Step 1: Create the file**

```csharp
using BenchmarkDotNet.Attributes;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Measures <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> throughput across
/// three deduplication scenarios: no store (baseline), warm ETag cache (all skipped),
/// and cold store (all new files hashed and ingested).
/// </summary>
[MemoryDiagnoser]
public class ProviderIngestionBenchmarks
{
    private string _tempDir = null!;
    private string _warmDbPath = null!;
    private string _coldDbPath = null!;
    private IRagPipeline _pipeline = null!;

    [Params(20)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _pipeline = new NoOpRagPipeline();

        // Create a temp directory with FileCount small text files
        _tempDir = Path.Combine(Path.GetTempPath(), $"ragnet-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        for (var i = 0; i < FileCount; i++)
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"doc{i:D4}.txt"),
                $"Content of document {i}. The quick brown fox jumps over the lazy dog.").ConfigureAwait(false);

        // Warm store: pre-populate with current ETags so all entries will be skipped
        _warmDbPath = Path.Combine(Path.GetTempPath(), $"ragnet-bench-warm-{Guid.NewGuid():N}.db");
        var warmStore = new SqliteContentHashStore(_warmDbPath);
        var seedProvider = new LocalFilesDataProvider(_tempDir);
        await foreach (var entry in seedProvider.GetFilesAsync().ConfigureAwait(false))
            await warmStore.SetAsync("bench", entry.Id, entry.ETag, "placeholder-hash").ConfigureAwait(false);

        _coldDbPath = Path.Combine(Path.GetTempPath(), $"ragnet-bench-cold-{Guid.NewGuid():N}.db");
    }

    [IterationSetup(Target = nameof(IngestFromProviderAsync_ColdStore_AllNew))]
    public void ColdSetup()
    {
        // Delete and recreate the cold DB so every iteration starts with an empty store
        if (File.Exists(_coldDbPath)) File.Delete(_coldDbPath);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        if (File.Exists(_warmDbPath)) File.Delete(_warmDbPath);
        if (File.Exists(_coldDbPath)) File.Delete(_coldDbPath);
    }

    /// <summary>Baseline: no hash store, every file is ingested unconditionally.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> IngestFromProviderAsync_NoStore()
    {
        var provider = new LocalFilesDataProvider(_tempDir);
        var result = await _pipeline.IngestFromProviderAsync(provider, "bench").ConfigureAwait(false);
        return result.Ingested;
    }

    /// <summary>Warm cache: all ETags match → every file skipped without reading content.</summary>
    [Benchmark]
    public async Task<int> IngestFromProviderAsync_WarmStore_AllSkipped()
    {
        var provider = new LocalFilesDataProvider(_tempDir);
        var store = new SqliteContentHashStore(_warmDbPath);
        var result = await _pipeline.IngestFromProviderAsync(provider, "bench", hashStore: store).ConfigureAwait(false);
        return result.Skipped;
    }

    /// <summary>Cold store: every file is read, SHA-256 hashed, and ingested.</summary>
    [Benchmark]
    public async Task<int> IngestFromProviderAsync_ColdStore_AllNew()
    {
        var provider = new LocalFilesDataProvider(_tempDir);
        var store = new SqliteContentHashStore(_coldDbPath);
        var result = await _pipeline.IngestFromProviderAsync(provider, "bench", hashStore: store).ConfigureAwait(false);
        return result.Ingested;
    }

    private sealed class NoOpRagPipeline : IRagPipeline
    {
        public Task<IngestionResult> IngestAsync(
            Stream document,
            DocumentMetadata metadata,
            IngestionOptions? options = null,
            IProgress<IngestionProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 1 });

        public Task<IReadOnlyList<SearchResult>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>([]);

        public Task<RagResponse> AskAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
```

**Step 2: Build to verify it compiles**

```bash
cd .worktrees/docs-tests-benchmarks
dotnet build benchmarks/Rag.NET.Benchmarks -c Release -q
```

Expected: Build succeeded, 0 errors.

**Step 3: Smoke-test with a dry run (no actual benchmarking)**

```bash
dotnet run --project benchmarks/Rag.NET.Benchmarks -c Release -- --filter "*ProviderIngestion*" --list tests
```

Expected: Lists the three benchmark methods.

**Step 4: Commit**

```bash
git add benchmarks/Rag.NET.Benchmarks/ProviderIngestionBenchmarks.cs
git commit -m "bench: add ProviderIngestionBenchmarks for no-store, warm-cache, and cold-store scenarios"
```

---

### Final verification

Run the full test suite to confirm nothing is broken:

```bash
cd .worktrees/docs-tests-benchmarks
dotnet test -q 2>&1 | grep -E "(Passed!|Failed!|failed)"
```

Expected: all pass, 0 failures, 1 pre-existing AzureAISearch skip.
