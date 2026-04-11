# ZeroAlloc Package Expansion — Design

## Goal

Expand usage of ZeroAlloc-Net packages across Rag.NET to improve type safety, performance, and AOT readiness. This is Group 1 (storage layer) — non-breaking, highest bang-for-buck.

## Scope

Three integration efforts, all targeting the storage/model layer:

1. **ValueObjects** — add `ProviderId`, `EntryId`, `SessionId`
2. **Serialisation** — add `RagJsonSerializerContext` (source-generated JSON)
3. **Results** — wrap vector store metadata deserialization in `Result<T, RagError>`

---

## Key Decisions

| Question | Decision |
|---|---|
| `EntryId` standalone or compound with `ProviderId`? | Standalone — `ProviderId` is used independently in 43+ files; compound key is just `(ProviderId, EntryId)` tuple |
| Where does `RagJsonSerializerContext` live? | `Rag.NET.Abstractions` — closest to model types, shared by all vector stores |
| Scope of serializer context? | `Dictionary<string, string>` + `List<string>` — covers vector store metadata and RAGAS evaluator JSON parsing |
| How to handle metadata deserialization failure? | `Result<T, RagError>` internally, default policy: log warning + return empty metadata (public interface unchanged) |
| Breaking changes? | Source-breaking for ValueObjects (callers must wrap strings). Acceptable — not public yet |

---

## 1. ValueObjects — `ProviderId`, `EntryId`, `SessionId`

### New types in `Rag.NET.Abstractions`

```csharp
[ValueObject]
public readonly partial record struct ProviderId(string Value);

[ValueObject]
public readonly partial record struct EntryId(string Value);

[ValueObject]
public readonly partial record struct SessionId(string Value);
```

Follow the existing `DocumentId` pattern (already uses `ZeroAlloc.ValueObjects`).

### Propagation

| Value Object | Interfaces affected | Implementation files |
|---|---|---|
| `ProviderId` | `IContentHashStore` (5 methods), `IDataProvider.ProviderId` | `SqliteContentHashStore`, `RagPipelineExtensions`, all DataProvider implementations |
| `EntryId` | `IContentHashStore` (5 methods), `FileEntry.Id` | `SqliteContentHashStore`, `RagPipelineExtensions` |
| `SessionId` | `IConversationMemory` (4 methods) | `ConversationMemoryPipeline`, `PersistentConversationMemory` |

All changes are mechanical: wrap `new XxxId("value")` at creation, use `.Value` when a raw string is needed (SQL parameters, HTTP APIs).

---

## 2. Serialisation — `RagJsonSerializerContext`

### New type in `Rag.NET.Abstractions`

```csharp
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class RagJsonSerializerContext : JsonSerializerContext;
```

Marked `internal` with `[InternalsVisibleTo]` for vector store and RAGAS evaluator packages.

### Callsites

| Package | Method | Type serialized |
|---|---|---|
| `Rag.NET.VectorStores.PgVector` | `StoreAsync`, `SearchAsync` | `Dictionary<string, string>` |
| `Rag.NET.VectorStores.Qdrant` | `StoreAsync`, `SearchAsync` | `Dictionary<string, string>` |
| `Rag.NET.VectorStores.AzureAISearch` | `StoreAsync`, `SearchAsync` | `Dictionary<string, string>` |
| `Rag.NET.Evaluation.Ragas` | `FaithfulnessEvaluator`, `ContextRecallEvaluator` | `List<string>` |
| `SqliteBm25Index` | `SearchAsync` | `Dictionary<string, string>` |
| `SqliteDocumentStore` | `GetChunksAsync` | `Dictionary<string, string>` |

Each callsite changes from:
```csharp
JsonSerializer.Serialize(metadata)
```
to:
```csharp
JsonSerializer.Serialize(metadata, RagJsonSerializerContext.Default.DictionaryStringString)
```

### Benefits

- No runtime reflection for JSON serialization
- AOT-compatible (trimming-safe)
- Perf improvement on vector store hot paths

---

## 3. Results — Resilient Metadata Deserialization

### Shared helper in `Rag.NET.Abstractions`

```csharp
internal static class MetadataSerializer
{
    public static Result<Dictionary<string, string>, RagError> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize(json,
                RagJsonSerializerContext.Default.DictionaryStringString) ?? [];
        }
        catch (JsonException ex)
        {
            return RagError.From(ex, "Failed to deserialize chunk metadata");
        }
    }
}
```

### Vector store usage pattern

```csharp
var metadata = MetadataSerializer.DeserializeMetadata(raw)
    .Match(ok => ok, err => { _logger.LogWarning(...); return []; });
```

Public interface unchanged — `SearchAsync` / `StoreAsync` signatures stay the same.

### Files affected

- `PgVectorStore.cs` — 2 deserialization sites
- `QdrantVectorStore.cs` — 1 deserialization site
- `AzureAISearchVectorStore.cs` — 1 deserialization site
- `SqliteBm25Index.cs` — 1 deserialization site
- `SqliteDocumentStore.cs` — 1 deserialization site

---

## Testing

- **ValueObjects:** Existing tests compile-fail → fix by wrapping strings → proves all callsites migrated
- **Serialisation:** Existing vector store and RAGAS evaluator tests exercise the serialization paths; add a unit test for `RagJsonSerializerContext` roundtrip
- **Results:** Add tests for `MetadataSerializer.DeserializeMetadata` — valid JSON, empty string, malformed JSON; verify vector store tests still pass with the new code path

---

## File Map

```
src/
  Rag.NET.Abstractions/
    Models/ProviderId.cs              <- new
    Models/EntryId.cs                 <- new
    Models/SessionId.cs               <- new
    Serialization/RagJsonSerializerContext.cs  <- new
    Serialization/MetadataSerializer.cs       <- new
    Abstractions/IContentHashStore.cs <- modified (string -> ProviderId/EntryId)
    Abstractions/IConversationMemory.cs <- modified (string -> SessionId)
    Models/FileEntry.cs               <- modified (string Id -> EntryId Id)
  Rag.NET/
    Storage/SqliteContentHashStore.cs <- modified
    DataProviders/RagPipelineExtensions.cs <- modified
  Rag.NET.Memory/
    ConversationMemoryPipeline.cs     <- modified
    PersistentConversationMemory.cs   <- modified
  Rag.NET.VectorStores.PgVector/     <- modified (serialization + results)
  Rag.NET.VectorStores.Qdrant/       <- modified (serialization + results)
  Rag.NET.VectorStores.AzureAISearch/ <- modified (serialization + results)
  Rag.NET.Evaluation.Ragas/
    FaithfulnessEvaluator.cs          <- modified (serialization)
    ContextRecallEvaluator.cs         <- modified (serialization)

tests/
  Rag.NET.Tests/
    Serialization/MetadataSerializerTests.cs <- new
    Serialization/RagJsonSerializerContextTests.cs <- new
```
