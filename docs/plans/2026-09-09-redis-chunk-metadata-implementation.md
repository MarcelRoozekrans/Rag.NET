# Redis Chunk Metadata Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `RedisVectorStore` persists chunk metadata, returns it from both read paths, and honours `SearchOptions.MetadataFilter` on declared keys — instead of returning an empty dictionary and silently ignoring every filter.

**Architecture:** Metadata is stored twice, for two different jobs. The whole dictionary goes into one `metadata` hash field as JSON via the shared `MetadataSerializer`, which is what both read paths decode. Each *declared filterable* key additionally goes into its own `md_<key>` hash field as a kind-prefixed, Base64Url-encoded token, declared in the RediSearch schema as a case-sensitive TAG attribute — that is what the filter clause matches. Filtering on an undeclared key throws, and initialisation throws when a live index predates the declared keys.

**Tech Stack:** .NET 10, C#, NRedisStack 1.7.4 (RediSearch), StackExchange.Redis, xunit v3, Testcontainers.Redis against `redis/redis-stack-server:latest`.

**Spec:** `docs/plans/2026-09-09-redis-chunk-metadata-design.md` — read it before Task 1. Its §3.2 and §3.3 carry amendments dated 2026-09-09 that supersede the originally approved text; the amendments are what this plan implements.

## Global Constraints

- **Issue:** #513. **Phase:** 6.2.31. Related: #521 (the other three stores' corrupt-metadata posture — **do not touch them in this phase**).
- **Every value's textual form comes from `MetadataValue.ToString()`.** Never hand-roll `double.ToString(...)`, `bool` casing, or a date format. One accessor cannot drift from itself.
- **Base64Url is `System.Buffers.Text.Base64Url.EncodeToString`**, the same API `AzureAISearchVectorStore.cs:232` uses.
- **`EscapeTag` already exists** — `RedisVectorStore.cs:426`, `internal static`. Reuse it; do not write a second escaper.
- **A corrupt `metadata` blob throws.** A *missing* `metadata` field does not — it means the hash predates this change and reads as an empty dictionary. These two are different and both are tested.
- **The negative-`chunk_index` test must stay green.** On all seven backends it has been the only thing catching an unsigned-index implementation.
- **Commit format:** conventional commits, header ≤ 100 characters. CI lints every commit a PR adds.
- **Branch:** `feat/513-redis-chunk-metadata`, already created off `origin/main`. Do not commit to `main`.
- **Test command:** `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q`. Docker must be running; these tests start a container.
- **Constructing a `MetadataValue` in tests:** this plan writes `new MetadataValue("x")` so each test states which kind it means. **`MetadataValue`'s constructors may not be public** — the type exposes implicit conversions from `string`, `int`, `long`, `double`, `bool` and `DateTimeOffset`. If a constructor call does not compile, use the conversion instead (`MetadataValue v = 3d;` or `("page", (MetadataValue)3d)`) and keep every assertion identical. Do not change what a test asserts to make it compile.

---

### Task 1: Settle whether `FT.ALTER` reindexes existing documents (gated probe)

The design chose approach B — declared keys — partly because approach A rests on an unverified fact. **This task settles it before B's configuration surface becomes permanent API.** It is throwaway: nothing it produces is committed.

**Files:**
- Create: nothing committed. Use a scratch container only.

**Interfaces:**
- Consumes: nothing.
- Produces: a yes/no answer that either confirms Task 2 onwards or stops the plan.

- [ ] **Step 1: Confirm Docker is running**

Run: `docker version --format '{{.Server.Version}}'`
Expected: a version string. If it fails, start Docker Desktop and retry. Do not proceed without it.

- [ ] **Step 2: Start a scratch Redis Stack container**

```bash
docker run -d --rm --name ragnet-ftalter-probe -p 16399:6379 redis/redis-stack-server:latest
```

- [ ] **Step 3: Create an index, write a document, then add an attribute afterwards**

```bash
docker exec ragnet-ftalter-probe redis-cli FT.CREATE probe-idx ON HASH PREFIX 1 probe: SCHEMA document_id TAG
docker exec ragnet-ftalter-probe redis-cli HSET probe:1 document_id d1 md_tenant 's:YWNtZQ'
docker exec ragnet-ftalter-probe redis-cli FT.ALTER probe-idx SCHEMA ADD md_tenant TAG
docker exec ragnet-ftalter-probe redis-cli FT.SEARCH probe-idx '@md_tenant:{s\:YWNtZQ}'
```

Expected, if `FT.ALTER` reindexes existing documents: the final command returns `1) "1"` and the `probe:1` hash.
Expected, if it does not: the final command returns `1) "0"`.

- [ ] **Step 4: Repeat with a document written AFTER the alter, as a control**

```bash
docker exec ragnet-ftalter-probe redis-cli HSET probe:2 document_id d2 md_tenant 's:YWNtZQ'
docker exec ragnet-ftalter-probe redis-cli FT.SEARCH probe-idx '@md_tenant:{s\:YWNtZQ}'
```

Expected: at least `probe:2` is found. **If this control returns 0, the probe itself is wrong** — the escaping or the tag syntax is off — and neither result from Step 3 means anything. Fix the probe before reading Step 3's answer.

- [ ] **Step 5: Tear down**

```bash
docker rm -f ragnet-ftalter-probe
```

- [ ] **Step 6: Act on the answer**

- **`FT.ALTER` does NOT reindex existing documents** (Step 3 returned 0, Step 4 found `probe:2`): approach B stands as designed. Record the result in the design doc's §1 — replacing "Docker was unavailable at design time" with the measurement and its date — commit that one-line change, and continue to Task 2.
- **`FT.ALTER` DOES reindex** (Step 3 found `probe:1`): **STOP.** Approach A is viable and would remove B's permanent configuration surface. Report the measurement to the operator and ask whether to re-open the design before writing any code. Do not proceed to Task 2 on your own judgement.

- [ ] **Step 7: Commit the design-doc update (only on the B-stands branch)**

```bash
git add docs/plans/2026-09-09-redis-chunk-metadata-design.md
git commit -m "docs(plans): measure FT.ALTER — it does not reindex, so 6.2.31 keeps approach B"
```

---

### Task 2: Persist the metadata blob, and decode it on the keyed lookup

**Files:**
- Modify: `src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs` — field constants (near line 47), `StoreAsync` (line 189), `MapChunk` (line 334)
- Test: `tests/Rag.NET.VectorStores.Redis.Tests/RedisChunkLookupTests.cs` — rewrite `MetadataIsAbsentBecauseTheStorePersistsNone` (line 175)

**Interfaces:**
- Consumes: `MetadataSerializer.SerializeMetadata(IDictionary<string, MetadataValue>) → string` and `MetadataSerializer.DeserializeMetadata(string?) → Result<Dictionary<string, MetadataValue>, RagError>` from `Rag.NET.Abstractions.Serialization`.
- Produces: the constant `MetadataField = "metadata"`, and `private static IDictionary<string, MetadataValue> DecodeMetadata(RedisValue raw, string documentId, int chunkIndex)` — used by Task 3 as well.

- [ ] **Step 1: Rewrite the assertion that says this store persists none**

In `RedisChunkLookupTests.cs`, replace the whole `MetadataIsAbsentBecauseTheStorePersistsNone` test (and the `<remarks>` above it that says the store persists none) with:

```csharp
    /// <summary>
    /// The keyed read returns the metadata <c>StoreAsync</c> persisted (#513).
    /// </summary>
    /// <remarks>
    /// This test previously asserted the opposite — that metadata was absent because the store
    /// persisted none — and was written to fail on the day that changed rather than to be deleted
    /// then. This is that day.
    /// </remarks>
    [Fact]
    public async Task MetadataSurvivesTheRoundTrip()
    {
        await StoreAsync(new EmbeddedChunk
        {
            Chunk = new TextChunk
            {
                DocumentId = new DocumentId("doc-m"),
                ChunkIndex = 0,
                Text = "with metadata",
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["source"] = "unit-test",
                    ["page"] = 3,
                    ["draft"] = false,
                },
            },
            Embedding = new ReadOnlyMemory<float>([1f, 0f, 0f, 0f]),
        });

        var found = await _store.GetChunksAsync(
            [new ChunkKey("doc-m", 0)], TestContext.Current.CancellationToken);

        var only = Assert.Single(found);
        Assert.Equal("with metadata", only.Text);
        Assert.Equal(3, only.Metadata.Count);
        Assert.Equal(new MetadataValue("unit-test"), only.Metadata["source"]);
        Assert.Equal(MetadataValueKind.Number, only.Metadata["page"].Kind);
        Assert.Equal(3d, only.Metadata["page"].NumberValue);
        Assert.False(only.Metadata["draft"].BooleanValue);
    }

    /// <summary>
    /// A hash written before this store persisted metadata has no <c>metadata</c> field at all.
    /// That is not corruption: it reads as an empty dictionary, so an upgraded deployment keeps
    /// serving its existing chunks rather than throwing on every one of them.
    /// </summary>
    [Fact]
    public async Task AHashWithNoMetadataFieldReadsAsEmptyRatherThanThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = _connection.GetDatabase();
        await database.HashSetAsync(
            "lookup-idx:doc-legacy:0",
            [
                new HashEntry("document_id", "doc-legacy"),
                new HashEntry("chunk_index", 0),
                new HashEntry("text", "written by an older version"),
            ]);

        var found = await _store.GetChunksAsync([new ChunkKey("doc-legacy", 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal("written by an older version", only.Text);
        Assert.Empty(only.Metadata);
    }

    /// <summary>
    /// A <c>metadata</c> field that is present but will not deserialize is backend corruption, and
    /// this store throws naming the document and chunk — matching
    /// <c>WeaviateVectorStore</c>'s reviewed posture rather than the three stores that still return
    /// an empty dictionary (#521). On this store in particular, "reads as no metadata" is
    /// indistinguishable from the defect #513 fixed.
    /// </summary>
    [Fact]
    public async Task ACorruptMetadataFieldThrowsNamingTheChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = _connection.GetDatabase();
        await database.HashSetAsync(
            "lookup-idx:doc-corrupt:2",
            [
                new HashEntry("document_id", "doc-corrupt"),
                new HashEntry("chunk_index", 2),
                new HashEntry("text", "corrupt metadata"),
                new HashEntry("metadata", "{not json"),
            ]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.GetChunksAsync([new ChunkKey("doc-corrupt", 2)], ct));

        Assert.Contains("doc-corrupt", error.Message, StringComparison.Ordinal);
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
    }
```

`MetadataValue` and `MetadataValueKind` both live in `Rag.NET.Models`, which this file already imports. Add no usings unless the compiler asks.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q --filter "FullyQualifiedName~RedisChunkLookupTests"`
Expected: `MetadataSurvivesTheRoundTrip` FAILS (metadata count 0, expected 3). `ACorruptMetadataFieldThrowsNamingTheChunk` FAILS (no exception thrown). `AHashWithNoMetadataFieldReadsAsEmptyRatherThanThrowing` PASSES already — that is correct, it pins behaviour that must survive.

- [ ] **Step 3: Add the field constant**

In `RedisVectorStore.cs`, beside the existing constants (after `ChunkIndexField`, around line 50):

```csharp
    private const string MetadataField = "metadata";
```

- [ ] **Step 4: Write the blob in `StoreAsync`**

In `StoreAsync`, extend the `entries` array (currently four elements, around line 197):

```csharp
            var entries = new HashEntry[]
            {
                new(DocumentIdField, chunk.Chunk.DocumentId.Value),
                new(ChunkIndexField, chunk.Chunk.ChunkIndex),
                new(TextField, chunk.Chunk.Text),
                new(MetadataField, MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata)),
                new(EmbeddingField, ToBytes(chunk.Embedding.Span)),
            };
```

Add `using Rag.NET.Abstractions.Serialization;` to the file's usings if absent.

- [ ] **Step 5: Add the decoder and use it in `MapChunk`**

Add beside `MapChunk`:

```csharp
    /// <summary>
    /// Decodes the <c>metadata</c> hash field. A <b>missing</b> field is a hash written before this
    /// store persisted metadata and reads as empty; a field that is <b>present and corrupt</b>
    /// throws, because on this store a chunk that reads as having no metadata is indistinguishable
    /// from the defect #513 fixed. Matches <c>WeaviateVectorStore</c>'s reviewed posture (#521).
    /// </summary>
    /// <param name="raw">The raw field value, or a null <see cref="RedisValue"/> when absent.</param>
    /// <param name="documentId">Named in the exception, so a corrupt chunk is findable.</param>
    /// <param name="chunkIndex">Named in the exception.</param>
    /// <returns>The decoded metadata; empty when the field is absent.</returns>
    private static IDictionary<string, MetadataValue> DecodeMetadata(
        RedisValue raw, string documentId, int chunkIndex)
    {
        if (raw.IsNullOrEmpty)
            return new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

        var result = MetadataSerializer.DeserializeMetadata(raw.ToString());
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Redis hash '{documentId}:{chunkIndex.ToString(CultureInfo.InvariantCulture)}' " +
                $"has a corrupt {MetadataField} field.");
        }

        return result.Value;
    }
```

Then in `MapChunk`, collect the raw value in the existing loop and pass it through:

```csharp
        string text = string.Empty;
        string documentId = string.Empty;
        var chunkIndex = 0;
        RedisValue metadata = RedisValue.Null;

        foreach (var entry in hash)
        {
            var name = entry.Name.ToString();
            if (string.Equals(name, TextField, StringComparison.Ordinal))
                text = entry.Value.ToString();
            else if (string.Equals(name, DocumentIdField, StringComparison.Ordinal))
                documentId = entry.Value.ToString();
            else if (string.Equals(name, ChunkIndexField, StringComparison.Ordinal))
                chunkIndex = (int)entry.Value;
            else if (string.Equals(name, MetadataField, StringComparison.Ordinal))
                metadata = entry.Value;
        }

        return new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
            Metadata = DecodeMetadata(metadata, documentId, chunkIndex),
        };
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q --filter "FullyQualifiedName~RedisChunkLookupTests"`
Expected: all PASS, including the pre-existing negative-index and separator tests.

- [ ] **Step 7: Update the store's own remarks that now state something false**

`RedisVectorStore.cs`'s `GetChunksAsync` `<remarks>` contains a paragraph beginning **"These chunks carry no metadata, and neither do this store's search results."** Delete that paragraph — Task 3 makes the search half true as well, and leaving it would be a doc comment contradicting the code beneath it. Do the same for the class-level `<remarks>` in `RedisChunkLookupTests.cs` if it repeats the claim.

- [ ] **Step 8: Commit**

```bash
git add src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs tests/Rag.NET.VectorStores.Redis.Tests/RedisChunkLookupTests.cs
git commit -m "feat(redis): persist chunk metadata and return it from the keyed lookup (#513)"
```

---

### Task 3: Return metadata from the search path too

**Files:**
- Modify: `src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs` — `SearchAsync` (`ReturnFields` at line 238, result projection at line 255)
- Test: `tests/Rag.NET.VectorStores.Redis.Tests/RedisVectorStoreTests.cs`

**Interfaces:**
- Consumes: `DecodeMetadata` from Task 2.
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

Append to `RedisVectorStoreTests.cs`:

```csharp
    /// <summary>
    /// Search returns the same metadata the keyed lookup does. The two paths read different shapes
    /// — a projected document against a whole hash — so they can diverge, which is why 6.2.25
    /// pulled Qdrant's mapping into one place and why this is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task SearchAsync_ReturnsTheStoredMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        DocumentId = new DocumentId("doc-a"),
                        ChunkIndex = 0,
                        Text = "alpha",
                        Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                        {
                            ["tenant"] = "acme",
                            ["page"] = 7,
                        },
                    },
                    Embedding = new ReadOnlyMemory<float>([1f, 0f, 0f, 0f]),
                },
            ],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f }, new SearchOptions { TopK = 1 }, ct);

        var only = Assert.Single(results);
        Assert.Equal(new MetadataValue("acme"), only.Chunk.Metadata["tenant"]);
        Assert.Equal(7d, only.Chunk.Metadata["page"].NumberValue);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q --filter "FullyQualifiedName~SearchAsync_ReturnsTheStoredMetadata"`
Expected: FAIL — `KeyNotFoundException` on `Metadata["tenant"]`, because the dictionary is empty.

- [ ] **Step 3: Project the field and decode it**

In `SearchAsync`, add `MetadataField` to the returned fields:

```csharp
            .ReturnFields(DocumentIdField, ChunkIndexField, TextField, MetadataField, ScoreField)
```

and in the result loop, build the chunk with metadata:

```csharp
            var documentId = document[DocumentIdField].ToString();
            var chunkIndex = (int)document[ChunkIndexField];

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = document[TextField].ToString(),
                    DocumentId = new DocumentId(documentId),
                    ChunkIndex = chunkIndex,
                    Metadata = DecodeMetadata(document[MetadataField], documentId, chunkIndex),
                },
                Score = score,
            });
```

- [ ] **Step 4: Run the whole Redis suite**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs tests/Rag.NET.VectorStores.Redis.Tests/RedisVectorStoreTests.cs
git commit -m "feat(redis): return stored metadata from search, not only the keyed lookup (#513)"
```

---

### Task 4: The filter token — kind prefix plus Base64Url, unit-tested without a container

**Files:**
- Modify: `src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs` — new internal statics beside `EscapeTag` (line 426)
- Test: `tests/Rag.NET.VectorStores.Redis.Tests/RedisMetadataTokenTests.cs` (create)

**Interfaces:**
- Consumes: `MetadataValue.Kind`, `MetadataValue.ToString()`, `System.Buffers.Text.Base64Url.EncodeToString`.
- Produces: `internal static string MetadataToken(MetadataValue value)` and `internal static string MetadataFieldName(string key)` — both used by Tasks 5 and 6.

- [ ] **Step 1: Write the failing tests**

Create `tests/Rag.NET.VectorStores.Redis.Tests/RedisMetadataTokenTests.cs`:

```csharp
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.VectorStores.Redis.Tests;

/// <summary>
/// The TAG token a filterable metadata value is stored and queried as. No container: this is pure
/// encoding. The RediSearch behaviour it protects against — separator splitting and case folding —
/// is exercised against a real server by the metadata filter tests.
/// </summary>
public sealed class RedisMetadataTokenTests
{
    /// <summary>
    /// The kind prefix is what stops a filter of the string "3" matching the number 3, which
    /// <c>SearchOptions.MetadataFilter</c> requires in as many words.
    /// </summary>
    [Fact]
    public void ANumberAndAStringOfTheSameTextProduceDifferentTokens()
    {
        Assert.NotEqual(
            RedisVectorStore.MetadataToken(new MetadataValue(3d)),
            RedisVectorStore.MetadataToken(new MetadataValue("3")));
    }

    /// <summary>
    /// A TAG field splits its value on <c>,</c>, so a comma inside a value would store as two tags
    /// and match neither. Base64Url's alphabet contains no comma, which is why the value is encoded
    /// rather than escaped — the same argument 6.2.30 made for the Azure key.
    /// </summary>
    [Fact]
    public void AValueContainingACommaEncodesWithoutOne()
    {
        var token = RedisVectorStore.MetadataToken(new MetadataValue("acme, inc"));

        Assert.DoesNotContain(',', token);
    }

    /// <summary>Two values differing only in case must not collide.</summary>
    [Fact]
    public void CaseIsSignificant()
    {
        Assert.NotEqual(
            RedisVectorStore.MetadataToken(new MetadataValue("ACME")),
            RedisVectorStore.MetadataToken(new MetadataValue("acme")));
    }

    /// <summary>
    /// Every kind round-trips through one canonical textual form — <c>MetadataValue.ToString()</c>
    /// — so the write path and the filter path cannot drift.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("with spaces and : colons")]
    [InlineData("")]
    public void AStringTokenIsStableForTheSameValue(string value)
    {
        Assert.Equal(
            RedisVectorStore.MetadataToken(new MetadataValue(value)),
            RedisVectorStore.MetadataToken(new MetadataValue(value)));
    }

    /// <summary>The field name is namespaced so a metadata key named <c>text</c> is representable.</summary>
    [Fact]
    public void TheFieldNameIsNamespaced()
    {
        Assert.Equal("md_text", RedisVectorStore.MetadataFieldName("text"));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q --filter "FullyQualifiedName~RedisMetadataTokenTests"`
Expected: compile error — `MetadataToken` and `MetadataFieldName` do not exist. That is the failure.

- [ ] **Step 3: Implement both helpers**

In `RedisVectorStore.cs`, beside `EscapeTag`:

```csharp
    /// <summary>The hash field and index attribute a filterable metadata key is stored under.</summary>
    /// <param name="key">The metadata key.</param>
    /// <returns>The namespaced field name, e.g. <c>md_tenant</c>.</returns>
    internal static string MetadataFieldName(string key) => MetadataFieldPrefix + key;

    /// <summary>
    /// The TAG token one filterable metadata value is stored and queried as: its kind, a colon,
    /// then the Base64Url of its canonical text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The kind prefix is the typed half of the contract.</b> <c>SearchOptions.MetadataFilter</c>
    /// matches typed — a filter of <c>3</c> must not match the string <c>"3"</c> — and without the
    /// prefix both render as <c>3</c>.
    /// </para>
    /// <para>
    /// <b>The value is encoded, not escaped, because a TAG field splits on a separator</b>
    /// (<c>,</c> by default). No separator character is safe when a value can contain any
    /// character, and Base64Url's alphabet contains none of them. Dropping the encoding is the
    /// simplification that passes every test whose values are well-behaved words.
    /// </para>
    /// <para>
    /// <b>The text comes from <see cref="MetadataValue.ToString"/></b>, which already emits
    /// invariant numbers, <c>true</c>/<c>false</c>, and the shared date format — so the write path
    /// and the filter path are one accessor and cannot drift.
    /// </para>
    /// </remarks>
    /// <param name="value">The metadata value.</param>
    /// <returns>The token.</returns>
    internal static string MetadataToken(MetadataValue value)
    {
        var kind = value.Kind switch
        {
            MetadataValueKind.Number => 'n',
            MetadataValueKind.Boolean => 'b',
            MetadataValueKind.DateTimeOffset => 'd',
            _ => 's',
        };

        return kind + ":" + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value.ToString()));
    }
```

Add the constant beside the other field names:

```csharp
    private const string MetadataFieldPrefix = "md_";
```

and the usings `System.Buffers.Text;` and `System.Text;` if absent.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q --filter "FullyQualifiedName~RedisMetadataTokenTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs tests/Rag.NET.VectorStores.Redis.Tests/RedisMetadataTokenTests.cs
git commit -m "feat(redis): encode filterable metadata as kind-prefixed Base64Url tags (#513)"
```

---

### Task 5: Declare filterable keys, and write their fields

**Files:**
- Modify: `src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs` — constructors (lines 63-88), `CreateCollectionAsync` (line 108), `StoreAsync` (line 189)
- Modify: `src/Rag.NET.VectorStores.Redis/RedisBuilderExtensions.cs` — both `UseRedis` overloads
- Test: `tests/Rag.NET.VectorStores.Redis.Tests/RedisMetadataFilterTests.cs` (create)

**Interfaces:**
- Consumes: `MetadataToken`, `MetadataFieldName` from Task 4.
- Produces: constructor parameter `IReadOnlyList<string>? filterableMetadataKeys = null` on both public constructors and both `UseRedis` overloads; `private readonly IReadOnlyList<string> _filterableKeys`.

- [ ] **Step 1: Write the failing test**

Create `tests/Rag.NET.VectorStores.Redis.Tests/RedisMetadataFilterTests.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Rag.NET.VectorStores.Redis.Tests;

/// <summary>
/// Metadata filtering against real Redis (#513). Before this, <c>SearchAsync</c> never read
/// <c>MetadataFilter</c> at all and a filtered search silently returned unfiltered results.
/// </summary>
public sealed class RedisMetadataFilterTests : IAsyncLifetime
{
    private const int Dimensions = 4;

    private readonly RedisContainer _container =
        new RedisBuilder("redis/redis-stack-server:latest").Build();

    private RedisVectorStore _store = null!;
    private IConnectionMultiplexer _connection = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        _store = new RedisVectorStore(
            _connection, "filter-idx", Dimensions, filterableMetadataKeys: ["tenant", "page"]);
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _connection.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static EmbeddedChunk Chunk(
        string documentId, string text, float[] embedding, params (string Key, MetadataValue Value)[] metadata)
    {
        var dictionary = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
            dictionary[key] = value;

        return new EmbeddedChunk
        {
            Chunk = new TextChunk
            {
                DocumentId = new DocumentId(documentId),
                ChunkIndex = 0,
                Text = text,
                Metadata = dictionary,
            },
            Embedding = new ReadOnlyMemory<float>(embedding),
        };
    }

    /// <summary>
    /// <b>This discriminates server-side filtering from client-side.</b> <c>TopK = 1</c> with the
    /// NEAREST chunk excluded by the filter: only a filter applied inside the query can return the
    /// farther chunk. A store that filtered after fetching would return nothing, and one that
    /// ignored the filter — the defect this fixes — would return the near chunk.
    /// </summary>
    [Fact]
    public async Task AFilterIsAppliedInsideTheQuery_NotAfterTheTopKCut()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("doc-near", "near", [1f, 0f, 0f, 0f], ("tenant", "other")),
                Chunk("doc-far", "far", [0f, 1f, 0f, 0f], ("tenant", "acme")),
            ],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 1,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["tenant"] = "acme",
                },
            },
            ct);

        var only = Assert.Single(results);
        Assert.Equal("doc-far", only.Chunk.DocumentId.Value);
    }

    /// <summary>A filter of the string "3" must not match metadata written as the number 3.</summary>
    [Fact]
    public async Task AStringFilterDoesNotMatchANumber()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-n", "numeric", [1f, 0f, 0f, 0f], ("page", 3))], ct);

        var asString = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["page"] = "3",
                },
            },
            ct);

        Assert.Empty(asString);

        var asNumber = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["page"] = 3,
                },
            },
            ct);

        Assert.Single(asNumber);
    }

    /// <summary>
    /// A value carrying the characters RediSearch treats as syntax, and the comma a TAG field
    /// splits on. Without escaping the query is malformed or injected; without Base64Url the value
    /// stores as two tags.
    /// </summary>
    [Fact]
    public async Task AValueContainingTagSyntaxAndACommaStillMatchesItself()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [Chunk("doc-x", "awkward", [1f, 0f, 0f, 0f], ("tenant", "acme, inc - eu:west"))],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["tenant"] = "acme, inc - eu:west",
                },
            },
            ct);

        var only = Assert.Single(results);
        Assert.Equal("doc-x", only.Chunk.DocumentId.Value);
    }

    /// <summary>Case is significant: TAG fields fold case unless declared not to.</summary>
    [Fact]
    public async Task AFilterDoesNotMatchAValueDifferingOnlyInCase()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-c", "cased", [1f, 0f, 0f, 0f], ("tenant", "ACME"))], ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["tenant"] = "acme",
                },
            },
            ct);

        Assert.Empty(results);
    }

    /// <summary>An empty filter dictionary is not a filter, and must not narrow the page.</summary>
    [Fact]
    public async Task AnEmptyFilterReturnsTheWholePage()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("doc-1", "one", [1f, 0f, 0f, 0f], ("tenant", "acme")),
                Chunk("doc-2", "two", [0f, 1f, 0f, 0f], ("tenant", "other")),
            ],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
            },
            ct);

        Assert.Equal(2, results.Count);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q --filter "FullyQualifiedName~RedisMetadataFilterTests"`
Expected: compile error — the constructor has no `filterableMetadataKeys` parameter. That is the failure for this task; the query behaviour is Task 6's.

- [ ] **Step 3: Thread the declared keys through the constructors**

Both public constructors gain a trailing optional parameter and pass it down; the private constructor stores it:

```csharp
    public RedisVectorStore(
        string configuration,
        string indexName = "ragnet-idx",
        int vectorDimensions = 1536,
        IReadOnlyList<string>? filterableMetadataKeys = null)
        : this(
            ConnectionMultiplexer.Connect(configuration),
            indexName,
            vectorDimensions,
            ownsConnection: true,
            filterableMetadataKeys)
    {
    }
```

Mirror the same shape on the `IConnectionMultiplexer` overload with `ownsConnection: false`, and in the private constructor:

```csharp
        _filterableKeys = filterableMetadataKeys is null
            ? []
            : [.. filterableMetadataKeys];
```

with the field:

```csharp
    private readonly IReadOnlyList<string> _filterableKeys;
```

- [ ] **Step 4: Declare the TAG attributes in the schema**

In `CreateCollectionAsync`, after `.AddTextField(TextField)` and before the vector field:

```csharp
        var schema = new Schema()
            .AddTagField(DocumentIdField)
            .AddNumericField(ChunkIndexField)
            .AddTextField(TextField);

        foreach (var key in _filterableKeys)
        {
            // caseSensitive: MetadataValue compares strings ordinally, and a TAG field folds case
            // by default — without this, "ACME" would answer a filter for "acme".
            _ = schema.AddTagField(MetadataFieldName(key), caseSensitive: true);
        }

        _ = schema.AddVectorField(
            EmbeddingField,
            Schema.VectorField.VectorAlgo.HNSW,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["TYPE"] = "FLOAT32",
                ["DIM"] = vectorDimensions.ToString(CultureInfo.InvariantCulture),
                ["DISTANCE_METRIC"] = "COSINE",
            });
```

If `AddTagField`'s signature does not accept `caseSensitive` as a named argument in this position, check its overloads and pass it positionally — do **not** drop it. A case-folding TAG field is a silent wrong-answer, and `AFilterDoesNotMatchAValueDifferingOnlyInCase` exists to catch exactly that.

- [ ] **Step 5: Write the per-key fields in `StoreAsync`**

Replace the fixed-size `entries` array with a list that appends declared keys present on the chunk:

```csharp
            var entries = new List<HashEntry>(5 + _filterableKeys.Count)
            {
                new(DocumentIdField, chunk.Chunk.DocumentId.Value),
                new(ChunkIndexField, chunk.Chunk.ChunkIndex),
                new(TextField, chunk.Chunk.Text),
                new(MetadataField, MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata)),
                new(EmbeddingField, ToBytes(chunk.Embedding.Span)),
            };

            foreach (var key in _filterableKeys)
            {
                if (chunk.Chunk.Metadata.TryGetValue(key, out var value))
                    entries.Add(new HashEntry(MetadataFieldName(key), MetadataToken(value)));
            }

            await database.HashSetAsync(
                    KeyFor(chunk.Chunk.DocumentId.Value, chunk.Chunk.ChunkIndex),
                    [.. entries])
                .ConfigureAwait(false);
```

A key absent from a chunk's metadata writes no field, so a filter on it does not match that chunk — which is what "matches every key/value pair exactly" means.

- [ ] **Step 6: Surface the parameter on both `UseRedis` overloads**

In `RedisBuilderExtensions.cs`, add to both:

```csharp
    /// <param name="filterableMetadataKeys">
    /// Metadata keys that may be used in <c>MetadataFilter</c>. They become case-sensitive TAG
    /// attributes in the index, so they must be known when the index is created. **A filter naming
    /// a key that is not declared here throws** rather than returning an unfiltered page — Redis is
    /// the only backend in this library that requires the declaration, because RediSearch filters
    /// only on attributes the schema names.
    /// </param>
```

and pass it to the constructor. Keep both existing parameter orders intact so current callers compile unchanged.

- [ ] **Step 7: Run — expect the filter tests to still fail, for the right reason**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q`
Expected: the project compiles; `RedisMetadataFilterTests` still FAIL because `SearchAsync` does not build a filter clause yet. Every other test PASSES. **If any Task 2 or Task 3 test broke here, the `entries` rewrite dropped a field — fix that before continuing.**

- [ ] **Step 8: Commit**

```bash
git add src/Rag.NET.VectorStores.Redis/
git commit -m "feat(redis): declare filterable metadata keys as case-sensitive tag fields (#513)"
```

---

### Task 6: Build the filter clause, and throw on an undeclared key

**Files:**
- Modify: `src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs` — `SearchAsync` query construction (line 235)
- Test: `tests/Rag.NET.VectorStores.Redis.Tests/RedisMetadataFilterTests.cs` (append)

**Interfaces:**
- Consumes: `MetadataToken`, `MetadataFieldName`, `EscapeTag`, `_filterableKeys`.
- Produces: `private string BuildFilterPrefix(IDictionary<string, MetadataValue>? filter)` returning `"*"` when there is no filter.

- [ ] **Step 1: Add the undeclared-key test**

Append to `RedisMetadataFilterTests.cs`:

```csharp
    /// <summary>
    /// <b>An undeclared key throws rather than returning an unfiltered page.</b> Returning
    /// everything is what this store did before #513 and is the worst available answer: the caller
    /// asked to narrow and got the opposite, silently. The message names the key and the declared
    /// set so the fix is obvious from the exception alone.
    /// </summary>
    [Fact]
    public async Task AFilterOnAnUndeclaredKeyThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-u", "undeclared", [1f, 0f, 0f, 0f], ("tenant", "acme"))], ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.SearchAsync(
                new[] { 1f, 0f, 0f, 0f },
                new SearchOptions
                {
                    TopK = 5,
                    MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["undeclared"] = "x",
                    },
                },
                ct));

        Assert.Contains("undeclared", error.Message, StringComparison.Ordinal);
        Assert.Contains("tenant", error.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify the whole filter class fails**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q --filter "FullyQualifiedName~RedisMetadataFilterTests"`
Expected: `AFilterIsAppliedInsideTheQuery_NotAfterTheTopKCut` FAILS returning `doc-near` (the filter is ignored — this is the defect, observed); `AFilterOnAnUndeclaredKeyThrows` FAILS with no exception; `AnEmptyFilterReturnsTheWholePage` PASSES already.

- [ ] **Step 3: Implement the clause builder**

Add to `RedisVectorStore.cs`:

```csharp
    /// <summary>
    /// The RediSearch pre-filter for a metadata filter, or <c>*</c> when there is none.
    /// </summary>
    /// <remarks>
    /// Conditions are AND-ed by juxtaposition, matching <c>SearchOptions.MetadataFilter</c>'s
    /// "matches every key/value pair". Each value is tokenised (kind + Base64Url) and then escaped
    /// for TAG syntax — the token's own colon is syntax inside a query even though nothing else in
    /// it is.
    /// </remarks>
    /// <param name="filter">The requested filter; null or empty means no filtering.</param>
    /// <returns>The query prefix.</returns>
    /// <exception cref="InvalidOperationException">A key was not declared filterable.</exception>
    private string BuildFilterPrefix(IDictionary<string, MetadataValue>? filter)
    {
        if (filter is not { Count: > 0 })
            return "*";

        var clause = new System.Text.StringBuilder("(");
        var first = true;
        foreach (var pair in filter)
        {
            if (!_filterableKeys.Contains(pair.Key, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Metadata key '{pair.Key}' is not filterable on this Redis index. RediSearch " +
                    $"filters only on attributes the schema declares, so filterable keys are fixed " +
                    $"when the index is created. Declared: " +
                    $"[{string.Join(", ", _filterableKeys)}]. Add '{pair.Key}' to " +
                    $"filterableMetadataKeys and recreate the index.");
            }

            if (!first)
                _ = clause.Append(' ');

            first = false;
            _ = clause.Append('@')
                .Append(MetadataFieldName(pair.Key))
                .Append(":{")
                .Append(EscapeTag(MetadataToken(pair.Value)))
                .Append('}');
        }

        return clause.Append(')').ToString();
    }
```

- [ ] **Step 4: Use it in the query**

Replace the query construction's leading `*`:

```csharp
        var filterPrefix = BuildFilterPrefix(options.MetadataFilter);
        var query = new Query($"{filterPrefix}=>[KNN {options.TopK.ToString(CultureInfo.InvariantCulture)} @{EmbeddingField} $vec AS {ScoreField}]")
```

The rest of the builder chain is unchanged.

- [ ] **Step 5: Run the filter tests**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q --filter "FullyQualifiedName~RedisMetadataFilterTests"`
Expected: all PASS.

- [ ] **Step 6: Run the whole Redis suite**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs tests/Rag.NET.VectorStores.Redis.Tests/RedisMetadataFilterTests.cs
git commit -m "feat(redis): honour MetadataFilter server-side, and throw on an undeclared key (#513)"
```

---

### Task 7: The `FT.INFO` guard — an index that predates the declared keys

This is the load-bearing guard. `InitializeAsync` deliberately leaves an existing index alone, so without this check the whole design holds only on a freshly created index.

**Files:**
- Modify: `src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs` — `InitializeAsync` (line 98)
- Test: `tests/Rag.NET.VectorStores.Redis.Tests/RedisMetadataFilterTests.cs` (append)

**Interfaces:**
- Consumes: `Database.FT().InfoAsync(indexName)` → `InfoResult`, whose `Attributes` lists the declared attributes.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

Append to `RedisMetadataFilterTests.cs`:

```csharp
    /// <summary>
    /// <b>An index created before a key was declared filterable must fail loudly at startup.</b>
    /// <c>InitializeAsync</c> leaves an existing index alone — dropping it would discard every
    /// stored vector — so on an upgraded deployment the <c>md_*</c> attributes are simply absent,
    /// and every filtered query would fail at the server or, worse, be answered wrongly. Without
    /// this check the guarantee holds only on a fresh index, which is not where the defect lives.
    /// </summary>
    [Fact]
    public async Task AnIndexMissingADeclaredKeyFailsInitialisation()
    {
        var ct = TestContext.Current.CancellationToken;

        // An index created with NO filterable keys, standing in for one written by an older version.
        using var older = new RedisVectorStore(_connection, "legacy-idx", Dimensions);
        await older.InitializeAsync(ct);

        using var upgraded = new RedisVectorStore(
            _connection, "legacy-idx", Dimensions, filterableMetadataKeys: ["tenant"]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => upgraded.InitializeAsync(ct));

        Assert.Contains("tenant", error.Message, StringComparison.Ordinal);
        Assert.Contains("legacy-idx", error.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q --filter "FullyQualifiedName~AnIndexMissingADeclaredKey"`
Expected: FAIL — no exception; initialisation silently accepts the stale index.

- [ ] **Step 3: Implement the check**

Rewrite `InitializeAsync`:

```csharp
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!await CollectionExistsAsync(_indexName, cancellationToken).ConfigureAwait(false))
        {
            await CreateCollectionAsync(_indexName, _vectorDimensions, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await VerifyFilterableKeysAreIndexedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Throws when the live index does not declare every configured filterable key.
    /// </summary>
    /// <remarks>
    /// An existing index is never altered or dropped here, so a key added to the configuration
    /// after the index was built would otherwise be silently unfilterable. Failing at
    /// initialisation turns a wrong query result into a startup error naming the key.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A declared key is not an index attribute.</exception>
    private async Task VerifyFilterableKeysAreIndexedAsync()
    {
        if (_filterableKeys.Count == 0)
            return;

        var info = await Database.FT().InfoAsync(_indexName).ConfigureAwait(false);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in info.Attributes)
        {
            foreach (var value in attribute.Values)
                _ = declared.Add(value.ToString());
        }

        foreach (var key in _filterableKeys)
        {
            var field = MetadataFieldName(key);
            if (!declared.Contains(field))
            {
                throw new InvalidOperationException(
                    $"Redis index '{_indexName}' does not declare the attribute '{field}', so " +
                    $"filtering on metadata key '{key}' cannot work. The index predates this " +
                    $"configuration; recreate it and re-ingest.");
            }
        }
    }
```

**`InfoResult.Attributes`' exact element shape is not assumed above** — it is read as a collection of key/value maps and every value is collected, so the attribute's identifier is found wherever the client puts it. If the type does not expose `.Values`, print one element (`Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(info.Attributes))` in a scratch test) and adapt the two inner lines to the real shape. Do not guess a property name that does not compile.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q`
Expected: all PASS. **Watch the other fixtures:** they call `InitializeAsync` on a fresh index every time, so the new path must not throw for them.

- [ ] **Step 5: Commit**

```bash
git add src/Rag.NET.VectorStores.Redis/RedisVectorStore.cs tests/Rag.NET.VectorStores.Redis.Tests/RedisMetadataFilterTests.cs
git commit -m "feat(redis)!: fail initialisation when an index predates its filterable keys (#513)"
```

---

### Task 8: Mutation sweep

Every backend in #318 ran this and it found something every time — including, on Weaviate, a *missing* guard that eight passing tests did not.

**Files:**
- Modify: nothing permanently. Each mutation is applied, tested, then reverted with `git checkout --`.

**Interfaces:**
- Consumes: the finished implementation.
- Produces: a table of mutation → catching test, for the phase's ROADMAP entry.

- [ ] **Step 1: Run each mutation, record which test catches it**

For each row: apply the change, run `dotnet test tests/Rag.NET.VectorStores.Redis.Tests -v q`, record the failing test names, then `git checkout -- src/`.

| # | mutation | expected catcher |
| --- | --- | --- |
| 1 | Delete the kind prefix from `MetadataToken` (return only the Base64Url) | `ANumberAndAStringOfTheSameTextProduceDifferentTokens`, `AStringFilterDoesNotMatchANumber` |
| 2 | Replace `Base64Url.EncodeToString(...)` with `value.ToString()` | `AValueContainingACommaEncodesWithoutOne`, `AValueContainingTagSyntaxAndACommaStillMatchesItself` |
| 3 | Drop `caseSensitive: true` from `AddTagField` | `AFilterDoesNotMatchAValueDifferingOnlyInCase` |
| 4 | Drop `EscapeTag` from `BuildFilterPrefix` | `AValueContainingTagSyntaxAndACommaStillMatchesItself` |
| 5 | Return `"*"` unconditionally from `BuildFilterPrefix` (the original defect) | `AFilterIsAppliedInsideTheQuery_NotAfterTheTopKCut` |
| 6 | Treat an empty filter as a filter (drop the `Count: > 0` check) | `AnEmptyFilterReturnsTheWholePage` |
| 7 | Skip the undeclared-key throw and ignore the key instead | `AFilterOnAnUndeclaredKeyThrows` |
| 8 | Delete `VerifyFilterableKeysAreIndexedAsync`'s call site | `AnIndexMissingADeclaredKeyFailsInitialisation` |
| 9 | Drop `MetadataField` from `ReturnFields` | `SearchAsync_ReturnsTheStoredMetadata` |
| 10 | Return empty instead of throwing on a corrupt blob | `ACorruptMetadataFieldThrowsNamingTheChunk` |
| 11 | Throw instead of returning empty for a *missing* metadata field | `AHashWithNoMetadataFieldReadsAsEmptyRatherThanThrowing` |
| 12 | Make `chunk_index` unsigned (`Math.Abs`) in `KeyFor` | the existing negative-index test — **and, on six backends, nothing else** |

- [ ] **Step 2: Act on any mutation that survives**

A surviving mutation means a missing test, not an acceptable gap. Write the test that catches it, then re-run. **Do not record a mutation as "caught" without naming the test that failed** — on Weaviate, this is exactly where the missing escaping guard was found.

- [ ] **Step 3: Verify the working tree is clean**

Run: `git status --short`
Expected: empty. Any leftover mutation is a defect about to be committed.

---

### Task 9: Documentation, and the two claims that become true

**Files:**
- Modify: `src/Rag.NET.VectorStores.Redis/README.md`
- Modify: `docs/guide/` — the vector-store page covering Redis (find with `grep -rln "UseRedis" docs/`)
- Modify: `docs/planning/ROADMAP.md` — the Phase 6.2.31 block

**Interfaces:** none.

- [ ] **Step 1: Document the declaration step in the package README**

State plainly: filterable metadata keys must be declared when the store is constructed; they become case-sensitive TAG attributes; **an undeclared key throws**; and an index built before a key was declared fails initialisation and must be recreated. Show a `UseRedis(..., filterableMetadataKeys: ["tenant"])` example. Say that Redis is the only backend in this library with this requirement, and why — RediSearch filters only on schema-declared attributes.

- [ ] **Step 2: Check the two contract claims now hold**

`TextChunk.Metadata`'s remarks say *"every store persists and filters on that type"* (`TextChunk.cs:61-62`). After this phase Redis persists, and filters on declared keys. **Amend that sentence to name the Redis qualifier** rather than leaving a claim that is true only with a footnote nobody has written.

- [ ] **Step 3: Record the outcome in the ROADMAP phase block**

Update `### Phase 6.2.31` in `docs/planning/ROADMAP.md`: status to `complete <date>`, add `**Completed:**`, add the mutation table from Task 8 and what the phase actually found. Follow the shape of the 6.2.24-6.2.30 blocks.

- [ ] **Step 4: Run the full solution's fast tier**

Run: `dotnet test -v q --filter "Category!=Integration"` — and if that filter does not match this repo's convention, enumerate the test projects (`ls tests/`) and run each. **Do not assume `dotnet build` covers the packaging guards; `pack-validate` is separate.**
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Rag.NET.VectorStores.Redis/README.md docs/ src/Rag.NET.Abstractions/Models/TextChunk.cs
git commit -m "docs(redis): document declared filterable metadata keys and the recreate-on-upgrade break"
```

---

### Task 10: Pre-push review and PR

- [ ] **Step 1: Run every test project**

Enumerate them — `ls tests/` — and run each. Do not rely on a remembered list.

- [ ] **Step 2: Run the pre-push review skill**

Use `pre-push-review`. It covers plan adherence, commit hygiene and code quality, which `audit-milestone` explicitly does not.

- [ ] **Step 3: Open the PR**

Title: `feat(redis)!: store, return and filter on chunk metadata (#513)`. Breaking: the index schema changes, so an existing index must be recreated and re-ingested. The body should carry the mutation table and state what the phase found beyond #513's text — that `SearchAsync` ignored `MetadataFilter` entirely.

- [ ] **Step 4: Stop**

Do not merge. The merge is the operator's.
