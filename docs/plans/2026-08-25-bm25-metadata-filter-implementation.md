# BM25 Metadata Filter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `RetrievalOptions.MetadataFilter` apply to the BM25 arm of client-side hybrid search, so a filtered query can no longer return chunks the filter excludes.

**Architecture:** Add the filter to `IBm25Index.Search` (a breaking change to a published interface, chosen deliberately — see the spec) so BM25 ranks only eligible chunks and `topK` comes back full of eligible hits. Extract the dense arm's private `MatchesFilter` into a public `MetadataFilterMatcher` in `Rag.NET.Abstractions` so both arms share one definition of "matches" and cannot drift apart.

**Tech Stack:** .NET 10, C#, xUnit v3 (via Microsoft.Testing.Platform), NSubstitute.

**Spec:** [`docs/plans/2026-08-25-bm25-metadata-filter-design.md`](./2026-08-25-bm25-metadata-filter-design.md)

## Global Constraints

- **`TreatWarningsAsErrors=true`** is set in `Directory.Build.props`. A warning fails the build; XML doc comments are required on public members.
- **`Rag.NET.Abstractions` is a published package.** Anything public added there is a permanent surface commitment.
- **Filter semantics are unchanged:** typed equality (`MetadataValue` equality — a Number `3` does not match a String `"3"`), ordinal string comparison, AND across pairs, and `null`/empty means no filtering.
- **`dotnet test` runs through xunit v3's in-process runner**, so `--filter` is silently ignored. Filter with `-class '*TypeName*'` against the test executable, or just run the whole test project.
- **Do not touch native hybrid paths.** They filter server-side and correctly.

---

### Task 1: `MetadataFilterMatcher` — one definition of "matches"

**Files:**
- Create: `src/Rag.NET.Abstractions/Search/MetadataFilterMatcher.cs`
- Create: `tests/Rag.NET.Tests/Search/MetadataFilterMatcherTests.cs`
- Modify: `src/Rag.NET/Storage/InMemoryVectorStore.cs` (delete the private `MatchesFilter` at `:314`, redirect its two call sites at `:81` and `:232`)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `public static bool MetadataFilterMatcher.Matches(TextChunk chunk, IDictionary<string, MetadataValue>? filter)` in namespace `Rag.NET.Abstractions`. Tasks 2 and 3 depend on this exact signature.

- [ ] **Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Search/MetadataFilterMatcherTests.cs`:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Tests.Search;

public sealed class MetadataFilterMatcherTests
{
    private static TextChunk Chunk(params (string Key, MetadataValue Value)[] metadata)
    {
        var dict = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
            dict[key] = value;

        return new TextChunk
        {
            DocumentId = new DocumentId("doc-1"),
            ChunkIndex = 0,
            Text = "text",
            Metadata = dict,
        };
    }

    [Fact]
    public void Matches_NullFilter_MatchesEverything() =>
        Assert.True(MetadataFilterMatcher.Matches(Chunk(("tenant", "a")), null));

    [Fact]
    public void Matches_EmptyFilter_MatchesEverything() =>
        Assert.True(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a")), new Dictionary<string, MetadataValue>(StringComparer.Ordinal)));

    [Fact]
    public void Matches_MissingKey_DoesNotMatch() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["absent"] = "a" }));

    [Fact]
    public void Matches_DifferentValue_DoesNotMatch() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["tenant"] = "b" }));

    [Fact]
    public void Matches_EveryPairMustMatch_AndSemantics() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a"), ("lang", "en")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
                ["lang"] = "fr",
            }));

    [Fact]
    public void Matches_AllPairsMatch_Matches() =>
        Assert.True(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a"), ("lang", "en")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
                ["lang"] = "en",
            }));

    // The typed-equality guarantee RetrievalOptions.MetadataFilter documents: a Number filter
    // does not match the String form of the same digits. Asserted in both directions because a
    // matcher that coerced one way and not the other would pass a one-directional test.
    [Fact]
    public void Matches_NumberFilterAgainstStringValue_DoesNotMatch() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("page", "3")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 3 }));

    [Fact]
    public void Matches_StringFilterAgainstNumberValue_DoesNotMatch() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("page", 3)),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = "3" }));

    // Ordinal, not culture- or case-insensitive.
    [Fact]
    public void Matches_StringComparisonIsOrdinal() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "A")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["tenant"] = "a" }));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: FAIL to **compile** — `MetadataFilterMatcher` does not exist. A compile failure is the correct "red" here; do not proceed until you have seen it.

- [ ] **Step 3: Create the matcher**

Create `src/Rag.NET.Abstractions/Search/MetadataFilterMatcher.cs`:

```csharp
using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// The single definition of whether a chunk satisfies a metadata filter, shared by every
/// retrieval arm.
/// </summary>
/// <remarks>
/// <para>
/// This is public rather than internal on purpose. Implementing <see cref="IBm25Index"/> obliges
/// an implementer to honour <c>RetrievalOptions.MetadataFilter</c>, and shipping that obligation
/// without shipping the semantics would leave each implementer to reimplement typed equality by
/// guesswork — against a dense arm whose implementation they cannot read.
/// </para>
/// <para>
/// It exists because the matching used to be a private static inside <c>InMemoryVectorStore</c>.
/// Duplicating it into the BM25 indexes would have let the arms disagree about what matches, so a
/// filtered query would return different chunks depending on which arm found them — a new defect
/// of the same family as the one this was extracted to fix (#350).
/// </para>
/// </remarks>
public static class MetadataFilterMatcher
{
    /// <summary>
    /// Whether <paramref name="chunk"/> satisfies every pair in <paramref name="filter"/>.
    /// </summary>
    /// <param name="chunk">The chunk whose <see cref="TextChunk.Metadata"/> is tested.</param>
    /// <param name="filter">
    /// The required pairs. <see langword="null"/> or empty means no filtering, matching
    /// <c>RetrievalOptions.MetadataFilter</c>'s documented behaviour.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when every key is present and equal by <see cref="MetadataValue"/>
    /// equality — typed, so a Number <c>3</c> does not match a String <c>"3"</c>, and ordinal for
    /// strings. AND semantics across pairs.
    /// </returns>
    public static bool Matches(TextChunk chunk, IDictionary<string, MetadataValue>? filter)
    {
        if (filter is null || filter.Count == 0)
            return true;

        foreach (var (key, value) in filter)
        {
            // Typed equality: a Number 3 filter does not match a String "3" value.
            if (!chunk.Metadata.TryGetValue(key, out var actual) || actual != value)
                return false;
        }

        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: PASS, including all nine `MetadataFilterMatcherTests`.

- [ ] **Step 5: Redirect `InMemoryVectorStore` and delete its private copy**

In `src/Rag.NET/Storage/InMemoryVectorStore.cs`, replace both call sites (`:81` and `:232`):

```csharp
if (!MatchesFilter(entry.Embedded.Chunk, options.MetadataFilter))
```

with:

```csharp
if (!MetadataFilterMatcher.Matches(entry.Embedded.Chunk, options.MetadataFilter))
```

and at `:232`:

```csharp
if (!MetadataFilterMatcher.Matches(entry.Chunk.Chunk, options.MetadataFilter))
```

Then **delete the private `MatchesFilter` method** (`:314`) entirely. Add `using Rag.NET.Abstractions;` if it is not already present.

Leaving the private copy in place would defeat the point of the extraction — two definitions is exactly the state being removed.

- [ ] **Step 6: Run the full test project to verify nothing regressed**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: PASS. The dense arm's existing filter tests now exercise the shared matcher; if any fails, the extraction changed behaviour and must be reconciled before continuing.

- [ ] **Step 7: Commit**

```bash
git add src/Rag.NET.Abstractions/Search/MetadataFilterMatcher.cs tests/Rag.NET.Tests/Search/MetadataFilterMatcherTests.cs src/Rag.NET/Storage/InMemoryVectorStore.cs
git commit -m "refactor(abstractions): extract MetadataFilterMatcher so every arm matches alike"
```

---

### Task 2: `IBm25Index.Search` takes the filter

**Files:**
- Modify: `src/Rag.NET.Abstractions/Abstractions/IBm25Index.cs`
- Modify: `src/Rag.NET/Search/InMemoryBm25Index.cs:112`
- Modify: `src/Rag.NET.Storage.Sqlite/SqliteBm25Index.cs:65`
- Modify: `tests/Rag.NET.Tests/Search/InMemoryBm25IndexTests.cs`
- Modify: `tests/Rag.NET.Storage.Sqlite.Tests/Storage/SqliteBm25IndexTests.cs`

**Interfaces:**
- Consumes: `MetadataFilterMatcher.Matches(TextChunk, IDictionary<string, MetadataValue>?)` from Task 1.
- Produces: `IBm25Index.Search(string query, int topK, IDictionary<string, MetadataValue>? metadataFilter = null)`. Task 3 calls this three-argument form.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Rag.NET.Tests/Search/InMemoryBm25IndexTests.cs`:

```csharp
    private static TextChunk FilterChunk(int index, string text, string tenant)
    {
        return new TextChunk
        {
            DocumentId = new DocumentId("doc-" + index.ToString(CultureInfo.InvariantCulture)),
            ChunkIndex = index,
            Text = text,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = tenant,
            },
        };
    }

    [Fact]
    public void Search_WithMetadataFilter_ExcludesNonMatchingChunks()
    {
        using var sut = new InMemoryBm25Index();
        sut.Add(1, FilterChunk(1, "shared search term", "a"));
        sut.Add(2, FilterChunk(2, "shared search term", "b"));

        var results = sut.Search(
            "shared search term",
            topK: 10,
            metadataFilter: new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            });

        var hit = Assert.Single(results);
        Assert.True(hit.chunk.Metadata["tenant"] == "a");
    }

    // The advantage of filtering inside the index over filtering the results afterwards: topK
    // comes back full of eligible hits rather than the best overall minus whatever was dropped.
    // With post-filtering, asking for 1 here would return 0 whenever the "b" chunk outranked
    // the "a" one.
    [Fact]
    public void Search_WithMetadataFilter_FillsTopKWithEligibleChunks()
    {
        using var sut = new InMemoryBm25Index();
        sut.Add(1, FilterChunk(1, "term term term term", "b"));
        sut.Add(2, FilterChunk(2, "term", "a"));

        var results = sut.Search(
            "term",
            topK: 1,
            metadataFilter: new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            });

        var hit = Assert.Single(results);
        Assert.True(hit.chunk.Metadata["tenant"] == "a");
    }

    [Fact]
    public void Search_WithNullMetadataFilter_ReturnsEverything()
    {
        using var sut = new InMemoryBm25Index();
        sut.Add(1, FilterChunk(1, "shared search term", "a"));
        sut.Add(2, FilterChunk(2, "shared search term", "b"));

        var results = sut.Search("shared search term", topK: 10, metadataFilter: null);

        Assert.Equal(2, results.Count);
    }
```

Add `using System.Globalization;` and `using Rag.NET.Models;` to the file's usings if absent.

Append the equivalent to `tests/Rag.NET.Storage.Sqlite.Tests/Storage/SqliteBm25IndexTests.cs`, constructing the index the way the existing tests in that file do (they need a database path — follow the surrounding pattern rather than inventing one):

```csharp
    [Fact]
    public void Search_WithMetadataFilter_ExcludesNonMatchingChunks()
    {
        using var sut = CreateIndex();
        sut.Add(1, FilterChunk(1, "shared search term", "a"));
        sut.Add(2, FilterChunk(2, "shared search term", "b"));

        var results = sut.Search(
            "shared search term",
            topK: 10,
            metadataFilter: new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            });

        var hit = Assert.Single(results);
        Assert.True(hit.chunk.Metadata["tenant"] == "a");
    }
```

Reuse that file's existing index-construction helper. If it has none, add a `CreateIndex()` local mirroring how its other tests build a `SqliteBm25Index`, and a `FilterChunk` helper identical to the one above.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: FAIL to compile — `Search` has no three-argument overload.

- [ ] **Step 3: Add the parameter to the interface**

In `src/Rag.NET.Abstractions/Abstractions/IBm25Index.cs`, replace the `Search` declaration:

```csharp
    /// <summary>
    /// Returns up to <paramref name="topK"/> chunks ranked by BM25 score against
    /// <paramref name="query"/>, best first, restricted to chunks satisfying
    /// <paramref name="metadataFilter"/>.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="topK">The maximum number of chunks to return.</param>
    /// <param name="metadataFilter">
    /// Required metadata pairs, or <see langword="null"/> for no filtering. Implementations MUST
    /// apply this via <see cref="MetadataFilterMatcher.Matches"/> (or semantics identical to it)
    /// and MUST apply it <b>before</b> truncating to <paramref name="topK"/>, so the caller
    /// receives the best <i>eligible</i> chunks rather than the best chunks minus the ineligible
    /// ones.
    /// </param>
    /// <returns>Matching chunks with their BM25 scores, best first.</returns>
    IReadOnlyList<(TextChunk chunk, double score)> Search(
        string query, int topK, IDictionary<string, MetadataValue>? metadataFilter = null);
```

Add `using Rag.NET.Models;` if not already present (it is — the file already uses `TextChunk`).

- [ ] **Step 4: Filter inside `InMemoryBm25Index`**

In `src/Rag.NET/Search/InMemoryBm25Index.cs`, change the signature at `:112` and filter while collecting results. Replace:

```csharp
    public IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK)
```

with:

```csharp
    public IReadOnlyList<(TextChunk chunk, double score)> Search(
        string query, int topK, IDictionary<string, MetadataValue>? metadataFilter = null)
```

and replace the result-collection loop:

```csharp
            var result = new List<(TextChunk chunk, double score)>(Math.Min(scores.Count, topK));
            foreach (var kv in scores)
                result.Add((_docs[kv.Key].chunk, kv.Value));
```

with:

```csharp
            var result = new List<(TextChunk chunk, double score)>(Math.Min(scores.Count, topK));
            foreach (var kv in scores)
            {
                var chunk = _docs[kv.Key].chunk;

                // Filtered before the sort and the topK truncation below, which is the whole
                // advantage of filtering here rather than in the caller: topK comes back full of
                // eligible chunks instead of the best overall with the ineligible ones removed.
                if (!MetadataFilterMatcher.Matches(chunk, metadataFilter))
                    continue;

                result.Add((chunk, kv.Value));
            }
```

Add `using Rag.NET.Abstractions;` if absent.

- [ ] **Step 5: Forward the filter from `SqliteBm25Index`**

In `src/Rag.NET.Storage.Sqlite/SqliteBm25Index.cs:65`, replace:

```csharp
    public IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();
        return _memory.Search(query, topK);
    }
```

with:

```csharp
    /// <inheritdoc />
    /// <remarks>
    /// The filter is forwarded rather than pushed into SQL: this type is a write-through wrapper
    /// over <see cref="InMemoryBm25Index"/> and every search already runs in memory. The
    /// <c>metadata_json</c> column rehydrates chunks on load; it is not queried here.
    /// </remarks>
    public IReadOnlyList<(TextChunk chunk, double score)> Search(
        string query, int topK, IDictionary<string, MetadataValue>? metadataFilter = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialised();
        return _memory.Search(query, topK, metadataFilter);
    }
```

- [ ] **Step 6: Run both test projects to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests -c Release`
Run: `dotnet test tests/Rag.NET.Storage.Sqlite.Tests -c Release`

Expected: PASS on both.

- [ ] **Step 7: Mutation-check the filter**

Temporarily invert the guard in `InMemoryBm25Index` — change `if (!MetadataFilterMatcher.Matches(...))` to `if (MetadataFilterMatcher.Matches(...))` — and re-run `tests/Rag.NET.Tests`.

Expected: the three new `Search_WithMetadataFilter*` tests FAIL. **Restore the guard** and re-run to confirm PASS.

A filter test that passes against unfiltered code is this milestone's recurring failure (#332's regression test did exactly that). Do not skip this step.

- [ ] **Step 8: Commit**

```bash
git add src/Rag.NET.Abstractions/Abstractions/IBm25Index.cs src/Rag.NET/Search/InMemoryBm25Index.cs src/Rag.NET.Storage.Sqlite/SqliteBm25Index.cs tests/Rag.NET.Tests/Search/InMemoryBm25IndexTests.cs tests/Rag.NET.Storage.Sqlite.Tests/Storage/SqliteBm25IndexTests.cs
git commit -m "feat(abstractions)!: IBm25Index.Search takes a metadata filter (#350)"
```

---

### Task 3: `EnsembleBehavior` passes the filter, and the leak is proven closed

**Files:**
- Modify: `src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs:110`
- Modify: `tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs`

**Interfaces:**
- Consumes: `IBm25Index.Search(string, int, IDictionary<string, MetadataValue>?)` from Task 2.
- Produces: nothing further depends on this task.

> **Before writing anything, fix the existing stubs.** Every test in this file stubs the BM25 arm as
> `bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())`. With a third defaulted parameter, that call
> compiles to `Search(x, y, null)`, so NSubstitute matches it **only when the filter is null**. Once
> Task 3 Step 4 makes production pass a real filter, any such stub stops matching and silently
> returns an empty list instead of the hit the test expects.
>
> Update every existing occurrence in this file to:
>
> ```csharp
> bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
> ```
>
> This is a silent-failure trap, not a compile error: leaving them alone produces tests that pass
> for the wrong reason today and mislead whoever reads them next.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs`, using this file's own
construction style — inline substitutes plus the existing `MakeCtx`, `MakeResult` and `MakeBm25Hit`
helpers. There are no `CreateBehavior`/`CreateContext` helpers in this file; do not add any.

```csharp
    // #350: the BM25 arm never received MetadataFilter, and RrfMerger merged its hits alongside
    // the filtered arms, so a filtered query could return a chunk the filter excluded.
    [Fact]
    public async Task HandleAsync_ClientSideHybrid_PassesMetadataFilterToTheBm25Arm()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f, 0.2f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions
        {
            UseHybridSearch = true,
            TopK = 5,
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            },
        });

        _ = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        // The third argument is the assertion. That Search was called at all was always true.
        bm25Index.Received(1).Search(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Is<IDictionary<string, MetadataValue>?>(f => f != null && f["tenant"] == "a"));
    }
```

Note `f["tenant"] == "a"`: `MetadataValue` defines `operator ==` and an implicit conversion from
`string`, so this is a typed comparison rather than a `ToString()` comparison.

Then one test per remaining trigger that forces the client-side path, because the leak is wider than
"stores with no native hybrid". Both use a store that **does** implement `IHybridSearchable` — build
it with `Substitute.For<IVectorStore, IHybridSearchable>()` so the substitute satisfies both — and
assert the BM25 arm still receives the filter:

```csharp
    // CanDispatchNatively returns false when MinScore is non-zero, so a store WITH native hybrid
    // still takes the client-side path -- and still leaked before this fix.
    [Fact]
    public async Task HandleAsync_NativeStoreWithMinScore_StillPassesFilterToTheBm25Arm()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore, IHybridSearchable>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f, 0.2f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions
        {
            UseHybridSearch = true,
            TopK = 5,
            MinScore = 0.2,
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            },
        });

        _ = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        bm25Index.Received(1).Search(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Is<IDictionary<string, MetadataValue>?>(f => f != null && f["tenant"] == "a"));
    }

    // Same again for EnsembleOptions: supplying one at all expresses weighting intent, which the
    // native path cannot honour, so the request falls back to client-side fusion.
    [Fact]
    public async Task HandleAsync_NativeStoreWithEnsembleOptions_StillPassesFilterToTheBm25Arm()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore, IHybridSearchable>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f, 0.2f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions
        {
            UseHybridSearch = true,
            TopK = 5,
            EnsembleOptions = new EnsembleOptions(),
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            },
        });

        _ = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        bm25Index.Received(1).Search(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Is<IDictionary<string, MetadataValue>?>(f => f != null && f["tenant"] == "a"));
    }
```

The third trigger — a sparse arm running — needs `SparseGenerator` and an `ISparseSearchable` store,
which this file may not already set up. If wiring it costs more than the two above, cover it in
`InMemoryBm25IndexTests` instead: the trigger only decides *which path runs*, and Task 2 already
proves the index itself filters. Say which you did in the commit message rather than leaving it
ambiguous.

- [ ] **Step 2: Write the end-to-end parity test**

This is the test that guards the divergence risk the shared matcher exists to prevent — the dense and BM25 arms agreeing on what matches. It uses real components rather than substitutes:

```csharp
    // The dense arm and the BM25 arm must agree about what a filter matches. They are separate
    // implementations reached by separate code paths; if they ever disagree, a filtered query
    // returns different chunks depending on which arm found them.
    [Fact]
    public void DenseAndBm25Arms_AgreeOnWhichChunksMatchAFilter()
    {
        var chunks = new[]
        {
            FilterChunk(1, "alpha term", "a"),
            FilterChunk(2, "beta term", "b"),
            FilterChunk(3, "gamma term", "a"),
        };

        var filter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["tenant"] = "a",
        };

        using var bm25 = new InMemoryBm25Index();
        for (var i = 0; i < chunks.Length; i++)
            bm25.Add(i + 1, chunks[i]);

        var bm25Matched = bm25.Search("term", topK: 10, metadataFilter: filter)
            .Select(static hit => hit.chunk.ChunkIndex)
            .OrderBy(static index => index)
            .ToArray();

        var matcherMatched = chunks
            .Where(chunk => MetadataFilterMatcher.Matches(chunk, filter))
            .Select(static chunk => chunk.ChunkIndex)
            .OrderBy(static index => index)
            .ToArray();

        Assert.Equal(matcherMatched, bm25Matched);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: FAIL. The three `EnsembleBehavior` tests fail because the call site still passes two arguments, so the substitute's three-argument `Received` never matches.

- [ ] **Step 4: Pass the filter at the call site**

In `src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs:110`, replace:

```csharp
            bm25Hits = Bm25Index.Search(ctx.Query, topK: searchOptions.TopK);
```

with:

```csharp
            bm25Hits = Bm25Index.Search(
                ctx.Query, topK: searchOptions.TopK, metadataFilter: searchOptions.MetadataFilter);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: PASS.

- [ ] **Step 6: Run the whole solution**

Run: `dotnet build Rag.NET.slnx -c Release` then `dotnet test Rag.NET.slnx -c Release --no-build`

Expected: build clean (0 errors — remember `TreatWarningsAsErrors`), all tests pass. Any other `IBm25Index` implementation or call site in the solution surfaces here as a compile error; fix it by forwarding the filter, not by dropping it.

- [ ] **Step 7: Commit**

```bash
git add src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs
git commit -m "fix(retrieval): apply MetadataFilter to the BM25 arm of client-side hybrid (#350)"
```

---

### Task 4: Document the breaking change

**Files:**
- Modify: `docs/guide/` — the hybrid-search or retrieval page that documents `MetadataFilter` (locate with `grep -rln "MetadataFilter" docs/`)
- Modify: `CHANGELOG.md` if one is maintained by hand (check first — the project uses release-please, which generates it from commit messages; if so, **skip the changelog edit** and rely on the `!` in Task 2's commit subject)

**Interfaces:**
- Consumes: the completed behaviour from Tasks 1-3.
- Produces: nothing.

- [ ] **Step 1: Check how the changelog is produced**

Run: `cat docs/planning/CONVENTIONS.md | grep -A 2 Changelog`

If it says `auto`, do not hand-edit `CHANGELOG.md`. The breaking change is communicated by the `!` in `feat(abstractions)!:` from Task 2.

- [ ] **Step 2: Document the fix where `MetadataFilter` is described**

Find the page: `grep -rln "MetadataFilter" docs/guide/`

Add a short note stating plainly what changed and what it means for implementers:

```markdown
### `MetadataFilter` and the BM25 arm

`MetadataFilter` applies to every arm of hybrid search, including BM25.

This was not true before v1.0: `IBm25Index.Search` had no filter parameter, so the BM25 arm of
**client-side** hybrid search ranked and returned chunks the filter excluded, and those hits were
merged into the result. It affected more than stores without native hybrid search — supplying
`EnsembleOptions`, setting a non-zero `MinScore`, or running a sparse arm all fall back to the
client-side path, so a store with correct server-side filtering could still leak.

**If you implement `IBm25Index` yourself**, `Search` now takes an optional
`IDictionary<string, MetadataValue>? metadataFilter`. Apply it with
`MetadataFilterMatcher.Matches` — which is public for exactly this reason — and apply it *before*
truncating to `topK`, so callers receive the best eligible chunks rather than the best chunks minus
the ineligible ones.
```

- [ ] **Step 3: Verify the docs build**

Run: `dotnet test tests/Rag.NET.Tests -c Release --filter-class '*Documentation*'` if a docs-example compilation test exists (search: `grep -rln "docs" tests/ --include=*Documentation*`). Otherwise run the full solution test once more.

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add docs/
git commit -m "docs(retrieval): MetadataFilter now applies to the BM25 arm (#350)"
```

---

## Definition of Done

- [ ] `MetadataFilterMatcher` is public in `Rag.NET.Abstractions`, and `InMemoryVectorStore`'s private `MatchesFilter` is **deleted** rather than left beside it.
- [ ] `IBm25Index.Search` takes the filter; both implementations honour it; SQLite forwards it.
- [ ] The filter is applied **before** `topK` truncation, verified by `Search_WithMetadataFilter_FillsTopKWithEligibleChunks`.
- [ ] A test fails against the pre-fix code — confirmed by the Task 2 Step 7 mutation check, not assumed.
- [ ] All three client-side triggers are covered: no native hybrid, `MinScore != 0`, `EnsembleOptions` supplied.
- [ ] The dense and BM25 arms are proven to agree on what matches.
- [ ] `dotnet build Rag.NET.slnx -c Release` is clean and the full suite passes.
- [ ] The breaking change is documented for external implementers.
