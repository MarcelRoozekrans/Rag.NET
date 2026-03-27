# Tag-Based Retrieval + Multi-Language Code Splitting — Design

**Date:** 2026-03-27
**Status:** Approved

---

## Goal

Two independent features:

1. **Tag-Based Retrieval** — `IRetriever` decorator that automatically narrows retrieval using semantic tag matching. Tags from `DocumentMetadata.Tags` are embedded at ingest time; at query time the query embedding is compared against all known tag vectors and the best matches are injected as `MetadataFilter`.

2. **Multi-Language Code Splitting** — `IChunkingStrategy` that uses per-language separator hierarchies (Python, JS/TS, Java, Go, Rust, Ruby, C#, C++, PHP, Swift) to split code at class/function/method boundaries. Language detected from file extension, overridable via options.

---

## Tag-Based Retrieval

### Overview

`TagRetriever` wraps any `IRetriever`. On each `RetrieveAsync` call it embeds the query, scans `ITagIndex` for similar tag values, and merges the top matches into `MetadataFilter` before delegating to the inner retriever. The tag index is populated automatically during ingestion by `TagIngestionBehavior`.

### Models

```csharp
public sealed class TagRetrievalOptions
{
    public int TopK { get; init; } = 1;         // max matched tags to inject per key
    public double MinScore { get; init; } = 0.82;
}

public interface ITagIndex
{
    void Add(string key, string value, ReadOnlyMemory<float> embedding);
    IReadOnlyList<(string Key, string Value, double Score)> Search(
        ReadOnlyMemory<float> queryEmbedding, int topK, double minScore);
}
```

`InMemoryTagIndex` — thread-safe via `ReaderWriterLockSlim`, deduplicates by `(key, value)`. Tag vocabulary is typically small (tens to hundreds of values) so in-memory cosine scan is negligible.

### Ingestion flow

`TagIngestionBehavior` runs after `MetadataBehavior`. For each ingested document it:
1. Iterates `DocumentMetadata.Tags`
2. Skips any `(key, value)` already in the index (deduplication)
3. Embeds new values via the registered `IEmbeddingGenerator`
4. Calls `ITagIndex.Add(key, value, embedding)`

Embedding failures are logged and skipped — non-fatal.

### Retrieval flow

```
TagRetriever.RetrieveAsync(query, options):
  if !options.UseTagRetrieval: return inner.RetrieveAsync(query, options)
  embedding = embedder.GenerateAsync(query)          // failure → passthrough
  matches = tagIndex.Search(embedding, TopK, MinScore)
  // at most one match per key — highest score wins
  // caller's existing MetadataFilter entries preserved
  mergedOptions = options with { MetadataFilter = merge(options.MetadataFilter, matches) }
  return inner.RetrieveAsync(query, mergedOptions)
```

### DI Registration

```csharp
services.AddRagNet(rag => rag
    .UseTagRetrieval());                                     // defaults

services.AddRagNet(rag => rag
    .UseTagRetrieval(new TagRetrievalOptions
    {
        TopK     = 2,
        MinScore = 0.85,
    }));
```

`UseTagRetrieval` registers `TagRetrievalOptions` as a sentinel. `WireTagRetrieval` (called from `AddRagNet` after the builder delegate) registers `InMemoryTagIndex` as `ITagIndex`, registers `TagRetriever` as concrete type, and replaces `IRetriever` with it.

Per-call opt-out: `new RetrievalOptions { UseTagRetrieval = false }`.

### Decorator stacking with `UseDeepResearch`

When both are registered, the desired order is `TagRetriever → DeepResearchRetriever → PipelineRetriever` (tag narrowing first, then iterative deep research within the narrowed space).

To enable this, `WireDeepResearch` is updated to register `DeepResearchRetriever` as its own concrete type (in addition to replacing `IRetriever`). `WireTagRetrieval` then resolves `DeepResearchRetriever` if present, otherwise `PipelineRetriever`, as its inner.

Call order in `AddRagNet`:
1. `WireRefinementStrategy`
2. `WireDeepResearch`
3. `WireTagRetrieval`

### Error handling

| Condition | Behaviour |
|---|---|
| Tag embedding fails at ingest | Logged as warning, tag skipped — non-fatal |
| Tag index empty at query time | Skipped silently, original options passed through |
| Query embedding fails in `TagRetriever` | Logged as warning, original options passed through |
| `UseTagRetrieval = false` | Decorator skipped entirely |

---

## Multi-Language Code Splitting

### Overview

`CodeChunkingStrategy` implements `IChunkingStrategy`. It uses the same recursive descent algorithm as `RecursiveChunkingStrategy` but parameterises the separator list on the detected (or configured) language. Unknown extensions fall back to a generic code separator set.

### Options

```csharp
public sealed class CodeChunkingOptions
{
    /// <summary>
    /// Explicit language name. When null, language is auto-detected from the file extension
    /// in <c>DocumentSection.DocumentId.Value</c>.
    /// Recognised values: python, javascript, typescript, java, go, rust, ruby, csharp, cpp, php, swift.
    /// Throws <see cref="ArgumentException"/> at registration if set to an unrecognised value.
    /// </summary>
    public string? Language { get; init; }
}
```

### Language detection

`Path.GetExtension(section.DocumentId.Value)` mapped to a canonical language name:

| Extension(s) | Language |
|---|---|
| `.py` | `python` |
| `.js`, `.mjs`, `.cjs` | `javascript` |
| `.ts`, `.tsx` | `typescript` |
| `.java` | `java` |
| `.go` | `go` |
| `.rs` | `rust` |
| `.rb` | `ruby` |
| `.cs` | `csharp` |
| `.cpp`, `.cc`, `.cxx`, `.h`, `.hpp` | `cpp` |
| `.php` | `php` |
| `.swift` | `swift` |
| unknown | generic fallback |

When `CodeChunkingOptions.Language` is set, it overrides extension detection for all documents.

### Separator hierarchies

Each language tries to split at the highest semantic boundary first:

```
python:     \nclass  →  \ndef  →  \n\tdef  →  \n\n  →  \n  →  (space)
javascript: \nfunction  →  \nclass  →  \nconst  →  \nlet  →  \n\n  →  \n  →  (space)
typescript: \nfunction  →  \nclass  →  \ninterface  →  \ntype  →  \nconst  →  \n\n  →  \n  →  (space)
java:       \npublic class  →  \nprivate  →  \nprotected  →  \npublic  →  \nvoid  →  \n\n  →  \n  →  (space)
go:         \nfunc  →  \ntype  →  \nvar  →  \nconst  →  \n\n  →  \n  →  (space)
rust:       \nfn  →  \nimpl  →  \nstruct  →  \nenum  →  \ntrait  →  \n\n  →  \n  →  (space)
ruby:       \ndef  →  \nclass  →  \nmodule  →  \n\n  →  \n  →  (space)
csharp:     \npublic class  →  \nprivate  →  \nprotected  →  \npublic  →  \nnamespace  →  \n\n  →  \n  →  (space)
cpp:        \nvoid  →  \nclass  →  \nstruct  →  \nnamespace  →  \n\n  →  \n  →  (space)
php:        \nfunction  →  \nclass  →  \n\n  →  \n  →  (space)
swift:      \nfunc  →  \nclass  →  \nstruct  →  \nextension  →  \n\n  →  \n  →  (space)
generic:    \n\n  →  \n  →  (space)
```

`ChunkingOptions.MaxChunkSize` and `Overlap` still apply. Overlap is typically set to 0 for code (noted in docs).

### DI Registration

```csharp
services.AddRagNet(rag => rag
    .UseCodeChunking());                                      // auto-detect from extension

services.AddRagNet(rag => rag
    .UseCodeChunking(new CodeChunkingOptions
    {
        Language = "python",                                  // explicit override for all docs
    }));
```

### Error handling

| Condition | Behaviour |
|---|---|
| Unknown file extension | Falls back to generic code separators |
| `Language` set to unrecognised value | `ArgumentException` thrown at `UseCodeChunking()` call |

---

## Files

**New:**
- `src/Rag.NET/Abstractions/ITagIndex.cs`
- `src/Rag.NET/Search/InMemoryTagIndex.cs`
- `src/Rag.NET/Ingestion/Behaviors/TagIngestionBehavior.cs`
- `src/Rag.NET/Retrieval/TagRetriever.cs`
- `src/Rag.NET/Models/Options/TagRetrievalOptions.cs`
- `src/Rag.NET/Chunking/CodeChunkingStrategy.cs`
- `src/Rag.NET/Models/Options/CodeChunkingOptions.cs`

**Modified:**
- `src/Rag.NET/DependencyInjection/RagBuilder.cs` — add `UseTagRetrieval()`, `UseCodeChunking()`
- `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` — add `WireTagRetrieval()`, fix `WireDeepResearch()` to register `DeepResearchRetriever` as concrete type
- `src/Rag.NET/Models/Options/RetrievalOptions.cs` — add `UseTagRetrieval = true`

**New tests:**
- `tests/Rag.NET.Tests/Search/InMemoryTagIndexTests.cs`
- `tests/Rag.NET.Tests/Ingestion/TagIngestionBehaviorTests.cs`
- `tests/Rag.NET.Tests/Retrieval/TagRetrieverTests.cs`
- `tests/Rag.NET.Tests/Chunking/CodeChunkingStrategyTests.cs`
- `tests/Rag.NET.Tests/DependencyInjection/UseTagRetrievalTests.cs`
- `tests/Rag.NET.Tests/DependencyInjection/UseCodeChunkingTests.cs`

---

## Testing Plan

### `InMemoryTagIndex`
1. `Search` returns matches above `MinScore`, ordered by score descending
2. Duplicate `(key, value)` — second `Add` is ignored
3. At most one result per key — highest score per key returned
4. Concurrent `Add` + `Search` — no data races

### `TagIngestionBehavior`
5. Tags from `DocumentMetadata.Tags` embedded and added to index
6. Same tag value on second document — `Add` not called again (dedup)
7. Embedding failure — logged, tag skipped, behavior completes

### `TagRetriever`
8. Matches found — injected into `MetadataFilter`, caller's existing entries preserved
9. No matches above threshold — options passed through unchanged
10. Query embedding failure — logged, original options passed through
11. `UseTagRetrieval = false` — inner retriever called with original options, no embedding call

### `CodeChunkingStrategy`
12. Python file — splits at `\ndef ` boundary before falling back to `\n`
13. TypeScript file — splits at `\nfunction ` boundary
14. Go file — splits at `\nfunc ` boundary
15. Unknown extension (`.xyz`) — falls back to generic separators
16. `Language = "python"` override — used regardless of file extension
17. `Language = "invalid"` — `ArgumentException` at registration

### DI
18. `UseTagRetrieval` → `IRetriever` is `TagRetriever`, `ITagIndex` resolves
19. `UseCodeChunking` → `IChunkingStrategy` is `CodeChunkingStrategy`
20. `UseTagRetrieval` + `UseDeepResearch` → `TagRetriever` wraps `DeepResearchRetriever`
