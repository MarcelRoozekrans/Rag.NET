# Code Review Fixes — Critical & Important Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix all 3 Critical and 8 Important findings from the full-source code review.

**Architecture:** Pure bug-fix pass — no new features, no API surface changes beyond `OnnxRerankerOptions.VocabPath` (new required property) and `SqliteBm25Index/SqliteParentChunkStore.InitializeAsync` (new public method). Each task is one isolated fix with a targeted test and a commit.

**Tech Stack:** C# / .NET 10 / xUnit v3 / NSubstitute. `TreatWarningsAsErrors=true`, `MA0006`/`MA0002` analyzers active. Always use `TestContext.Current.CancellationToken` on async test calls.

---

### Task 1 — C2: PgVector table name injection

**Files:**
- Modify: `src/Rag.NET.PgVector/PgVectorStore.cs`
- Modify: `tests/Rag.NET.PgVector.Tests/PgVectorStoreTests.cs`

**Context:** `CreateCollectionAsync` (lines 188, 203) and `DeleteCollectionAsync` (line 216) interpolate the caller-supplied `name` directly into DDL SQL. A name like `foo; DROP TABLE rag_chunks; --` would execute arbitrary SQL. Fix: validate `name` against a strict pattern and double-quote it as a PostgreSQL identifier.

**Step 1: Write the failing tests** — append to `PgVectorStoreTests`:

```csharp
[Theory]
[InlineData("foo; DROP TABLE rag_chunks; --")]
[InlineData("FOO")]
[InlineData("1starts_with_digit")]
[InlineData("has space")]
[InlineData("has-hyphen")]
public async Task CreateCollectionAsync_InvalidName_ThrowsArgumentException(string name)
{
    await Assert.ThrowsAsync<ArgumentException>(
        () => _sut.CreateCollectionAsync(name, 3, TestContext.Current.CancellationToken));
}

[Theory]
[InlineData("foo; DROP TABLE rag_chunks; --")]
[InlineData("1bad")]
public async Task DeleteCollectionAsync_InvalidName_ThrowsArgumentException(string name)
{
    await Assert.ThrowsAsync<ArgumentException>(
        () => _sut.DeleteCollectionAsync(name, TestContext.Current.CancellationToken));
}
```

**Step 2: Run tests to verify they fail**
```bash
cd c:/Projects/Prive/Rag.NET
dotnet test tests/Rag.NET.PgVector.Tests --no-build -q --filter "InvalidName_ThrowsArgumentException"
```
Expected: FAIL — no validation exists yet.

**Step 3: Add `ValidateAndQuoteIdentifier` to `PgVectorStore`** — add this private static method anywhere in the class:

```csharp
/// <summary>
/// Validates that <paramref name="name"/> is a safe PostgreSQL identifier
/// (lowercase letters, digits, underscores; max 63 chars; must start with letter or underscore)
/// and returns it double-quoted for safe use in DDL statements.
/// </summary>
private static string ValidateAndQuoteIdentifier(string name)
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(
            name, @"^[a-z_][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.None))
        throw new ArgumentException(
            $"Collection name '{name}' is invalid. Use only lowercase letters, digits, and underscores " +
            "(max 63 chars, must start with a letter or underscore).",
            nameof(name));
    return $"\"{name}\"";
}
```

**Step 4: Apply to `CreateCollectionAsync`** — replace the two interpolated SQL strings (lines 187–196 and 203):

```csharp
// At the start of CreateCollectionAsync, before any SQL:
var quotedName = ValidateAndQuoteIdentifier(name);

// Replace $$"""CREATE TABLE IF NOT EXISTS {{name}} ...""" with:
var sql = $$"""
    CREATE TABLE IF NOT EXISTS {{quotedName}} (
        id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        document_id TEXT NOT NULL,
        chunk_index INTEGER NOT NULL,
        text TEXT NOT NULL,
        metadata JSONB NOT NULL DEFAULT '{}',
        embedding vector({{vectorDimensions}}) NOT NULL
    )
    """;

// Replace the index command string ($"CREATE INDEX IF NOT EXISTS idx_{name}..."):
var indexCmd = new NpgsqlCommand(
    $"CREATE INDEX IF NOT EXISTS \"idx_{name}_document_id\" ON {quotedName} (document_id)", conn);
```

**Step 5: Apply to `DeleteCollectionAsync`** — replace line 216:

```csharp
// At the start of DeleteCollectionAsync:
var quotedName = ValidateAndQuoteIdentifier(name);

// Replace:
var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS {quotedName}", conn);
```

**Step 6: Run tests**
```bash
dotnet test tests/Rag.NET.PgVector.Tests --no-build -q --filter "InvalidName_ThrowsArgumentException"
```
Expected: PASS.

**Step 7: Commit**
```bash
git add src/Rag.NET.PgVector/PgVectorStore.cs tests/Rag.NET.PgVector.Tests/PgVectorStoreTests.cs
git commit -m "fix: validate and quote PostgreSQL collection name to prevent DDL injection"
```

---

### Task 2 — I5: AzureAISearch OData filter injection

**Files:**
- Modify: `src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs`
- Modify: `tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`

**Context:** Metadata key/value strings are interpolated directly into OData `search.ismatch(...)` filter expressions in both `SearchAsync` (line 122) and `HybridSearchAsync` (line 158). A value containing a single quote (`'`) produces a malformed OData expression. Fix: escape single quotes by doubling them before interpolation.

**Step 1: Read existing test file** to understand import style:
```bash
head -20 tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs
```

**Step 2: Write a failing test** — append to `AzureAISearchVectorStoreTests`:

```csharp
[Fact]
public void BuildMetadataFilter_ValueWithSingleQuote_EscapedCorrectly()
{
    // The escaping must turn ' into '' in the OData literal
    // We test via the produced filter string: verify it doesn't contain unescaped '
    // Reflection-free: confirm the escaping helper produces the right output directly.
    const string raw = "it's here";
    const string expected = "it''s here";
    Assert.Equal(expected, AzureAISearchVectorStore.EscapeODataString(raw));
}
```

> **Note:** `EscapeODataString` does not exist yet. Make it `internal static` so the test can reach it (the test project should already have `InternalsVisibleTo` or you may need to add it — check the `.csproj`).

**Step 3: Check for `InternalsVisibleTo`**
```bash
grep -r "InternalsVisibleTo" src/Rag.NET.AzureAISearch/
```
If missing, add to `src/Rag.NET.AzureAISearch/Rag.NET.AzureAISearch.csproj`:
```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
    <_Parameter1>Rag.NET.AzureAISearch.Tests</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

**Step 4: Run test to verify it fails**
```bash
dotnet test tests/Rag.NET.AzureAISearch.Tests --no-build -q --filter "EscapeODataString"
```
Expected: FAIL — method does not exist yet.

**Step 5: Add `EscapeODataString` to `AzureAISearchVectorStore`** and apply it:

```csharp
/// <summary>Escapes a string for use in an OData string literal by doubling single quotes.</summary>
internal static string EscapeODataString(string value) =>
    value.Replace("'", "''", StringComparison.Ordinal);
```

In both `SearchAsync` and `HybridSearchAsync`, replace the filter-building LINQ (appears identically in both):

```csharp
// OLD:
.Select(kvp => $"search.ismatch('\"{kvp.Key}\":\"{kvp.Value}\"', 'metadata')")

// NEW:
.Select(kvp =>
    $"search.ismatch('\"{EscapeODataString(kvp.Key)}\":\"{EscapeODataString(kvp.Value)}\"', 'metadata')")
```

**Step 6: Run tests**
```bash
dotnet test tests/Rag.NET.AzureAISearch.Tests --no-build -q
```
Expected: all pass.

**Step 7: Commit**
```bash
git add src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs \
        src/Rag.NET.AzureAISearch/Rag.NET.AzureAISearch.csproj \
        tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs
git commit -m "fix: escape single quotes in AzureAISearch OData metadata filter to prevent filter injection"
```

---

### Task 3 — C3: AzureAISearch delete pagination

**Files:**
- Modify: `src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs`
- Modify: `tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`

**Context:** `DeleteByDocumentIdAsync` (lines 170–193) fetches at most 1000 chunk IDs and deletes them. Documents with more than 1000 chunks (large documents with small `MaxChunkSize`) have leftover chunks silently orphaned after deletion. Fix: loop until fewer than 1000 IDs are returned in a batch.

**Step 1: Write a failing test** — append to `AzureAISearchVectorStoreTests`. Read the file first to understand how the mock search client is set up, then write:

```csharp
[Fact]
public async Task DeleteByDocumentIdAsync_MoreThan1000Chunks_DeletesAllPages()
{
    // Simulate a document with 1001 chunks stored.
    // First fetch returns 1000 IDs (full page → must re-fetch).
    // Second fetch returns 1 ID (partial page → stop).
    // Verify IndexDocumentsAsync is called twice (once per page).

    // ... (implement using NSubstitute mocks matching the existing test patterns in the file)
}
```

> Read the existing tests in `AzureAISearchVectorStoreTests.cs` first to understand mock client setup. The test must verify that the delete loop runs twice for a 1001-chunk document.

**Step 2: Run test to verify it fails**
```bash
dotnet test tests/Rag.NET.AzureAISearch.Tests --no-build -q --filter "DeletesAllPages"
```

**Step 3: Replace `DeleteByDocumentIdAsync`** — rewrite the method body:

```csharp
public async Task DeleteByDocumentIdAsync(
    string documentId,
    CancellationToken cancellationToken = default)
{
    const int pageSize = 1000;

    List<string> idsToDelete;
    do
    {
        idsToDelete = [];

        var searchOptions = new Azure.Search.Documents.SearchOptions
        {
            Filter = $"document_id eq '{EscapeODataString(documentId)}'",
            Select = { "id" },
            Size = pageSize,
        };

        var response = await _searchClient.SearchAsync<SearchDocument>(
            null, searchOptions, cancellationToken).ConfigureAwait(false);

        await foreach (var result in response.Value.GetResultsAsync()
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            idsToDelete.Add(result.Document.GetString("id"));
        }

        if (idsToDelete.Count > 0)
        {
            var batch = IndexDocumentsBatch.Delete("id", idsToDelete);
            await _searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    } while (idsToDelete.Count == pageSize);
}
```

Note: also applies `EscapeODataString` to `documentId` in the filter — a defence-in-depth improvement consistent with Task 2.

**Step 4: Run full Azure tests**
```bash
dotnet test tests/Rag.NET.AzureAISearch.Tests --no-build -q
```
Expected: all pass.

**Step 5: Commit**
```bash
git add src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs \
        tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs
git commit -m "fix: paginate AzureAISearch DeleteByDocumentIdAsync to handle documents with >1000 chunks"
```

---

### Task 4 — I4: LocalFilesDataProvider exposes absolute path as document ID

**Files:**
- Modify: `src/Rag.NET/DataProviders/LocalFilesDataProvider.cs`
- Modify: `tests/Rag.NET.Tests/DataProviders/LocalFilesDataProviderTests.cs`

**Context:** `FileEntry.Id` is set to the full absolute path (line 40). This flows into `DocumentMetadata.DocumentId` and is returned in API search results, leaking `C:\Data\Sensitive\payroll.xlsx`-style paths to clients. Fix: use `Path.GetRelativePath(_rootPath, path)` which returns e.g. `"subdir/readme.md"` instead.

**Step 1: Update the existing test** that asserts the absolute path — in `LocalFilesDataProviderTests.cs`, find `GetFilesAsync_Entry_HasAbsolutePathAsId` and replace the assertion:

```csharp
[Fact]
public async Task GetFilesAsync_Entry_HasRelativePathAsId()
{
    WriteFile("readme.md");
    var sut = new LocalFilesDataProvider(_dir);
    var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
        .ToListAsync(TestContext.Current.CancellationToken);

    Assert.Equal("readme.md", entries[0].Id);
}
```

Also add a test for a subdirectory file:

```csharp
[Fact]
public async Task GetFilesAsync_SubdirectoryFile_HasRelativePathWithForwardSlash()
{
    var sub = Path.Combine(_dir, "docs");
    Directory.CreateDirectory(sub);
    File.WriteAllText(Path.Combine(sub, "guide.md"), "content");

    var sut = new LocalFilesDataProvider(_dir);
    var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
        .ToListAsync(TestContext.Current.CancellationToken);

    // Path.GetRelativePath uses the platform separator — normalise for the assertion
    var id = entries[0].Id.Replace('\\', '/');
    Assert.Equal("docs/guide.md", id);
}
```

**Step 2: Run tests to verify they fail**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "HasRelativePath"
```
Expected: FAIL — Id is still the absolute path.

**Step 3: Fix `LocalFilesDataProvider`** — change line 40 in `GetFilesAsync`:

```csharp
// OLD:
Id: path,

// NEW:
Id: Path.GetRelativePath(_rootPath, path),
```

**Step 4: Run the full test suite for Rag.NET.Tests**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q
```
Expected: all pass (the old `HasAbsolutePathAsId` test is gone; new relative-path tests pass).

**Step 5: Commit**
```bash
git add src/Rag.NET/DataProviders/LocalFilesDataProvider.cs \
        tests/Rag.NET.Tests/DataProviders/LocalFilesDataProviderTests.cs
git commit -m "fix: use relative path as LocalFilesDataProvider entry ID to avoid filesystem path disclosure"
```

---

### Task 5 — I8: Rss/Sitemap data providers return non-seekable stream

**Files:**
- Modify: `src/Rag.NET.DataProviders.Web/RssDataProvider.cs`
- Modify: `src/Rag.NET.DataProviders.Web/SitemapDataProvider.cs`
- Modify: `tests/Rag.NET.DataProviders.Web.Tests/RssDataProviderTests.cs`
- Modify: `tests/Rag.NET.DataProviders.Web.Tests/SitemapDataProviderTests.cs`

**Context:** `OpenContentAsync` calls `_httpClient.GetStreamAsync(...)` which returns the raw network response stream — non-seekable and non-rewindable. `DocumentIngestor.ChunkAndStoreParentsAsync` calls `document.CanSeek` and throws `InvalidOperationException` when parent-document retrieval is enabled. `WebCrawlerDataProvider` correctly buffers to `MemoryStream`. Fix both providers the same way.

**Step 1: Write failing tests** — append to `RssDataProviderTests.cs`:

```csharp
[Fact]
public async Task GetFilesAsync_Rss2_OpenContentAsync_ReturnsSeekableStream()
{
    const string xml = """
        <?xml version="1.0"?>
        <rss version="2.0">
          <channel>
            <item>
              <guid>https://example.com/post-1</guid>
              <link>https://example.com/post-1</link>
            </item>
          </channel>
        </rss>
        """;
    // Register both the feed URL and the content URL in FakeHttpMessageHandler
    var handler = new FakeHttpMessageHandler(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["https://example.com/feed.rss"] = xml,
        ["https://example.com/post-1"] = "<html>content</html>",
    });
    var sut = new RssDataProvider("https://example.com/feed.rss", new HttpClient(handler));
    var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
        .ToListAsync(TestContext.Current.CancellationToken);

    await using var stream = await entries[0].OpenContentAsync(TestContext.Current.CancellationToken);
    Assert.True(stream.CanSeek, "stream must be seekable for parent-document retrieval");
}
```

Add an equivalent test to `SitemapDataProviderTests.cs`:

```csharp
[Fact]
public async Task GetFilesAsync_OpenContentAsync_ReturnsSeekableStream()
{
    const string xml = """
        <?xml version="1.0"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url><loc>https://example.com/page</loc></url>
        </urlset>
        """;
    var handler = new FakeHttpMessageHandler(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["https://example.com/sitemap.xml"] = xml,
        ["https://example.com/page"] = "<html>content</html>",
    });
    var sut = new SitemapDataProvider("https://example.com/sitemap.xml", new HttpClient(handler));
    var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
        .ToListAsync(TestContext.Current.CancellationToken);

    await using var stream = await entries[0].OpenContentAsync(TestContext.Current.CancellationToken);
    Assert.True(stream.CanSeek);
}
```

**Step 2: Run tests to verify they fail**
```bash
dotnet test tests/Rag.NET.DataProviders.Web.Tests --no-build -q --filter "ReturnsSeekableStream"
```
Expected: FAIL — streams are non-seekable.

**Step 3: Fix `RssDataProvider`** — in both yield-return statements (Atom entry, line ~47; RSS item, line ~67), replace the `OpenContentAsync` lambda:

```csharp
// OLD:
OpenContentAsync: async ct => await _httpClient.GetStreamAsync(capturedLink, ct).ConfigureAwait(false),

// NEW:
OpenContentAsync: async ct =>
{
    var response = await _httpClient.GetStreamAsync(capturedLink, ct).ConfigureAwait(false);
    var buffer = new MemoryStream();
    await response.CopyToAsync(buffer, ct).ConfigureAwait(false);
    await response.DisposeAsync().ConfigureAwait(false);
    buffer.Position = 0;
    return (Stream)buffer;
},
```

**Step 4: Fix `SitemapDataProvider`** — same change in the one yield-return in `LoadSitemapAsync` (line ~62):

```csharp
// OLD:
OpenContentAsync: async ct => await _httpClient.GetStreamAsync(capturedLoc, ct).ConfigureAwait(false),

// NEW:
OpenContentAsync: async ct =>
{
    var response = await _httpClient.GetStreamAsync(capturedLoc, ct).ConfigureAwait(false);
    var buffer = new MemoryStream();
    await response.CopyToAsync(buffer, ct).ConfigureAwait(false);
    await response.DisposeAsync().ConfigureAwait(false);
    buffer.Position = 0;
    return (Stream)buffer;
},
```

**Step 5: Run tests**
```bash
dotnet test tests/Rag.NET.DataProviders.Web.Tests --no-build -q
```
Expected: all pass.

**Step 6: Commit**
```bash
git add src/Rag.NET.DataProviders.Web/RssDataProvider.cs \
        src/Rag.NET.DataProviders.Web/SitemapDataProvider.cs \
        tests/Rag.NET.DataProviders.Web.Tests/RssDataProviderTests.cs \
        tests/Rag.NET.DataProviders.Web.Tests/SitemapDataProviderTests.cs
git commit -m "fix: buffer Rss/Sitemap OpenContentAsync to MemoryStream so parent-document retrieval works"
```

---

### Task 6 — I1: DocumentIngestor seekable guard fires after stream is already consumed

**Files:**
- Modify: `src/Rag.NET/Ingestion/DocumentIngestor.cs`
- Modify: `tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs`

**Context:** `IngestAsync` calls `ParseAndChunkAsync` (line 41), which fully consumes the stream. Only after that does `ChunkAndStoreParentsAsync` check `document.CanSeek` (line 90) and throw. The guard fires too late — child chunks are already computed. Fix: move the check to the top of `IngestAsync`, before any I/O.

**Step 1: Write a failing test** — append to `DocumentIngestorTests.cs`. Read the file first to understand the existing test structure, then:

```csharp
[Fact]
public async Task IngestAsync_NonSeekableStream_WithParentOptions_ThrowsBeforeParsingStarts()
{
    // Arrange: non-seekable stream
    var stream = new NonSeekableStream(new MemoryStream("hello world"u8.ToArray()));
    var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt" };

    var parentStore = Substitute.For<IParentChunkStore>();
    var sut = new DocumentIngestor(
        [_parser], _chunker, _vectorStore, _embedder,
        new ChunkingOptions(), _bm25Index,
        parentStore: parentStore,
        parentOptions: new ParentDocumentOptions());

    // Act & Assert: must throw BEFORE calling ParseAsync
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken));

    await _parser.DidNotReceive().ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>());
}

// Helper: minimal non-seekable stream wrapper
private sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

**Step 2: Run test to verify it fails**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "NonSeekableStream_WithParentOptions_ThrowsBeforeParsingStarts"
```
Expected: FAIL — parser is currently called before the guard fires.

**Step 3: Reorder the guard in `DocumentIngestor.IngestAsync`** — move the seekability check to before `ParseAndChunkAsync`. In `IngestAsync`, after the overwrite block (lines 35–39) and before line 41, add:

```csharp
if (parentOptions is not null && parentStore is not null && !document.CanSeek)
    throw new InvalidOperationException(
        "Parent-document retrieval requires a seekable stream. Wrap the stream in a MemoryStream before calling IngestAsync.");
```

Then remove the duplicate check at the top of `ChunkAndStoreParentsAsync` (lines 90–93 — the `if (!document.CanSeek) throw` block — remove only the guard, keep `document.Position = 0`).

**Step 4: Run full ingestion tests**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "FullyQualifiedName~DocumentIngestor"
```
Expected: all pass.

**Step 5: Commit**
```bash
git add src/Rag.NET/Ingestion/DocumentIngestor.cs \
        tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs
git commit -m "fix: check stream seekability before parsing in DocumentIngestor so the guard fires before any I/O"
```

---

### Task 7 — I3: ETag unconditionally written for content-unchanged files

**Files:**
- Modify: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs`
- Modify: `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`

**Context:** In `IngestWithHashCheckAsync` (line 117), when the content hash matches (file unchanged), `hashStore.SetAsync` is called unconditionally to refresh the ETag. For providers where `ETag` is null (e.g. local files have an ETag but there are cases where it can be null), this writes a no-op record to SQLite on every run for every unchanged file, causing unnecessary write amplification. Fix: skip the `SetAsync` call when `entry.ETag` is null and content is unchanged.

**Step 1: Write a failing test** — append to `IngestFromProviderTests.cs`:

```csharp
[Fact]
public async Task IngestFromProviderAsync_NullETag_HashMatch_DoesNotWriteHashStore()
{
    var hashStore = Substitute.For<IContentHashStore>();

    // Pre-compute SHA-256 of "hello" — same as what the provider will return
    var helloHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("hello"u8.ToArray()));
    hashStore.GetETagAsync("prov", "id-1", Arg.Any<CancellationToken>()).Returns((string?)null);
    hashStore.GetHashAsync("prov", "id-1", Arg.Any<CancellationToken>()).Returns(helloHash);

    // Provider returns null ETag — content unchanged
    var provider = MakeProvider(("id-1", "a.txt", "hello", null));

    await _pipeline.IngestFromProviderAsync(provider, "prov",
        hashStore: hashStore,
        cancellationToken: TestContext.Current.CancellationToken);

    // SetAsync must NOT be called — there is no new ETag to store and hash already matches
    await hashStore.DidNotReceive().SetAsync(
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
}
```

**Step 2: Run test to verify it fails**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "NullETag_HashMatch_DoesNotWriteHashStore"
```
Expected: FAIL.

**Step 3: Fix `IngestWithHashCheckAsync`** — in `RagPipelineExtensions.cs`, find the hash-match branch (lines 115–119) and add the ETag guard:

```csharp
// OLD:
if (string.Equals(hash, storedHash, StringComparison.Ordinal))
{
    await hashStore.SetAsync(providerId, entry.Id, entry.ETag, hash, cancellationToken).ConfigureAwait(false);
    return EntryOutcome.Skipped;
}

// NEW:
if (string.Equals(hash, storedHash, StringComparison.Ordinal))
{
    // Only refresh ETag when there's a non-null ETag to store (prevents a no-op write on every unchanged file)
    if (entry.ETag is not null)
        await hashStore.SetAsync(providerId, entry.Id, entry.ETag, hash, cancellationToken).ConfigureAwait(false);
    return EntryOutcome.Skipped;
}
```

**Step 4: Run full provider tests**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "FullyQualifiedName~IngestFromProvider"
```
Expected: all pass.

**Step 5: Commit**
```bash
git add src/Rag.NET/DataProviders/RagPipelineExtensions.cs \
        tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs
git commit -m "fix: skip hash-store write when ETag is null and content is unchanged, avoiding unnecessary SQLite writes"
```

---

### Task 8 — I2: Cache retrievers — factoryCalled flag is unreliable under concurrent requests

**Files:**
- Modify: `src/Rag.NET/Retrieval/EmbeddingCacheRetriever.cs`
- Modify: `src/Rag.NET/Retrieval/ResultCacheRetriever.cs`
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs`

**Context:** Both cache retrievers use a `factoryCalled` closure variable to detect cache misses. If two concurrent requests share the same cache key, only one invokes the factory — but the other caller's `factoryCalled` stays `false`, so it incorrectly logs a cache hit. Fix: move the cache-miss log into the factory itself (the factory is only called on a miss) and remove the unreliable `factoryCalled` flag. The `EmbeddingCacheHit` / `ResultCacheHit` log methods remain but are now unused — delete them from `RagPipelineLog` to avoid dead-code accumulation.

**Step 1: Write a failing test** — read `tests/Rag.NET.Tests/Retrieval/EmbeddingCacheRetrieverTests.cs` first to understand the test structure, then add:

```csharp
[Fact]
public async Task RetrieveAsync_CacheMiss_LogsAtDebugLevel()
{
    // When the factory is called (cache miss), the miss should be logged.
    // We verify by checking the inner retriever WAS called (= factory ran = cache miss).
    // This is a behavioral test for the no-race-condition logging path.
    var logger = Substitute.For<ILogger>();
    logger.IsEnabled(LogLevel.Debug).Returns(true);

    // ... set up HybridCache, inner retriever returning a result, opts with UseCacheEmbedding = true
    // call RetrieveAsync twice with different keys so the second is definitely a miss
    // verify inner.RetrieveAsync was called for both (both were misses)
    // (exact setup mirrors the existing test patterns in EmbeddingCacheRetrieverTests)
}
```

> The key behavioral change to test: inner retriever is called once per unique key → factory is invoked → miss-log is emitted inside the factory. Adapt to the test file's existing helper setup.

**Step 2: Fix `EmbeddingCacheRetriever`** — remove `factoryCalled` and move logging into the factory:

```csharp
var results = await cache.GetOrCreateAsync(
    cacheKey,
    async ct =>
    {
        RagPipelineLog.EmbeddingCacheMiss(_logger, query);   // ← log miss here (factory only called on miss)
        var innerResults = await inner.RetrieveAsync(query, options, ct).ConfigureAwait(false);
        return innerResults as List<SearchResult> ?? innerResults.ToList();
    },
    new HybridCacheEntryOptions { Expiration = cachingOptions.EmbeddingTtl },
    cancellationToken: cancellationToken).ConfigureAwait(false);

// Remove the if (!factoryCalled) block entirely
return results ?? [];
```

**Step 3: Fix `ResultCacheRetriever`** — same change:

```csharp
var results = await cache.GetOrCreateAsync(
    cacheKey,
    async ct =>
    {
        RagPipelineLog.ResultCacheMiss(_logger, query);      // ← log miss here
        var innerResults = await inner.RetrieveAsync(query, options, ct).ConfigureAwait(false);
        return innerResults as List<SearchResult> ?? innerResults.ToList();
    },
    new HybridCacheEntryOptions { Expiration = cachingOptions.ResultTtl },
    cancellationToken: cancellationToken).ConfigureAwait(false);

return results ?? [];
```

**Step 4: Update `RagPipelineLog`** — replace the `EmbeddingCacheHit` / `ResultCacheHit` methods with `EmbeddingCacheMiss` / `ResultCacheMiss`:

```csharp
// Remove:
[LoggerMessage(Level = LogLevel.Debug, Message = "Embedding cache hit for query '{Query}'")]
internal static partial void EmbeddingCacheHit(ILogger logger, string query);

[LoggerMessage(Level = LogLevel.Debug, Message = "Result cache hit for query '{Query}'")]
internal static partial void ResultCacheHit(ILogger logger, string query);

// Add:
[LoggerMessage(Level = LogLevel.Debug, Message = "Embedding cache miss for query '{Query}'")]
internal static partial void EmbeddingCacheMiss(ILogger logger, string query);

[LoggerMessage(Level = LogLevel.Debug, Message = "Result cache miss for query '{Query}'")]
internal static partial void ResultCacheMiss(ILogger logger, string query);
```

**Step 5: Build and run cache retriever tests**
```bash
dotnet build src/Rag.NET/Rag.NET.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "FullyQualifiedName~CacheRetriever"
```
Expected: all pass.

**Step 6: Commit**
```bash
git add src/Rag.NET/Retrieval/EmbeddingCacheRetriever.cs \
        src/Rag.NET/Retrieval/ResultCacheRetriever.cs \
        src/Rag.NET/Logging/RagPipelineLog.cs \
        tests/Rag.NET.Tests/Retrieval/EmbeddingCacheRetrieverTests.cs \
        tests/Rag.NET.Tests/Retrieval/ResultCacheRetrieverTests.cs
git commit -m "fix: log cache miss inside factory to eliminate race condition on factoryCalled flag in cache retrievers"
```

---

### Task 9 — I7: MultiQueryRetriever swallows all results when any variant query throws

**Files:**
- Modify: `src/Rag.NET/Retrieval/MultiQueryRetriever.cs`
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs`
- Modify: `tests/Rag.NET.Tests/Retrieval/MultiQueryRetrieverTests.cs`

**Context:** Lines 45–46 use `Task.WhenAll` over all variant queries. If any task throws, `Task.WhenAll` re-throws and the caller receives an error — even if the original query's task succeeded. Fix: wrap each per-query task in a try/catch, log failed variants as warnings, and merge only the successful results. Keep the parallel fan-out.

**Step 1: Write a failing test** — append to `MultiQueryRetrieverTests.cs`:

```csharp
[Fact]
public async Task RetrieveAsync_VariantQueryThrows_ReturnsResultsFromSuccessfulQueries()
{
    var ct = TestContext.Current.CancellationToken;

    _expander.ExpandAsync("q", 2, ct)
        .Returns(new List<string> { "variant1", "variant2" });

    // Original query succeeds
    _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
        .Returns([MakeResult("doc-1", 0, 0.9)]);
    // variant1 throws
    _inner.RetrieveAsync("variant1", Arg.Any<RetrievalOptions?>(), ct)
        .Returns(Task.FromException<IReadOnlyList<SearchResult>>(new InvalidOperationException("retriever failed")));
    // variant2 succeeds
    _inner.RetrieveAsync("variant2", Arg.Any<RetrievalOptions?>(), ct)
        .Returns([MakeResult("doc-2", 0, 0.7)]);

    var opts = new RetrievalOptions { UseMultiQuery = true, TopK = 10 };
    var results = await _sut.RetrieveAsync("q", opts, ct);

    // Both successful query results must be in the output
    Assert.Equal(2, results.Count);
    Assert.Contains(results, r => string.Equals(r.Chunk.DocumentId, "doc-1", StringComparison.Ordinal));
    Assert.Contains(results, r => string.Equals(r.Chunk.DocumentId, "doc-2", StringComparison.Ordinal));
}
```

**Step 2: Run test to verify it fails**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "VariantQueryThrows_ReturnsResultsFromSuccessfulQueries"
```
Expected: FAIL — the exception from `Task.WhenAll` propagates instead of returning partial results.

**Step 3: Add `VariantQueryFailed` to `RagPipelineLog`**:

```csharp
[LoggerMessage(Level = LogLevel.Warning, Message = "Variant query retrieval failed for variant '{Query}', skipping")]
internal static partial void VariantQueryFailed(ILogger logger, string query, Exception exception);
```

**Step 4: Replace the `Task.WhenAll` fan-out in `MultiQueryRetriever.RetrieveAsync`** — replace lines 45–55:

```csharp
var tasks = allQueries
    .Select(q => SafeRetrieveAsync(q, options, cancellationToken))
    .ToArray();
var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

return allResults
    .Where(r => r is not null)
    .SelectMany(r => r!)
    .GroupBy(r => (r.Chunk.DocumentId, r.Chunk.ChunkIndex))
    .Select(g => g.MaxBy(r => r.Score)!)
    .OrderByDescending(r => r.Score)
    .Take(opts.TopK)
    .ToList()
    .AsReadOnly();
```

Add the helper method to the class:

```csharp
private async Task<IReadOnlyList<SearchResult>?> SafeRetrieveAsync(
    string query,
    RetrievalOptions? options,
    CancellationToken cancellationToken)
{
    try
    {
        return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        RagPipelineLog.VariantQueryFailed(_logger, query, ex);
        return null;
    }
}
```

**Step 5: Run all MultiQueryRetriever tests**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "FullyQualifiedName~MultiQueryRetriever"
```
Expected: all pass.

**Step 6: Commit**
```bash
git add src/Rag.NET/Retrieval/MultiQueryRetriever.cs \
        src/Rag.NET/Logging/RagPipelineLog.cs \
        tests/Rag.NET.Tests/Retrieval/MultiQueryRetrieverTests.cs
git commit -m "fix: MultiQueryRetriever returns partial results when variant query throws, instead of discarding all"
```

---

### Task 10 — I6: SqliteBm25Index / SqliteParentChunkStore block the thread pool on first use

**Files:**
- Modify: `src/Rag.NET/Storage/SqliteBm25Index.cs`
- Modify: `src/Rag.NET/Storage/SqliteParentChunkStore.cs`
- Modify: `tests/Rag.NET.Tests/Storage/SqliteBm25IndexTests.cs`
- Modify: `tests/Rag.NET.Tests/Storage/SqliteParentChunkStoreTests.cs`

**Context:** Both stores do SQLite I/O lazily on first use. The `EnsureInitialised()` method calls `_initLock.Wait()` (blocking) which can starve the thread pool when many concurrent requests trigger initialization. Fix: add an `InitializeAsync()` public method that callers invoke during DI startup. This moves initialization I/O off the request path entirely.

**Step 1: Write a failing test** — append to `SqliteBm25IndexTests.cs`:

```csharp
[Fact]
public async Task InitializeAsync_CanBeAwaited_ThenAddWorksWithoutBlockingInit()
{
    var sut = CreateSut();

    // Should complete without blocking the thread-pool
    await sut.InitializeAsync(TestContext.Current.CancellationToken);

    // Subsequent operations use the already-initialised state
    sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
    var results = sut.Search("hello", 5);

    Assert.Single(results);
    Assert.Equal("doc-1", results[0].chunk.DocumentId);
}
```

**Step 2: Run test to verify it fails**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "InitializeAsync_CanBeAwaited"
```
Expected: FAIL — `InitializeAsync` does not exist.

**Step 3: Add `InitializeAsync` to `SqliteBm25Index`**:

```csharp
/// <summary>
/// Explicitly initialises the SQLite backing store. Call this during application startup
/// (e.g. from a hosted service or DI setup) to avoid blocking thread-pool threads
/// on the first <see cref="Add"/> or <see cref="Search"/> call.
/// </summary>
public Task InitializeAsync(CancellationToken cancellationToken = default)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_initialised) return Task.CompletedTask;
    return Task.Run(() =>
    {
        _initLock.Wait(cancellationToken);
        try
        {
            if (_initialised) return;
            InitialiseCore();
            _initialised = true;
        }
        finally
        {
            _initLock.Release();
        }
    }, cancellationToken);
}
```

**Step 4: Add `InitializeAsync` to `SqliteParentChunkStore`** — identical signature and implementation pattern (substitute `InitialiseCore()` with whatever the store's own initialization method is named — read the file to confirm).

**Step 5: Add a parallel test to `SqliteParentChunkStoreTests.cs`**:

```csharp
[Fact]
public async Task InitializeAsync_CanBeAwaited_ThenAddWorksWithoutBlockingInit()
{
    var sut = new SqliteParentChunkStore(_dbPath, "test-coll");
    await sut.InitializeAsync(TestContext.Current.CancellationToken);

    sut.Add("doc-1", 0, "parent text");
    Assert.True(sut.TryGet("doc-1", 0, out var text));
    Assert.Equal("parent text", text);
}
```

**Step 6: Run storage tests**
```bash
dotnet test tests/Rag.NET.Tests --no-build -q --filter "FullyQualifiedName~SqliteBm25Index|FullyQualifiedName~SqliteParentChunk"
```
Expected: all pass.

**Step 7: Commit**
```bash
git add src/Rag.NET/Storage/SqliteBm25Index.cs \
        src/Rag.NET/Storage/SqliteParentChunkStore.cs \
        tests/Rag.NET.Tests/Storage/SqliteBm25IndexTests.cs \
        tests/Rag.NET.Tests/Storage/SqliteParentChunkStoreTests.cs
git commit -m "fix: add InitializeAsync to SqliteBm25Index and SqliteParentChunkStore to avoid blocking thread-pool on lazy init"
```

---

### Task 11 — C1: OnnxReranker uses hash-based tokenization, producing random reranking scores

**Files:**
- Modify: `src/Rag.NET.Reranking.Onnx/OnnxRerankerOptions.cs`
- Modify: `src/Rag.NET.Reranking.Onnx/OnnxReranker.cs`
- Modify: `tests/Rag.NET.Reranking.Onnx.Tests/OnnxRerankerTests.cs`

**Context:** `TokenizePair` maps each word to `GetHashCode(...) & 0x7FFF` — a random 15-bit integer that has nothing to do with the model's BERT vocabulary. The result is that the ONNX model receives junk input and produces meaningless scores. Fix: require a `vocab.txt` path in `OnnxRerankerOptions`, load the vocabulary at construction, and replace hash-based token IDs with vocabulary lookups. Words not found in the vocabulary fall back to `[UNK]` (token ID 100 in all standard BERT vocabularies).

> **Note on WordPiece:** Full WordPiece sub-word tokenization is not implemented here — each whitespace-delimited word is either found verbatim in the vocabulary (after lowercasing) or mapped to `[UNK]`. This is a correct fix for the security/correctness problem (random IDs replaced by real IDs). For production use with custom models, integrate `Microsoft.ML.Tokenizers` for proper sub-word splitting.

**Step 1: Add `VocabPath` to `OnnxRerankerOptions`**:

```csharp
/// <summary>
/// Path to the BERT vocabulary file (vocab.txt).
/// Each line is a token; the line index is the token ID.
/// Standard BERT uncased vocab.txt files are available from the model hub.
/// </summary>
public required string VocabPath { get; set; }
```

**Step 2: Write failing tests** — append to `OnnxRerankerTests.cs`:

```csharp
[Fact]
public void Constructor_WhenVocabPathDoesNotExist_ThrowsFileNotFoundException()
{
    var options = new OnnxRerankerOptions
    {
        ModelPath = "nonexistent/model.onnx",
        VocabPath = "nonexistent/vocab.txt",
    };

    // FileNotFoundException for the model path fires first
    Assert.Throws<FileNotFoundException>(() => new OnnxReranker(options));
}

[Fact]
public void LoadVocab_CorrectlyMapsLineIndexToTokenId()
{
    // Write a minimal vocab file: [PAD]=0, [UNK]=1, hello=2, world=3
    var vocabFile = Path.GetTempFileName();
    File.WriteAllLines(vocabFile, ["[PAD]", "[UNK]", "hello", "world"]);

    try
    {
        var vocab = OnnxReranker.LoadVocabForTest(vocabFile);
        Assert.Equal(0, vocab["[PAD]"]);
        Assert.Equal(1, vocab["[UNK]"]);
        Assert.Equal(2, vocab["hello"]);
        Assert.Equal(3, vocab["world"]);
    }
    finally
    {
        File.Delete(vocabFile);
    }
}
```

> `LoadVocabForTest` is a `internal static` wrapper around the private `LoadVocab` for testability. See Step 4.

**Step 3: Run tests to verify they fail**
```bash
dotnet test tests/Rag.NET.Reranking.Onnx.Tests --no-build -q
```
Expected: compile failure (VocabPath not on options, LoadVocabForTest not on class).

**Step 4: Update `OnnxReranker`**:

Add field and update constructor:
```csharp
private readonly IReadOnlyDictionary<string, int> _vocab;

public OnnxReranker(OnnxRerankerOptions options)
{
    ArgumentNullException.ThrowIfNull(options);

    if (!File.Exists(options.ModelPath))
        throw new FileNotFoundException(
            $"ONNX model file not found: {options.ModelPath}", options.ModelPath);

    if (!File.Exists(options.VocabPath))
        throw new FileNotFoundException(
            $"BERT vocabulary file not found: {options.VocabPath}", options.VocabPath);

    _options = options;
    _session = new InferenceSession(options.ModelPath);
    _vocab = LoadVocab(options.VocabPath);
}
```

Add vocabulary loading and test hook:
```csharp
private static IReadOnlyDictionary<string, int> LoadVocab(string vocabPath)
{
    var lines = File.ReadAllLines(vocabPath);
    var vocab = new Dictionary<string, int>(lines.Length, StringComparer.Ordinal);
    for (var i = 0; i < lines.Length; i++)
    {
        var token = lines[i];
        if (!string.IsNullOrEmpty(token))
            vocab[token] = i;
    }
    return vocab;
}

// Internal for unit-test access; not part of the public API.
internal static IReadOnlyDictionary<string, int> LoadVocabForTest(string vocabPath) =>
    LoadVocab(vocabPath);
```

Replace the hash-based token ID lines in `TokenizePair`:
```csharp
// At the top of TokenizePair, prepare token-ID arrays:
const int unkId = 100; // [UNK] in standard BERT vocab

int[] queryIds  = Array.ConvertAll(queryTokens,  w => _vocab.TryGetValue(w.ToLowerInvariant(), out var id)  ? id : unkId);
int[] passageIds = Array.ConvertAll(passageTokens, w => _vocab.TryGetValue(w.ToLowerInvariant(), out var id) ? id : unkId);

// Replace line 93 (query loop):
inputIds[0, pos] = queryIds[i];

// Replace line 106 (passage loop):
inputIds[0, pos] = passageIds[i];
```

**Step 5: Update existing tests** — existing tests in `OnnxRerankerTests.cs` construct `OnnxRerankerOptions` without `VocabPath` — add it to both:

```csharp
var options = new OnnxRerankerOptions
{
    ModelPath = "nonexistent/model.onnx",
    VocabPath = "nonexistent/vocab.txt",    // add this line
};
```

**Step 6: Build and run**
```bash
dotnet build src/Rag.NET.Reranking.Onnx/Rag.NET.Reranking.Onnx.csproj -q
dotnet test tests/Rag.NET.Reranking.Onnx.Tests --no-build -q
```
Expected: all pass.

**Step 7: Commit**
```bash
git add src/Rag.NET.Reranking.Onnx/OnnxRerankerOptions.cs \
        src/Rag.NET.Reranking.Onnx/OnnxReranker.cs \
        tests/Rag.NET.Reranking.Onnx.Tests/OnnxRerankerTests.cs
git commit -m "fix: replace hash-based tokenization in OnnxReranker with vocab-file lookup to produce correct BERT token IDs"
```

---

### Final verification

Run the full test suite across all affected projects:

```bash
cd c:/Projects/Prive/Rag.NET
dotnet test tests/Rag.NET.Tests \
            tests/Rag.NET.PgVector.Tests \
            tests/Rag.NET.AzureAISearch.Tests \
            tests/Rag.NET.DataProviders.Web.Tests \
            tests/Rag.NET.Reranking.Onnx.Tests \
            -q 2>&1 | grep -E "Passed!|Failed!"
```

Expected: all projects pass, 0 failures.
