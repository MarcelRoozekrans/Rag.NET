# Gap Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix 8 gaps in the Rag.NET codebase: overlap/position tracking, tag propagation, pipeline delete, metadata filtering, hybrid search wiring, collection management, and multi-turn conversation.

**Architecture:** Each fix is a small, isolated change to existing files. No new projects needed. Tests use NSubstitute mocks for unit tests and Testcontainers for integration tests. All options classes get new properties with safe defaults so nothing breaks.

**Tech Stack:** .NET 10, xUnit v3, NSubstitute, BenchmarkDotNet, Testcontainers, pgvector, Qdrant gRPC, Azure AI Search SDK

---

### Task 1: Fix RecursiveChunkingStrategy — Overlap Support

**Files:**
- Modify: `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs`
- Test: `tests/Rag.NET.Tests/Chunking/RecursiveChunkingStrategyTests.cs`

**Step 1: Write the failing test for overlap**

Add to `RecursiveChunkingStrategyTests.cs`:

```csharp
[Fact]
public async Task ChunkAsync_WithOverlap_ChunksOverlap()
{
    // Two paragraphs, each under MaxChunkSize, but overlap should cause text from end of chunk 0
    // to appear at the start of chunk 1
    var text = "First paragraph content here.\n\nSecond paragraph content here.";
    var section = CreateSection(text);
    var options = new ChunkingOptions { MaxChunkSize = 200, Overlap = 10 };

    var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
        .ToListAsync(TestContext.Current.CancellationToken);

    Assert.Equal(2, chunks.Count);
    // The second chunk should start with overlapping text from the first chunk's end
    var firstChunkEnd = chunks[0].Text[^10..];
    Assert.StartsWith(firstChunkEnd, chunks[1].Text);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "RecursiveChunkingStrategyTests.ChunkAsync_WithOverlap_ChunksOverlap" -v n`
Expected: FAIL (overlap is currently ignored)

**Step 3: Write the failing test for position tracking**

Add to `RecursiveChunkingStrategyTests.cs`:

```csharp
[Fact]
public async Task ChunkAsync_TracksPositionsRelativeToSource()
{
    var text = "First paragraph.\n\nSecond paragraph.";
    var section = CreateSection(text);
    var options = new ChunkingOptions { MaxChunkSize = 200, Overlap = 0 };

    var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
        .ToListAsync(TestContext.Current.CancellationToken);

    Assert.Equal(2, chunks.Count);
    // First chunk position
    Assert.Equal(0, chunks[0].StartPosition);
    Assert.True(chunks[0].EndPosition > 0);
    // Second chunk position should be after the "\n\n" separator
    Assert.True(chunks[1].StartPosition > chunks[0].EndPosition);
    // Extracted text from source using positions should match chunk text
    Assert.Equal(chunks[0].Text, text[chunks[0].StartPosition..chunks[0].EndPosition].Trim());
    Assert.Equal(chunks[1].Text, text[chunks[1].StartPosition..chunks[1].EndPosition].Trim());
}
```

**Step 4: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "RecursiveChunkingStrategyTests.ChunkAsync_TracksPositionsRelativeToSource" -v n`
Expected: FAIL (StartPosition is always 0)

**Step 5: Implement overlap and position tracking**

Replace `ChunkAsync` method in `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs`:

```csharp
public async IAsyncEnumerable<TextChunk> ChunkAsync(
    DocumentSection section,
    ChunkingOptions options,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    if (string.IsNullOrEmpty(section.Text))
    {
        yield break;
    }

    var rawChunks = SplitRecursively(section.Text, options.MaxChunkSize, 0).ToList();

    int chunkIndex = 0;
    int searchStart = 0;
    string? previousChunkText = null;

    foreach (var text in rawChunks)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Build the actual chunk text with overlap from the previous chunk
        string chunkText;
        if (previousChunkText is not null && options.Overlap > 0)
        {
            int overlapLen = Math.Min(options.Overlap, previousChunkText.Length);
            var overlapText = previousChunkText[^overlapLen..];
            chunkText = overlapText + text;
        }
        else
        {
            chunkText = text;
        }

        // Find the position of the raw text in the source
        int startPos = section.Text.IndexOf(text, searchStart, StringComparison.Ordinal);
        if (startPos < 0)
        {
            startPos = searchStart;
        }

        int endPos = startPos + text.Length;
        searchStart = startPos + 1;

        yield return new TextChunk
        {
            Text = chunkText,
            DocumentId = section.DocumentId,
            ChunkIndex = chunkIndex++,
            StartPosition = startPos,
            EndPosition = endPos,
        };

        previousChunkText = text;
    }

    await Task.CompletedTask.ConfigureAwait(false);
}
```

**Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "RecursiveChunkingStrategyTests" -v n`
Expected: ALL PASS

**Step 7: Commit**

```bash
git add src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs tests/Rag.NET.Tests/Chunking/RecursiveChunkingStrategyTests.cs
git commit -m "fix: add overlap and position tracking to RecursiveChunkingStrategy"
```

---

### Task 2: Propagate DocumentMetadata.Tags to TextChunk.Metadata

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs:22-59` (IngestAsync method)
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Write the failing test**

Add to `RagPipelineTests.cs`:

```csharp
[Fact]
public async Task IngestAsync_PropagatesMetadataTagsToChunks()
{
    var metadata = new DocumentMetadata
    {
        DocumentId = "doc-1",
        FileName = "test.txt",
        ContentType = "text/plain",
        Tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["department"] = "engineering",
            ["year"] = "2026",
        },
    };

    var section = new DocumentSection { Text = "Hello world", DocumentId = "doc-1", SectionIndex = 0 };
    var chunk = new TextChunk { Text = "Hello world", DocumentId = "doc-1", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello world"));
    await _sut.IngestAsync(stream, metadata, TestContext.Current.CancellationToken);

    await _vectorStore.Received(1).StoreAsync(
        Arg.Is<IReadOnlyList<EmbeddedChunk>>(chunks =>
            chunks[0].Chunk.Metadata.ContainsKey("department") &&
            chunks[0].Chunk.Metadata["department"] == "engineering" &&
            chunks[0].Chunk.Metadata.ContainsKey("document_id") &&
            chunks[0].Chunk.Metadata["document_id"] == "doc-1" &&
            chunks[0].Chunk.Metadata.ContainsKey("file_name") &&
            chunks[0].Chunk.Metadata["file_name"] == "test.txt"),
        Arg.Any<CancellationToken>());
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipelineTests.IngestAsync_PropagatesMetadataTagsToChunks" -v n`
Expected: FAIL (metadata is not propagated)

**Step 3: Implement metadata propagation**

In `src/Rag.NET/Pipeline/RagPipeline.cs`, in the `IngestAsync` method, add this block after the `chunks` list is built (after line 39, before the `if (chunks.Count == 0)` check):

```csharp
// Propagate document metadata to chunks
foreach (var chunk in chunks)
{
    // Add document-level tags (don't overwrite parser-set metadata)
    foreach (var tag in metadata.Tags)
    {
        chunk.Metadata.TryAdd(tag.Key, tag.Value);
    }

    // Add standard document metadata
    chunk.Metadata.TryAdd("document_id", metadata.DocumentId);
    chunk.Metadata.TryAdd("file_name", metadata.FileName);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipelineTests" -v n`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: propagate DocumentMetadata.Tags to TextChunk.Metadata during ingestion"
```

---

### Task 3: Expose DeleteAsync on IRagPipeline

**Files:**
- Modify: `src/Rag.NET/Abstractions/IRagPipeline.cs`
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Write the failing test**

Add to `RagPipelineTests.cs`:

```csharp
[Fact]
public async Task DeleteAsync_DelegatesToVectorStore()
{
    await _sut.DeleteAsync("doc-1", TestContext.Current.CancellationToken);

    await _vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", Arg.Any<CancellationToken>());
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipelineTests.DeleteAsync_DelegatesToVectorStore" -v n`
Expected: FAIL (method doesn't exist — compilation error)

**Step 3: Add DeleteAsync to IRagPipeline**

Add to `src/Rag.NET/Abstractions/IRagPipeline.cs` (before the closing brace):

```csharp
Task DeleteAsync(
    string documentId,
    CancellationToken cancellationToken = default);
```

**Step 4: Implement DeleteAsync in RagPipeline**

Add to `src/Rag.NET/Pipeline/RagPipeline.cs` (after the `AskStreamingAsync` method):

```csharp
public Task DeleteAsync(
    string documentId,
    CancellationToken cancellationToken = default)
{
    return vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken);
}
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipelineTests" -v n`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/Rag.NET/Abstractions/IRagPipeline.cs src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: expose DeleteAsync on IRagPipeline"
```

---

### Task 4: Wire Metadata Filtering in PgVector

**Files:**
- Modify: `src/Rag.NET.PgVector/PgVectorStore.cs:92-142` (SearchAsync method)
- Test: `tests/Rag.NET.PgVector.Tests/PgVectorStoreTests.cs`

**Step 1: Write the failing integration test**

Add to `PgVectorStoreTests.cs`:

```csharp
[Fact]
public async Task Search_WithMetadataFilter_FiltersResults()
{
    var chunks = new List<EmbeddedChunk>
    {
        new()
        {
            Chunk = new TextChunk
            {
                Text = "engineering doc", DocumentId = "doc-1", ChunkIndex = 0,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            Embedding = new float[] { 1.0f, 0.0f, 0.0f },
        },
        new()
        {
            Chunk = new TextChunk
            {
                Text = "marketing doc", DocumentId = "doc-2", ChunkIndex = 0,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "marketing" },
            },
            Embedding = new float[] { 0.9f, 0.1f, 0.0f },
        },
    };

    await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

    var results = await _sut.SearchAsync(
        new float[] { 1.0f, 0.0f, 0.0f },
        new SearchOptions
        {
            TopK = 10,
            MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
        },
        TestContext.Current.CancellationToken);

    Assert.Single(results);
    Assert.Equal("engineering doc", results[0].Chunk.Text);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.PgVector.Tests --filter "PgVectorStoreTests.Search_WithMetadataFilter_FiltersResults" -v n`
Expected: FAIL (filter is ignored, both results returned)

**Step 3: Implement metadata filtering in PgVectorStore.SearchAsync**

Replace the `SearchAsync` method in `src/Rag.NET.PgVector/PgVectorStore.cs`:

```csharp
public async Task<IReadOnlyList<SearchResult>> SearchAsync(
    ReadOnlyMemory<float> queryEmbedding,
    SearchOptions options,
    CancellationToken cancellationToken = default)
{
    var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using (conn.ConfigureAwait(false))
    {
        var sql = """
            SELECT document_id, chunk_index, text, metadata,
                   1 - (embedding <=> $1) AS score
            FROM rag_chunks
            WHERE 1 - (embedding <=> $1) >= $2
            """;

        if (options.MetadataFilter is { Count: > 0 })
        {
            sql += " AND metadata @> $4::jsonb";
        }

        sql += """

            ORDER BY embedding <=> $1
            LIMIT $3
            """;

        var cmd = new NpgsqlCommand(sql, conn);
        await using (cmd.ConfigureAwait(false))
        {
            cmd.Parameters.AddWithValue(new Vector(queryEmbedding.ToArray()));
            cmd.Parameters.AddWithValue(options.MinScore);
            cmd.Parameters.AddWithValue(options.TopK);

            if (options.MetadataFilter is { Count: > 0 })
            {
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Jsonb,
                    JsonSerializer.Serialize(options.MetadataFilter));
            }

            var results = new List<SearchResult>();

            var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        reader.GetString(3)) ?? [];

                    results.Add(new SearchResult
                    {
                        Chunk = new TextChunk
                        {
                            DocumentId = reader.GetString(0),
                            ChunkIndex = reader.GetInt32(1),
                            Text = reader.GetString(2),
                            Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                        },
                        Score = reader.GetDouble(4),
                    });
                }
            }

            return results;
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.PgVector.Tests -v n`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/Rag.NET.PgVector/PgVectorStore.cs tests/Rag.NET.PgVector.Tests/PgVectorStoreTests.cs
git commit -m "feat: implement metadata filtering in PgVectorStore"
```

---

### Task 5: Wire Metadata Filtering in Qdrant

**Files:**
- Modify: `src/Rag.NET.Qdrant/QdrantVectorStore.cs:66-98` (SearchAsync method)
- Test: `tests/Rag.NET.Qdrant.Tests/QdrantVectorStoreTests.cs`

**Step 1: Write the failing integration test**

Add to `QdrantVectorStoreTests.cs`:

```csharp
[Fact]
public async Task Search_WithMetadataFilter_FiltersResults()
{
    var chunks = new List<EmbeddedChunk>
    {
        new()
        {
            Chunk = new TextChunk
            {
                Text = "engineering doc", DocumentId = "doc-1", ChunkIndex = 0,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            Embedding = new float[] { 1.0f, 0.0f, 0.0f },
        },
        new()
        {
            Chunk = new TextChunk
            {
                Text = "marketing doc", DocumentId = "doc-2", ChunkIndex = 0,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "marketing" },
            },
            Embedding = new float[] { 0.9f, 0.1f, 0.0f },
        },
    };

    await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

    var results = await _sut.SearchAsync(
        new float[] { 1.0f, 0.0f, 0.0f },
        new SearchOptions
        {
            TopK = 10,
            MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
        },
        TestContext.Current.CancellationToken);

    Assert.Single(results);
    Assert.Equal("engineering doc", results[0].Chunk.Text);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Qdrant.Tests --filter "QdrantVectorStoreTests.Search_WithMetadataFilter_FiltersResults" -v n`
Expected: FAIL (filter is ignored)

**Step 3: Implement metadata filtering in QdrantVectorStore**

The Qdrant store serializes metadata as a JSON string in the `metadata` payload field. We need to also store individual metadata keys as payload fields for filtering. Modify `StoreAsync` and `SearchAsync` in `src/Rag.NET.Qdrant/QdrantVectorStore.cs`.

In `StoreAsync`, after `["metadata"] = JsonSerializer.Serialize(chunk.Chunk.Metadata)`, add individual metadata fields with a `meta_` prefix:

```csharp
// Add individual metadata fields for filtering
foreach (var kvp in chunk.Chunk.Metadata)
{
    points[^1].Payload[$"meta_{kvp.Key}"] = kvp.Value;
}
```

In `SearchAsync`, build a filter when `MetadataFilter` is provided:

```csharp
public async Task<IReadOnlyList<SearchResult>> SearchAsync(
    ReadOnlyMemory<float> queryEmbedding,
    SearchOptions options,
    CancellationToken cancellationToken = default)
{
    Filter? filter = null;
    if (options.MetadataFilter is { Count: > 0 })
    {
        filter = new Filter();
        foreach (var kvp in options.MetadataFilter)
        {
            filter.Must.Add(MatchKeyword($"meta_{kvp.Key}", kvp.Value));
        }
    }

    var results = await _client.SearchAsync(
        _collectionName,
        queryEmbedding.ToArray(),
        filter: filter,
        limit: (ulong)options.TopK,
        scoreThreshold: (float)options.MinScore,
        cancellationToken: cancellationToken).ConfigureAwait(false);

    return results
        .Select(point =>
        {
            var metadata = point.Payload.TryGetValue("metadata", out var metaValue)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(metaValue.StringValue) ?? []
                : [];

            return new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = point.Payload["text"].StringValue,
                    DocumentId = point.Payload["document_id"].StringValue,
                    ChunkIndex = (int)point.Payload["chunk_index"].IntegerValue,
                    Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                },
                Score = point.Score,
            };
        })
        .ToList();
}
```

Note: Add `using Qdrant.Client.Grpc;` if `Filter` is not already imported.

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Qdrant.Tests -v n`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/Rag.NET.Qdrant/QdrantVectorStore.cs tests/Rag.NET.Qdrant.Tests/QdrantVectorStoreTests.cs
git commit -m "feat: implement metadata filtering in QdrantVectorStore"
```

---

### Task 6: Wire Metadata Filtering in Azure AI Search

**Files:**
- Modify: `src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs`
- Test: `tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`

**Step 1: Write the integration test**

Add to `AzureAISearchVectorStoreTests.cs`:

```csharp
[Fact]
public async Task Search_WithMetadataFilter_FiltersResults()
{
    var chunks = new List<EmbeddedChunk>
    {
        new()
        {
            Chunk = new TextChunk
            {
                Text = "engineering doc", DocumentId = "doc-filter-1", ChunkIndex = 0,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            Embedding = new float[] { 1.0f, 0.0f, 0.0f },
        },
        new()
        {
            Chunk = new TextChunk
            {
                Text = "marketing doc", DocumentId = "doc-filter-2", ChunkIndex = 0,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "marketing" },
            },
            Embedding = new float[] { 0.9f, 0.1f, 0.0f },
        },
    };

    await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

    var results = await _sut.SearchAsync(
        new float[] { 1.0f, 0.0f, 0.0f },
        new RagSearchOptions
        {
            TopK = 10,
            MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
        },
        TestContext.Current.CancellationToken);

    Assert.Single(results);
    Assert.Equal("engineering doc", results[0].Chunk.Text);
}
```

Note: Azure AI Search tests require credentials (environment variables `AZURE_SEARCH_ENDPOINT` and `AZURE_SEARCH_API_KEY`). The test project uses `using RagSearchOptions = Rag.NET.Models.Options.SearchOptions;` to disambiguate.

**Step 2: Implement metadata filtering in AzureAISearchVectorStore**

Azure AI Search stores metadata as a serialized JSON string in a `metadata` field. Since Azure AI Search cannot filter on arbitrary JSON keys inside a string field, we need a different approach. Add filterable fields for known metadata keys, or more practically, parse the `MetadataFilter` and build a filter on the `metadata` field using `search.ismatch`.

The simplest approach: Since metadata is stored as a string field, we can use `search.ismatch` or store metadata as individual fields. The practical approach for now is to filter on `document_id` if the filter contains it, and log a warning for other keys. However, a better approach is to modify the schema to store metadata keys as individual filterable string fields.

For the initial implementation, add the OData filter for keys that match existing index fields (like `document_id`). For arbitrary metadata, we need to change how metadata is stored. Add individual string fields per metadata key to the index at ingestion time.

**Simpler approach:** In `ExecuteSearchAsync`, apply the metadata filter as an OData `$filter` expression. Since metadata is a JSON string stored in a `metadata` string field, we use string matching:

In `SearchAsync` and `HybridSearchAsync`, pass the filter to `ExecuteSearchAsync`:

```csharp
// In SearchAsync, before calling ExecuteSearchAsync:
if (options.MetadataFilter is { Count: > 0 })
{
    var filterClauses = options.MetadataFilter
        .Select(kvp => $"search.ismatch('\"{kvp.Key}\":\"{kvp.Value}\"', 'metadata')")
        .ToList();
    searchOptions.Filter = string.Join(" and ", filterClauses);
}
```

Apply the same pattern in `HybridSearchAsync`.

**Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.AzureAISearch.Tests -v n`
Expected: ALL PASS (or skipped if no Azure credentials)

**Step 4: Commit**

```bash
git add src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs
git commit -m "feat: implement metadata filtering in AzureAISearchVectorStore"
```

---

### Task 7: Wire Hybrid Search into Pipeline

**Files:**
- Modify: `src/Rag.NET/Models/Options/RagOptions.cs`
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`
- Modify: `src/Rag.NET/Models/Options/SearchOptions.cs`
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Add UseHybridSearch property to options classes**

In `src/Rag.NET/Models/Options/RagOptions.cs`, add:
```csharp
public bool UseHybridSearch { get; set; }
```

In `src/Rag.NET/Models/Options/RetrievalOptions.cs`, add:
```csharp
public bool UseHybridSearch { get; set; }
```

In `src/Rag.NET/Models/Options/SearchOptions.cs`, add:
```csharp
public bool UseHybridSearch { get; set; }
```

**Step 2: Write the failing test for hybrid search dispatch**

Add to `RagPipelineTests.cs`:

```csharp
[Fact]
public async Task RetrieveAsync_WithHybridSearch_CallsHybridSearchable()
{
    var hybridStore = Substitute.For<IVectorStore, IHybridSearchable>();
    var sut = new RagPipeline(
        [_parser],
        _chunker,
        hybridStore,
        _embedder,
        chatClient: null,
        new ChunkingOptions());

    var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
    var searchResult = new SearchResult
    {
        Chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 },
        Score = 0.95,
    };

    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

    ((IHybridSearchable)hybridStore).HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(new List<SearchResult> { searchResult });

    var results = await sut.RetrieveAsync(
        "test query",
        new RetrievalOptions { UseHybridSearch = true },
        TestContext.Current.CancellationToken);

    Assert.Single(results);
    await ((IHybridSearchable)hybridStore).Received(1).HybridSearchAsync(
        "test query", Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task RetrieveAsync_WithHybridSearch_ThrowsWhenStoreNotHybrid()
{
    var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _sut.RetrieveAsync(
            "test query",
            new RetrievalOptions { UseHybridSearch = true },
            TestContext.Current.CancellationToken));
}
```

**Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipelineTests.RetrieveAsync_WithHybridSearch" -v n`
Expected: FAIL (hybrid search not wired)

**Step 4: Implement hybrid search in RagPipeline.RetrieveAsync**

Replace the `RetrieveAsync` method in `src/Rag.NET/Pipeline/RagPipeline.cs`:

```csharp
public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
    string query,
    RetrievalOptions? options = null,
    CancellationToken cancellationToken = default)
{
    var opts = options ?? new RetrievalOptions();
    var queryEmbeddings = await embeddingGenerator.GenerateAsync(
        [query], cancellationToken: cancellationToken).ConfigureAwait(false);

    var searchOptions = new SearchOptions
    {
        TopK = opts.TopK,
        MinScore = opts.MinScore,
        MetadataFilter = opts.MetadataFilter,
        UseHybridSearch = opts.UseHybridSearch,
    };

    if (opts.UseHybridSearch)
    {
        if (vectorStore is not IHybridSearchable hybrid)
        {
            throw new InvalidOperationException(
                "The registered IVectorStore does not implement IHybridSearchable. " +
                "Use a vector store that supports hybrid search, such as AzureAISearchVectorStore.");
        }

        return await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    return await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken)
        .ConfigureAwait(false);
}
```

Also update `AskAsync` and `AskStreamingAsync` to pass `UseHybridSearch` through `RetrievalOptions`:

In `AskAsync`, change:
```csharp
var retrievalOptions = new RetrievalOptions { TopK = opts.TopK, MinScore = opts.MinScore };
```
to:
```csharp
var retrievalOptions = new RetrievalOptions
{
    TopK = opts.TopK,
    MinScore = opts.MinScore,
    MetadataFilter = opts.MetadataFilter,
    UseHybridSearch = opts.UseHybridSearch,
};
```

Apply the same change in `AskStreamingAsync`.

Note: `RagOptions` doesn't currently have `MetadataFilter`. Add it:

In `src/Rag.NET/Models/Options/RagOptions.cs`, add:
```csharp
public IDictionary<string, string>? MetadataFilter { get; set; }
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipelineTests" -v n`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/Rag.NET/Models/Options/RagOptions.cs src/Rag.NET/Models/Options/RetrievalOptions.cs src/Rag.NET/Models/Options/SearchOptions.cs src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: wire hybrid search into pipeline with UseHybridSearch option"
```

---

### Task 8: Implement ICollectionManageable in PgVectorStore

**Files:**
- Modify: `src/Rag.NET.PgVector/PgVectorStore.cs`
- Modify: `src/Rag.NET.PgVector/PgVectorBuilderExtensions.cs`
- Test: `tests/Rag.NET.PgVector.Tests/PgVectorStoreTests.cs`

**Step 1: Write the failing integration test**

Add to `PgVectorStoreTests.cs`:

```csharp
[Fact]
public async Task CollectionManageable_CreateAndDeleteCollection()
{
    ICollectionManageable manageable = (ICollectionManageable)_sut;

    await manageable.CreateCollectionAsync("temp_collection", 3, TestContext.Current.CancellationToken);
    Assert.True(await manageable.CollectionExistsAsync("temp_collection", TestContext.Current.CancellationToken));

    await manageable.DeleteCollectionAsync("temp_collection", TestContext.Current.CancellationToken);
    Assert.False(await manageable.CollectionExistsAsync("temp_collection", TestContext.Current.CancellationToken));
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.PgVector.Tests --filter "PgVectorStoreTests.CollectionManageable_CreateAndDeleteCollection" -v n`
Expected: FAIL (InvalidCastException — PgVectorStore doesn't implement ICollectionManageable)

**Step 3: Implement ICollectionManageable on PgVectorStore**

Change the class declaration in `src/Rag.NET.PgVector/PgVectorStore.cs`:

```csharp
public sealed class PgVectorStore : IVectorStore, ICollectionManageable, IDisposable
```

Add the three methods before `Dispose()`:

```csharp
public async Task CreateCollectionAsync(
    string name,
    int vectorDimensions,
    CancellationToken cancellationToken = default)
{
    var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using (conn.ConfigureAwait(false))
    {
        var enableExt = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", conn);
        await using (enableExt.ConfigureAwait(false))
        {
            await enableExt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await conn.ReloadTypesAsync().ConfigureAwait(false);

        var sql = $"""
            CREATE TABLE IF NOT EXISTS {name} (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                document_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                text TEXT NOT NULL,
                metadata JSONB NOT NULL DEFAULT '{{}}',
                embedding vector({vectorDimensions}) NOT NULL
            )
            """;

        var cmd = new NpgsqlCommand(sql, conn);
        await using (cmd.ConfigureAwait(false))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var indexCmd = new NpgsqlCommand(
            $"CREATE INDEX IF NOT EXISTS idx_{name}_document_id ON {name} (document_id)", conn);
        await using (indexCmd.ConfigureAwait(false))
        {
            await indexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

public async Task DeleteCollectionAsync(
    string name,
    CancellationToken cancellationToken = default)
{
    var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using (conn.ConfigureAwait(false))
    {
        var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS {name}", conn);
        await using (cmd.ConfigureAwait(false))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

public async Task<bool> CollectionExistsAsync(
    string name,
    CancellationToken cancellationToken = default)
{
    var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using (conn.ConfigureAwait(false))
    {
        var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = $1)", conn);
        await using (cmd.ConfigureAwait(false))
        {
            cmd.Parameters.AddWithValue(name);
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is true;
        }
    }
}
```

**Step 4: Update DI registration**

In `src/Rag.NET.PgVector/PgVectorBuilderExtensions.cs`, add `ICollectionManageable` registration:

```csharp
public static RagBuilder UsePgVector(
    this RagBuilder builder,
    string connectionString,
    int vectorDimensions = 1536)
{
    var store = new PgVectorStore(connectionString, vectorDimensions);
    builder.Services.AddSingleton<IVectorStore>(store);
    builder.Services.AddSingleton<ICollectionManageable>(store);
    return builder;
}
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.PgVector.Tests -v n`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/Rag.NET.PgVector/PgVectorStore.cs src/Rag.NET.PgVector/PgVectorBuilderExtensions.cs tests/Rag.NET.PgVector.Tests/PgVectorStoreTests.cs
git commit -m "feat: implement ICollectionManageable in PgVectorStore"
```

---

### Task 9: Implement ICollectionManageable in AzureAISearchVectorStore

**Files:**
- Modify: `src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs`
- Modify: `src/Rag.NET.AzureAISearch/AzureAISearchBuilderExtensions.cs`
- Test: `tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`

**Step 1: Write the integration test**

Add to `AzureAISearchVectorStoreTests.cs`:

```csharp
[Fact]
public async Task CollectionManageable_CreateAndDeleteCollection()
{
    ICollectionManageable manageable = (ICollectionManageable)_sut;
    var tempIndex = $"temp-{Guid.NewGuid():N}"[..24]; // Azure AI Search index names max 128 chars

    await manageable.CreateCollectionAsync(tempIndex, 3, TestContext.Current.CancellationToken);
    Assert.True(await manageable.CollectionExistsAsync(tempIndex, TestContext.Current.CancellationToken));

    await manageable.DeleteCollectionAsync(tempIndex, TestContext.Current.CancellationToken);

    // Brief wait for consistency
    await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    Assert.False(await manageable.CollectionExistsAsync(tempIndex, TestContext.Current.CancellationToken));
}
```

**Step 2: Implement ICollectionManageable on AzureAISearchVectorStore**

Change the class declaration:

```csharp
public sealed class AzureAISearchVectorStore : IVectorStore, IHybridSearchable, ICollectionManageable
```

Add the three methods:

```csharp
public async Task CreateCollectionAsync(
    string name,
    int vectorDimensions,
    CancellationToken cancellationToken = default)
{
    var fields = new List<SearchField>
    {
        new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
        new SimpleField("document_id", SearchFieldDataType.String) { IsFilterable = true },
        new SimpleField("chunk_index", SearchFieldDataType.Int32),
        new SearchableField("text"),
        new SimpleField("metadata", SearchFieldDataType.String),
        new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
        {
            VectorSearchDimensions = vectorDimensions,
            VectorSearchProfileName = "default-profile",
        },
    };

    var vectorSearch = new VectorSearch();
    vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("default-algorithm"));
    vectorSearch.Profiles.Add(new VectorSearchProfile("default-profile", "default-algorithm"));

    var index = new SearchIndex(name)
    {
        Fields = fields,
        VectorSearch = vectorSearch,
    };

    await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken)
        .ConfigureAwait(false);
}

public async Task DeleteCollectionAsync(
    string name,
    CancellationToken cancellationToken = default)
{
    await _indexClient.DeleteIndexAsync(name, cancellationToken)
        .ConfigureAwait(false);
}

public async Task<bool> CollectionExistsAsync(
    string name,
    CancellationToken cancellationToken = default)
{
    try
    {
        await _indexClient.GetIndexAsync(name, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
    {
        return false;
    }
}
```

**Step 3: Update DI registration**

In `src/Rag.NET.AzureAISearch/AzureAISearchBuilderExtensions.cs`, add `ICollectionManageable`:

```csharp
public static RagBuilder UseAzureAISearch(
    this RagBuilder builder,
    Uri endpoint,
    string indexName,
    AzureKeyCredential credential,
    int vectorDimensions = 1536)
{
    var store = new AzureAISearchVectorStore(endpoint, indexName, credential, vectorDimensions);
    builder.Services.AddSingleton<IVectorStore>(store);
    builder.Services.AddSingleton<IHybridSearchable>(store);
    builder.Services.AddSingleton<ICollectionManageable>(store);
    return builder;
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.AzureAISearch.Tests -v n`
Expected: ALL PASS (or skipped if no Azure credentials)

**Step 5: Commit**

```bash
git add src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs src/Rag.NET.AzureAISearch/AzureAISearchBuilderExtensions.cs tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs
git commit -m "feat: implement ICollectionManageable in AzureAISearchVectorStore"
```

---

### Task 10: Add Multi-Turn Conversation Support

**Files:**
- Modify: `src/Rag.NET/Models/Options/RagOptions.cs`
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Add ConversationHistory property to RagOptions**

In `src/Rag.NET/Models/Options/RagOptions.cs`, add:

```csharp
using Microsoft.Extensions.AI;
```

and the property:

```csharp
public IList<ChatMessage>? ConversationHistory { get; set; }
```

**Step 2: Write the failing test**

Add to `RagPipelineTests.cs`:

```csharp
[Fact]
public async Task AskAsync_WithConversationHistory_IncludesHistoryInMessages()
{
    var chatClient = Substitute.For<IChatClient>();
    var sut = new RagPipeline(
        [_parser],
        _chunker,
        _vectorStore,
        _embedder,
        chatClient,
        new ChunkingOptions());

    var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(new List<SearchResult>());

    var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"));
    chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
        .Returns(response);

    var history = new List<ChatMessage>
    {
        new(ChatRole.User, "Previous question"),
        new(ChatRole.Assistant, "Previous answer"),
    };

    await sut.AskAsync(
        "follow-up question",
        new RagOptions { ConversationHistory = history },
        TestContext.Current.CancellationToken);

    await chatClient.Received(1).GetResponseAsync(
        Arg.Is<IEnumerable<ChatMessage>>(msgs =>
        {
            var list = msgs.ToList();
            // System, then history (2 messages), then user with context
            return list.Count == 4 &&
                   list[0].Role == ChatRole.System &&
                   list[1].Role == ChatRole.User && list[1].Text == "Previous question" &&
                   list[2].Role == ChatRole.Assistant && list[2].Text == "Previous answer" &&
                   list[3].Role == ChatRole.User;
        }),
        Arg.Any<ChatOptions?>(),
        Arg.Any<CancellationToken>());
}
```

**Step 3: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipelineTests.AskAsync_WithConversationHistory_IncludesHistoryInMessages" -v n`
Expected: FAIL (history is not included)

**Step 4: Implement conversation history in RagPipeline**

In `src/Rag.NET/Pipeline/RagPipeline.cs`, modify the `AskAsync` method. Replace the `messages` construction:

```csharp
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt),
};

if (opts.ConversationHistory is { Count: > 0 })
{
    messages.AddRange(opts.ConversationHistory);
}

messages.Add(new ChatMessage(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"));
```

Apply the same change in `BuildRagMessages`:

```csharp
private static (List<ChatMessage> Messages, ChatOptions Options) BuildRagMessages(
    IReadOnlyList<SearchResult> sources,
    string query,
    RagOptions opts)
{
    var context = string.Join("\n\n---\n\n",
        sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}"));

    var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, systemPrompt),
    };

    if (opts.ConversationHistory is { Count: > 0 })
    {
        messages.AddRange(opts.ConversationHistory);
    }

    messages.Add(new ChatMessage(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"));

    var chatOptions = new ChatOptions();
    if (opts.Temperature.HasValue)
    {
        chatOptions.Temperature = opts.Temperature.Value;
    }

    return (messages, chatOptions);
}
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipelineTests" -v n`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/Rag.NET/Models/Options/RagOptions.cs src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: add multi-turn conversation support via ConversationHistory"
```

---

### Task 11: Run Full Test Suite and Verify Build

**Files:** None (verification only)

**Step 1: Build the entire solution**

Run: `dotnet build Rag.NET.slnx -c Release`
Expected: Build succeeded, 0 errors, 0 warnings

**Step 2: Run all unit tests**

Run: `dotnet test tests/Rag.NET.Tests -v n`
Expected: ALL PASS

**Step 3: Run integration tests (PgVector requires Docker)**

Run: `dotnet test tests/Rag.NET.PgVector.Tests -v n`
Expected: ALL PASS

**Step 4: Run Qdrant integration tests (requires Docker)**

Run: `dotnet test tests/Rag.NET.Qdrant.Tests -v n`
Expected: ALL PASS

**Step 5: Commit any remaining fixes**

If any tests fail, fix the root cause and commit the fix.
