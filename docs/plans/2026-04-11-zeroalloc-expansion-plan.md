# ZeroAlloc Package Expansion (Group 1) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Expand ZeroAlloc-Net package usage across Rag.NET — add `ProviderId`/`EntryId`/`SessionId` value objects, source-generated JSON serialization, and resilient metadata deserialization with `Result<T, RagError>`.

**Architecture:** Three changes to the storage/model layer: (1) value objects replace raw string identifiers following the existing `DocumentId` pattern, (2) `RagJsonSerializerContext` eliminates reflection-based JSON serialization, (3) `MetadataSerializer` wraps deserialization in `Result<T, RagError>` with a default log-and-continue policy. All changes are source-breaking but the project is not public yet.

**Tech Stack:** `ZeroAlloc.ValueObjects`, `ZeroAlloc.Results`, `System.Text.Json` source generators, xunit.v3, NSubstitute.

---

## Context for the implementer

### Existing `DocumentId` pattern (follow this exactly)

`src/Rag.NET.Abstractions/Models/DocumentId.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroAlloc.ValueObjects;

namespace Rag.NET.Models;

[JsonConverter(typeof(DocumentIdJsonConverter))]
[ValueObject]
public sealed partial class DocumentId
{
    private readonly string _value;

    [EqualityMember]
    public string Value => _value;

    public DocumentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public override string ToString() => _value;

    public static implicit operator string(DocumentId id) => id._value;
    public static explicit operator DocumentId(string value) => new(value);

    private sealed class DocumentIdJsonConverter : JsonConverter<DocumentId>
    {
        public override DocumentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, DocumentId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value._value);
    }
}
```

### `RagError` discriminated union

```csharp
public abstract record RagError
{
    public sealed record ValidationFailed(IReadOnlyList<ValidationFailure> Failures) : RagError;
    public sealed record NoParserFound(string ContentType) : RagError;
    public sealed record StorageFailed(Exception Inner) : RagError;
    public sealed record NonSeekableStream() : RagError;
}
```

### InternalsVisibleTo pattern (csproj)

```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
    <_Parameter1>TargetAssemblyName</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

### Test conventions

- `TestContext.Current.CancellationToken` for all async tests
- xunit.v3 `[Fact]` / `[Theory]`
- `NSubstitute` for mocks
- Pattern: `MethodName_Condition_ExpectedBehavior`
- Tests in `tests/Rag.NET.Tests/`

### Key interfaces being modified

`IContentHashStore` (5 methods with `string providerId`, `string entryId`):
```csharp
Task<string?> GetETagAsync(string providerId, string entryId, CancellationToken cancellationToken = default);
Task<string?> GetHashAsync(string providerId, string entryId, CancellationToken cancellationToken = default);
Task SetAsync(string providerId, string entryId, string? etag, string hash, CancellationToken cancellationToken = default);
Task<IReadOnlySet<string>> GetAllIdsAsync(string providerId, CancellationToken cancellationToken = default);
Task RemoveAsync(string providerId, string entryId, CancellationToken cancellationToken = default);
```

`FileEntry` record:
```csharp
public sealed record FileEntry(
    string Id,
    string FileName,
    Func<CancellationToken, Task<Stream>> OpenContentAsync,
    string? ETag = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
```

`IConversationMemory.StoreAsync`:
```csharp
Task StoreAsync(string userMessage, string assistantMessage, string sessionId, CancellationToken cancellationToken = default);
```

---

## Task 1: Create `ProviderId`, `EntryId`, `SessionId` value objects

**Files:**
- Create: `src/Rag.NET.Abstractions/Models/ProviderId.cs`
- Create: `src/Rag.NET.Abstractions/Models/EntryId.cs`
- Create: `src/Rag.NET.Abstractions/Models/SessionId.cs`

**Step 1: Create `ProviderId.cs`**

Follow the `DocumentId` pattern exactly:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroAlloc.ValueObjects;

namespace Rag.NET.Models;

[JsonConverter(typeof(ProviderIdJsonConverter))]
[ValueObject]
public sealed partial class ProviderId
{
    private readonly string _value;

    [EqualityMember]
    public string Value => _value;

    public ProviderId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public override string ToString() => _value;

    public static implicit operator string(ProviderId id) => id._value;
    public static explicit operator ProviderId(string value) => new(value);

    private sealed class ProviderIdJsonConverter : JsonConverter<ProviderId>
    {
        public override ProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, ProviderId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value._value);
    }
}
```

**Step 2: Create `EntryId.cs`**

Same pattern, replace `ProviderId` with `EntryId` throughout.

**Step 3: Create `SessionId.cs`**

Same pattern, replace `ProviderId` with `SessionId` throughout.

**Step 4: Build to verify**

```
dotnet build src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj -v q
```

Expected: builds successfully.

**Step 5: Commit**

```bash
git add src/Rag.NET.Abstractions/Models/ProviderId.cs src/Rag.NET.Abstractions/Models/EntryId.cs src/Rag.NET.Abstractions/Models/SessionId.cs
git commit -m "feat(abstractions): add ProviderId, EntryId, SessionId value objects"
```

---

## Task 2: Migrate `IContentHashStore` + `SqliteContentHashStore`

**Files:**
- Modify: `src/Rag.NET.Abstractions/Abstractions/IContentHashStore.cs`
- Modify: `src/Rag.NET/Storage/SqliteContentHashStore.cs`

**Step 1: Update `IContentHashStore` interface**

Change all 5 method signatures from `string providerId` → `ProviderId providerId` and `string entryId` → `EntryId entryId`. Add `using Rag.NET.Models;` at the top.

```csharp
using Rag.NET.Models;

namespace Rag.NET.Abstractions;

public interface IContentHashStore
{
    Task<string?> GetETagAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default);
    Task<string?> GetHashAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default);
    Task SetAsync(ProviderId providerId, EntryId entryId, string? etag, string hash, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> GetAllIdsAsync(ProviderId providerId, CancellationToken cancellationToken = default);
    Task RemoveAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default);
}
```

Note: `GetAllIdsAsync` still returns `IReadOnlySet<string>` — the returned IDs are entry IDs. Change return type to `IReadOnlySet<EntryId>` as well.

**Step 2: Update `SqliteContentHashStore`**

Read `src/Rag.NET/Storage/SqliteContentHashStore.cs` first. In each method:
- Parameter types change from `string` to `ProviderId`/`EntryId`
- Where values are passed to SQL parameters, use `.Value`: `command.Parameters.AddWithValue("$pid", providerId.Value);`
- `GetAllIdsAsync`: wrap returned strings in `new EntryId(reader.GetString(0))`

**Step 3: Build to find remaining compile errors**

```
dotnet build src/Rag.NET/Rag.NET.csproj -v q 2>&1
```

Expected: compile errors in `RagPipelineExtensions.cs` and tests — these are fixed in later tasks.

**Step 4: Run existing hash store tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "SqliteContentHashStoreTests" -v q 2>&1
```

Fix any compile errors in the test file by wrapping string literals: `"provider-1"` → `new ProviderId("provider-1")`, `"entry-1"` → `new EntryId("entry-1")`.

Expected: all tests pass after migration.

**Step 5: Commit**

```bash
git add src/Rag.NET.Abstractions/Abstractions/IContentHashStore.cs src/Rag.NET/Storage/SqliteContentHashStore.cs tests/
git commit -m "refactor(abstractions): migrate IContentHashStore to ProviderId/EntryId value objects"
```

---

## Task 3: Migrate `FileEntry` + `RagPipelineExtensions`

**Files:**
- Modify: `src/Rag.NET/DataProviders/FileEntry.cs`
- Modify: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs`
- Modify: any DataProvider files that construct `FileEntry` with a string `Id`

**Step 1: Update `FileEntry`**

Read `src/Rag.NET/DataProviders/FileEntry.cs`. Change `string Id` to `EntryId Id`:

```csharp
using Rag.NET.Models;

namespace Rag.NET.DataProviders;

public sealed record FileEntry(
    EntryId Id,
    string FileName,
    Func<CancellationToken, Task<Stream>> OpenContentAsync,
    string? ETag = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
```

**Step 2: Update `RagPipelineExtensions`**

Read `src/Rag.NET/DataProviders/RagPipelineExtensions.cs`. Key changes:
- `string providerId` parameter → `ProviderId providerId`
- `seenIds` type: `ConcurrentDictionary<string, byte>` → `ConcurrentDictionary<EntryId, byte>`
- `previousIds` type: follow `GetAllIdsAsync` return type change → `IReadOnlySet<EntryId>`
- Where `entry.Id` is used as a string (e.g., in error messages), use `entry.Id.Value`
- Where `providerId` is passed to `hashStore` methods, it's already the right type after Task 2

**Step 3: Fix all DataProvider compile errors**

Run:
```
dotnet build src/Rag.NET/Rag.NET.csproj -v q 2>&1
```

Every DataProvider that creates a `FileEntry` will fail to compile. Search for `new FileEntry(` and wrap the first argument: `"file-id"` → `new EntryId("file-id")`.

There are ~20 DataProvider files. The fix is mechanical for each: `new FileEntry("id", ...)` → `new FileEntry(new EntryId("id"), ...)`.

**Step 4: Build entire solution**

```
dotnet build -v q 2>&1
```

Fix any remaining compile errors.

**Step 5: Run tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q 2>&1
```

Fix test compile errors (wrapping string args). All tests should pass.

**Step 6: Commit**

```bash
git add -A
git commit -m "refactor: migrate FileEntry.Id to EntryId, RagPipelineExtensions to ProviderId"
```

---

## Task 4: Migrate `IConversationMemory` + implementations to `SessionId`

**Files:**
- Modify: `src/Rag.NET.Abstractions/Abstractions/IConversationMemory.cs`
- Modify: `src/Rag.NET.Memory/PersistentConversationMemory.cs`
- Modify: `src/Rag.NET.Memory/ConversationMemoryPipeline.cs`

**Step 1: Update `IConversationMemory`**

Read the file first. Change `string sessionId` → `SessionId sessionId` in all method signatures. Add `using Rag.NET.Models;`.

**Step 2: Update `PersistentConversationMemory`**

Read the file. Key changes:
- `string sessionId` parameter → `SessionId sessionId`
- `ConcurrentDictionary<string, int>` → `ConcurrentDictionary<SessionId, int>`
- `new DocumentId(sessionId)` → `new DocumentId(sessionId.Value)`
- Remove `ArgumentException.ThrowIfNullOrEmpty(sessionId)` — the value object constructor already validates

**Step 3: Update `ConversationMemoryPipeline`**

Read the file. Change `string sessionId` → `SessionId sessionId` in method signatures and internal calls.

**Step 4: Build and fix compile errors**

```
dotnet build -v q 2>&1
```

Fix any remaining references (API endpoints, MCP tools, tests).

**Step 5: Run tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q 2>&1
```

All tests should pass after wrapping string literals.

**Step 6: Commit**

```bash
git add -A
git commit -m "refactor: migrate IConversationMemory to SessionId value object"
```

---

## Task 5: `RagJsonSerializerContext` + InternalsVisibleTo

**Files:**
- Create: `src/Rag.NET.Abstractions/Serialization/RagJsonSerializerContext.cs`
- Create: `tests/Rag.NET.Tests/Serialization/RagJsonSerializerContextTests.cs`
- Modify: `src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj` — add InternalsVisibleTo for vector store packages

**Step 1: Write the roundtrip test**

```csharp
using System.Text.Json;
using Xunit;

namespace Rag.NET.Tests.Serialization;

public class RagJsonSerializerContextTests
{
    [Fact]
    public void DictionaryStringString_Roundtrip_PreservesData()
    {
        var original = new Dictionary<string, string>
        {
            ["key1"] = "value1",
            ["key2"] = "value2",
        };

        var json = JsonSerializer.Serialize(original, RagJsonSerializerContext.Default.DictionaryStringString);
        var deserialized = JsonSerializer.Deserialize(json, RagJsonSerializerContext.Default.DictionaryStringString);

        Assert.NotNull(deserialized);
        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void ListString_Roundtrip_PreservesData()
    {
        var original = new List<string> { "claim 1", "claim 2", "claim 3" };

        var json = JsonSerializer.Serialize(original, RagJsonSerializerContext.Default.ListString);
        var deserialized = JsonSerializer.Deserialize(json, RagJsonSerializerContext.Default.ListString);

        Assert.NotNull(deserialized);
        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void DictionaryStringString_DeserializeNull_ReturnsNull()
    {
        var result = JsonSerializer.Deserialize("null", RagJsonSerializerContext.Default.DictionaryStringString);

        Assert.Null(result);
    }
}
```

**Step 2: Run tests to verify compile failure**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagJsonSerializerContextTests" -v q 2>&1
```

Expected: `RagJsonSerializerContext` not found.

**Step 3: Implement**

`src/Rag.NET.Abstractions/Serialization/RagJsonSerializerContext.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Rag.NET;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class RagJsonSerializerContext : JsonSerializerContext;
```

Note: namespace is `Rag.NET` (not `Rag.NET.Abstractions`) for convenience — all consuming packages already use this namespace.

**Step 4: Add InternalsVisibleTo entries**

In `src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj`, add to the existing `<ItemGroup>` with `AssemblyAttribute`:

```xml
<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
  <_Parameter1>Rag.NET.VectorStores.PgVector</_Parameter1>
</AssemblyAttribute>
<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
  <_Parameter1>Rag.NET.VectorStores.Qdrant</_Parameter1>
</AssemblyAttribute>
<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
  <_Parameter1>Rag.NET.VectorStores.AzureAISearch</_Parameter1>
</AssemblyAttribute>
<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
  <_Parameter1>Rag.NET.Evaluation.Ragas</_Parameter1>
</AssemblyAttribute>
```

**Step 5: Run tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagJsonSerializerContextTests" -v q 2>&1
```

Expected: all 3 tests pass.

**Step 6: Run full suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q 2>&1
```

**Step 7: Commit**

```bash
git add src/Rag.NET.Abstractions/Serialization/RagJsonSerializerContext.cs src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj tests/Rag.NET.Tests/Serialization/RagJsonSerializerContextTests.cs
git commit -m "feat(abstractions): add RagJsonSerializerContext for AOT-safe JSON serialization"
```

---

## Task 6: `MetadataSerializer` with `Result<T, RagError>`

**Files:**
- Create: `src/Rag.NET.Abstractions/Serialization/MetadataSerializer.cs`
- Create: `tests/Rag.NET.Tests/Serialization/MetadataSerializerTests.cs`

**Step 1: Write failing tests**

```csharp
using System.Text.Json;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Serialization;

public class MetadataSerializerTests
{
    [Fact]
    public void DeserializeMetadata_ValidJson_ReturnsDict()
    {
        var json = """{"key1":"value1","key2":"value2"}""";

        var result = MetadataSerializer.DeserializeMetadata(json);

        Assert.True(result.IsSuccess);
        var dict = result.Value;
        Assert.Equal("value1", dict["key1"]);
        Assert.Equal("value2", dict["key2"]);
    }

    [Fact]
    public void DeserializeMetadata_NullInput_ReturnsEmptyDict()
    {
        var result = MetadataSerializer.DeserializeMetadata(null);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void DeserializeMetadata_EmptyString_ReturnsEmptyDict()
    {
        var result = MetadataSerializer.DeserializeMetadata(string.Empty);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void DeserializeMetadata_MalformedJson_ReturnsError()
    {
        var result = MetadataSerializer.DeserializeMetadata("not json at all {{{");

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void SerializeMetadata_ValidDict_ReturnsJson()
    {
        var dict = new Dictionary<string, string>
        {
            ["key1"] = "value1",
        };

        var json = MetadataSerializer.SerializeMetadata(dict);
        var roundtrip = MetadataSerializer.DeserializeMetadata(json);

        Assert.True(roundtrip.IsSuccess);
        Assert.Equal("value1", roundtrip.Value["key1"]);
    }
}
```

**Step 2: Run to verify compile failure**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "MetadataSerializerTests" -v q 2>&1
```

**Step 3: Implement**

`src/Rag.NET.Abstractions/Serialization/MetadataSerializer.cs`:
```csharp
using System.Text.Json;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET;

/// <summary>
/// Centralised metadata serialization using <see cref="RagJsonSerializerContext"/>
/// for AOT-safe, reflection-free JSON handling. Deserialization wraps failures in
/// <see cref="Result{T, E}"/> — callers choose the error policy.
/// </summary>
internal static class MetadataSerializer
{
    public static Result<Dictionary<string, string>, RagError> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize(json,
                RagJsonSerializerContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            return new RagError.StorageFailed(ex);
        }
    }

    public static string SerializeMetadata(Dictionary<string, string> metadata)
        => JsonSerializer.Serialize(metadata, RagJsonSerializerContext.Default.DictionaryStringString);
}
```

**Step 4: Run tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "MetadataSerializerTests" -v q 2>&1
```

Expected: all 5 tests pass. Adjust assertions if `Result` API differs (check `.IsSuccess`/`.IsFailure`/`.Value` — the actual `ZeroAlloc.Results` API may use different property names).

**Step 5: Run full suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q 2>&1
```

**Step 6: Commit**

```bash
git add src/Rag.NET.Abstractions/Serialization/MetadataSerializer.cs tests/Rag.NET.Tests/Serialization/MetadataSerializerTests.cs
git commit -m "feat(abstractions): add MetadataSerializer with Result-based error handling"
```

---

## Task 7: Migrate vector stores to `MetadataSerializer` + `RagJsonSerializerContext`

**Files:**
- Modify: `src/Rag.NET.VectorStores.PgVector/PgVectorStore.cs`
- Modify: `src/Rag.NET.VectorStores.Qdrant/QdrantVectorStore.cs`
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs`

**Step 1: Update `PgVectorStore`**

Read `src/Rag.NET.VectorStores.PgVector/PgVectorStore.cs`. Two changes:

Serialization (line ~83):
```csharp
// Before:
JsonSerializer.Serialize(chunk.Chunk.Metadata)
// After:
MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata)
```

Deserialization (line ~136):
```csharp
// Before:
JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3))
    ?? new Dictionary<string, string>(StringComparer.Ordinal)
// After:
MetadataSerializer.DeserializeMetadata(reader.GetString(3))
    .Match(ok => ok, err => { logger.LogWarning("Metadata deserialization failed for chunk: {Error}", err); return new Dictionary<string, string>(); })
```

Note: If the class doesn't have `ILogger`, add `using Microsoft.Extensions.Logging;` and check if a logger is already injected. If not, use a simpler pattern without logging.

**Step 2: Update `QdrantVectorStore`**

Read `src/Rag.NET.VectorStores.Qdrant/QdrantVectorStore.cs`. Same pattern:

Serialization (line ~57):
```csharp
MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata)
```

Deserialization (line ~98):
```csharp
MetadataSerializer.DeserializeMetadata(metaValue.StringValue)
    .Match(ok => ok, err => new Dictionary<string, string>())
```

**Step 3: Update `AzureAISearchVectorStore`**

Read `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs`. Same pattern at lines ~86 and ~290.

**Step 4: Build**

```
dotnet build -v q 2>&1
```

If any vector store packages don't reference `Rag.NET.Abstractions`, add the project reference.

**Step 5: Run tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q 2>&1
```

All existing vector store tests should still pass — the serialization format hasn't changed, only the code path.

**Step 6: Commit**

```bash
git add src/Rag.NET.VectorStores.PgVector/ src/Rag.NET.VectorStores.Qdrant/ src/Rag.NET.VectorStores.AzureAISearch/
git commit -m "refactor(vectorstores): use MetadataSerializer for AOT-safe resilient deserialization"
```

---

## Task 8: Migrate SQLite stores to `MetadataSerializer`

**Files:**
- Modify: `src/Rag.NET/Storage/SqliteBm25Index.cs`
- Modify: `src/Rag.NET/Storage/SqliteDocumentStore.cs`

**Step 1: Update `SqliteBm25Index`**

Read `src/Rag.NET/Storage/SqliteBm25Index.cs`. Find the deserialization at line ~191:

```csharp
// Before:
var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6))
    ?? new Dictionary<string, string>(StringComparer.Ordinal);
// After:
var metadata = MetadataSerializer.DeserializeMetadata(reader.GetString(6))
    .Match(ok => ok, err => new Dictionary<string, string>(StringComparer.Ordinal));
```

Also find any serialization calls and replace with `MetadataSerializer.SerializeMetadata(...)`.

**Step 2: Update `SqliteDocumentStore`**

Read `src/Rag.NET/Storage/SqliteDocumentStore.cs`. Find deserialization at lines ~112 and ~148:

Apply the same pattern for both `tags` and `metadata` dictionaries.

**Step 3: Build + test**

```
dotnet build src/Rag.NET/Rag.NET.csproj -v q 2>&1
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q 2>&1
```

**Step 4: Commit**

```bash
git add src/Rag.NET/Storage/SqliteBm25Index.cs src/Rag.NET/Storage/SqliteDocumentStore.cs
git commit -m "refactor(storage): use MetadataSerializer in SQLite stores"
```

---

## Task 9: Migrate RAGAS evaluators to `RagJsonSerializerContext`

**Files:**
- Modify: `src/Rag.NET.Evaluation.Ragas/FaithfulnessEvaluator.cs`
- Modify: `src/Rag.NET.Evaluation.Ragas/ContextRecallEvaluator.cs`

**Step 1: Update `FaithfulnessEvaluator`**

Read `src/Rag.NET.Evaluation.Ragas/FaithfulnessEvaluator.cs`. Find the `JsonSerializer.Deserialize<List<string>>(raw)` call (line ~47):

```csharp
// Before:
return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
// After:
return JsonSerializer.Deserialize(raw, RagJsonSerializerContext.Default.ListString) ?? [];
```

Also update the `using` to remove `System.Text.Json` if no other usages remain, or keep it if `JsonException` is still caught.

**Step 2: Update `ContextRecallEvaluator`**

Read `src/Rag.NET.Evaluation.Ragas/ContextRecallEvaluator.cs`. Same change at line ~50:

```csharp
// Before:
return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
// After:
return JsonSerializer.Deserialize(raw, RagJsonSerializerContext.Default.ListString) ?? [];
```

**Step 3: Verify `Rag.NET.Evaluation.Ragas` can access `RagJsonSerializerContext`**

The context is `internal` in `Rag.NET.Abstractions`. Check that `Rag.NET.Evaluation.Ragas.csproj` references `Rag.NET.Evaluation.csproj` which references `Rag.NET.Abstractions.csproj` — but `InternalsVisibleTo` is NOT transitive. So `Rag.NET.Evaluation.Ragas` needs either:
- A direct `ProjectReference` to `Rag.NET.Abstractions`, or
- The InternalsVisibleTo we added in Task 5 (already done: `Rag.NET.Evaluation.Ragas` was added)

Verify by building:
```
dotnet build src/Rag.NET.Evaluation.Ragas/Rag.NET.Evaluation.Ragas.csproj -v q 2>&1
```

If access fails, add a direct project reference to `Rag.NET.Abstractions`.

**Step 4: Run RAGAS tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FaithfulnessEvaluatorTests|ContextRecallEvaluatorTests" -v q 2>&1
```

Expected: all tests pass (JSON robustness tests confirm the catch path still works).

**Step 5: Run full suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q 2>&1
```

**Step 6: Commit**

```bash
git add src/Rag.NET.Evaluation.Ragas/FaithfulnessEvaluator.cs src/Rag.NET.Evaluation.Ragas/ContextRecallEvaluator.cs
git commit -m "refactor(evaluation): use RagJsonSerializerContext in RAGAS evaluators"
```

---

## Task 10: Update features backlog

**Files:**
- Modify: `docs/reference/features.md`

**Step 1: Add ZeroAlloc expansion entry or update existing entries**

Find appropriate sections and note the expanded package usage. If there isn't a specific feature entry, this is a cross-cutting refactor — no backlog change needed.

Check whether any existing pending features are now partially addressed by this work.

**Step 2: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: update features backlog for ZeroAlloc package expansion"
```
