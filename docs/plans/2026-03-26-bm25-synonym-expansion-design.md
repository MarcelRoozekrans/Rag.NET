# Design: BM25 Synonym Expansion

**Date:** 2026-03-26
**Status:** Approved

---

## Overview

Augment the existing `InMemoryBm25Index` (and by extension `SqliteBm25Index`) with runtime-updatable domain-specific synonym groups. Synonyms are bidirectional: any term in a group expands to all other terms in that group at both index time and query time. No boost weights — synonyms are treated as equal-weight tokens, matching standard BM25 practice and avoiding significant implementation complexity for marginal gain.

---

## Architecture

### `SynonymMap`

New class at `src/Rag.NET/Search/SynonymMap.cs`:

```csharp
public sealed class SynonymMap
{
    // flat lookup: term (lowercase) → all other terms in its group (excluding self)
    private readonly Dictionary<string, ImmutableHashSet<string>> _lookup;
    private readonly ReaderWriterLockSlim _lock;

    public SynonymMap() { }
    public SynonymMap(IEnumerable<IReadOnlyCollection<string>> groups) { }

    public void AddGroup(params string[] terms) { }
    public void RemoveGroup(params string[] terms) { }
    public IReadOnlySet<string> Expand(string term) { } // empty set if no synonyms
}
```

**Bidirectionality:** `AddGroup("k8s", "kubernetes")` writes two entries:
- `"k8s"` → `{"kubernetes"}`
- `"kubernetes"` → `{"k8s"}`

Terms are normalised to lowercase on write and lookup, consistent with BM25 tokenisation.

**Thread safety:** `ReaderWriterLockSlim` — concurrent reads, exclusive writes. `AddGroup` / `RemoveGroup` are the only mutation points.

**`RemoveGroup` semantics:** Caller passes any subset of the original group terms; all matching entries are removed from the lookup. Partial removal is allowed (e.g. remove one abbreviation from a three-way group).

### `InMemoryBm25Index` modification

Constructor gains an optional parameter:

```csharp
public InMemoryBm25Index(SynonymMap? synonymMap = null)
```

`Tokenize` is updated to expand synonyms inline:

```csharp
internal static IEnumerable<string> Tokenize(string text, SynonymMap? synonymMap)
{
    foreach (var token in BaseTokenize(text))
    {
        yield return token;
        if (synonymMap is not null)
            foreach (var syn in synonymMap.Expand(token))
                yield return syn;
    }
}
```

Both `Add` (index time) and `Search` (query time) pass the same `SynonymMap` instance to `Tokenize`. This ensures symmetric expansion: a document indexed with "kubernetes" is found by a query for "k8s", and vice versa.

`SqliteBm25Index` wraps `InMemoryBm25Index` internally and delegates all operations to it — no changes required there.

### DI Registration

Added to `RagBuilder`:

```csharp
builder.UseBm25Synonyms(new SynonymMap([
    ["k8s", "kubernetes"],
    ["MI", "myocardial infarction"],
    ["JS", "javascript"],
]));
```

`UseBm25Synonyms` registers the `SynonymMap` as a singleton and replaces the `InMemoryBm25Index` registration to inject it. If `UseSqlitePersistence` is also called, the same `SynonymMap` flows into `SqliteBm25Index` via the wrapped `InMemoryBm25Index`.

Runtime updates require no DI interaction — callers hold a reference to the `SynonymMap` singleton and call `AddGroup` / `RemoveGroup` directly.

---

## File Layout

```
src/Rag.NET/
  Search/SynonymMap.cs                                      (new)
  Search/InMemoryBm25Index.cs                               (modified)
  DependencyInjection/RagBuilder.cs                         (modified)
tests/Rag.NET.Tests/
  Search/SynonymMapTests.cs                                 (new)
  Search/InMemoryBm25IndexSynonymTests.cs                   (new)
```

---

## Error Handling

- `AddGroup` with fewer than 2 terms → `ArgumentException` (a one-term group is meaningless).
- `AddGroup` with a term that already belongs to another group → terms are merged into a single group.
- `RemoveGroup` with unknown terms → no-op, no exception.
- Empty or whitespace terms → `ArgumentException`.

---

## Testing

| Scenario | Expected |
|----------|----------|
| Index "kubernetes", query "k8s" | Hit returned |
| Index "k8s", query "kubernetes" | Hit returned |
| Index "kubernetes", query "kubernetes" | Hit returned (unchanged) |
| Three-way group: index A, query B or C | All hits returned |
| No synonym map, existing query | Behaviour unchanged |
| AddGroup at runtime, new query | New synonyms applied immediately |
| RemoveGroup, repeat query | Synonyms no longer applied |
| AddGroup overlapping existing group | Groups merged correctly |
| SynonymMap with SqliteBm25Index | Same expansion applied |

---

## Out of Scope

- Per-synonym boost weights — adds significant complexity for marginal quality gain; standard BM25 treats all tokens equally
- Stemming / lemmatisation — separate concern, separate feature
- Synonym persistence to SQLite — caller manages the `SynonymMap` lifecycle; DI singleton survives restarts if caller reconstructs it from config
- LLM-assisted synonym generation
