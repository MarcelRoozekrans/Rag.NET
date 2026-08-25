# The BM25 arm ignores `MetadataFilter` — design

**Issue:** [#350](https://github.com/MarcelRoozekrans/Rag.NET/issues/350). **Phase:** 6.2.5, the
first of three contract defects. **Status:** design approved 2026-08-25. #328 and #360 are handled
separately as bounded work — they share a shape with this defect but no design.

## The defect

`RetrievalOptions.MetadataFilter` documents itself as:

> Restricts retrieval to chunks whose metadata matches every key/value pair exactly (typed
> equality … with ordinal comparison for strings and AND semantics across pairs).

On the client-side hybrid path it does not. `EnsembleBehavior` builds one `SearchOptions` carrying
the filter (`EnsembleBehavior.cs:47`) and hands it to the dense arm and the sparse arm. The BM25 arm
gets this instead (`EnsembleBehavior.cs:110`):

```csharp
bm25Hits = Bm25Index.Search(ctx.Query, topK: searchOptions.TopK);
```

**`IBm25Index.Search(string query, int topK)` has no filter parameter, so there is nothing to
pass.** Those hits are merged by `RrfMerger.MergeMany` alongside the filtered arms, and nothing
downstream re-applies the filter — the only other readers of `MetadataFilter` are the two behaviours
that *build* `SearchOptions` and the cache-key generator. **A filtered query can return chunks the
filter excludes.**

### It is wider than "stores without native hybrid"

The initial report said this affects stores lacking native hybrid search. `CanDispatchNatively` also
falls back to the client-side path when the caller supplies `EnsembleOptions`, when `MinScore` is
non-zero, or when a sparse arm would run:

```csharp
private bool CanDispatchNatively(RetrievalOptions opts) =>
    opts.EnsembleOptions is null && opts.MinScore is 0.0 && !SparseArmWouldRun(opts);
```

So **Azure AI Search with `MinScore = 0.2` and a metadata filter leaks too**, even though its own
native hybrid query filters server-side correctly. The conditions under which a per-query filter
silently stops being a boundary are not something a caller can be expected to know.

### Why this is worse than a relevance bug

`TextChunk.Metadata` documents itself as feeding *"`RetrievalOptions.MetadataFilter` matching,
RBAC/trust retrieval guards, and time-weighted re-scoring"*, and `TagRetriever` injects tags **as**
`MetadataFilter` entries — so tag-scoping leaks by the same path.

No specific RBAC bypass is claimed here; those guards may be applied elsewhere, and asserting one
without tracing it would be exactly the kind of unverified claim this project has been burned by.
The narrower point is sufficient: **a filter that silently does not apply is not a filter.**

## Approach

Three were considered. **Filtering inside the index was chosen.**

| Approach | Verdict |
|---|---|
| **Filter inside the index** — add the filter to `IBm25Index.Search` | **Chosen.** BM25 ranks only eligible chunks, so `topK` returns the best *eligible* hits. Breaking for external implementers. |
| Filter after the index returns — over-fetch in `EnsembleBehavior`, then drop | Rejected. Non-breaking, and it matches the `MmrBehavior` / 6.2.4 precedent — but it is the **under-fill** shape 6.2.4 just fixed: ask for 6, drop 3, return 3. Over-fetch lowers the odds; it cannot eliminate them. |
| Default interface method carrying the filter | Rejected. Source-compatible, but the default would have to fall back to unfiltered-then-drop, leaving the leak live for anyone who does not override — a fix that does not fix. |

**The breaking change is affordable now and not later.** v1.0 has not shipped; `IBm25Index` has
**two** in-repo implementations and exactly **one** call site; and 6.2.6 is already making a
breaking package change pre-tag for the same reason.

## Design

### 1. The signature

```csharp
IReadOnlyList<(TextChunk chunk, double score)> Search(
    string query, int topK, IDictionary<string, MetadataValue>? metadataFilter = null);
```

**`IDictionary`, not `IReadOnlyDictionary`**, despite the parameter being read-only in use.
`SearchOptions.MetadataFilter` is declared `IDictionary<string, MetadataValue>?` and
`IDictionary<K,V>` does **not** derive from `IReadOnlyDictionary<K,V>` — so the read-only form would
force a cast or a defensive copy at the one call site, on every hybrid query, to no benefit. It also
matches the existing `MatchesFilter(TextChunk, IDictionary<string, MetadataValue>?)`. Changing the
whole codebase to the read-only form is a defensible cleanup and is **not** this issue's business.

**Deliberately not `Search(string query, SearchOptions options)`**, although that would mirror
`IVectorStore.SearchAsync` and absorb future fields without breaking twice. `SearchOptions` carries
`MinScore`, which is meaningless on BM25's score scale, plus fields the index cannot honour. Taking
it would mean documenting which fields are ignored — and *an option that does not do what it says*
is the exact defect class this phase exists to close. **A parameter list that carries only what it
honours cannot lie.**

The default value keeps the single call site and any external *callers* source-compatible.
Implementers still break; that is the accepted cost, and it is the point of choosing this approach
over the other two.

### 2. One matcher, because divergence is the real risk

Today `MatchesFilter` is a **private static inside `InMemoryVectorStore`** (`:81`, `:232`). There is
no shared matcher. If the BM25 indexes reimplement typed equality, the dense and BM25 arms can
disagree about what matches — and a filtered query would then return different sets depending on
which arm found the chunk. That is a **new defect of the same family as the one being fixed**.

**Where it goes:** a public static in `Rag.NET.Abstractions`, beside the types it matches on —
`MetadataFilterMatcher.Matches(TextChunk chunk, IDictionary<string, MetadataValue>? filter)`.

`Rag.NET.Abstractions` because the callers span packages: `InMemoryVectorStore` and
`InMemoryBm25Index` live in `Rag.NET`, `SqliteBm25Index` lives in `Rag.NET.Storage.Sqlite`, and both
reference Abstractions. An internal helper with `InternalsVisibleTo` would work across those two but
is fragile and does nothing for the third audience.

**Public, not internal, and that is the substantive part.** This change requires every external
`IBm25Index` implementer to filter. Shipping the interface obligation without shipping the canonical
semantics would leave each of them to reimplement typed equality by guesswork — which is the
divergence risk above, exported to people who cannot see the dense arm's implementation to copy it.

`InMemoryVectorStore.MatchesFilter` becomes a call to it and stops being a private duplicate. This is
a targeted improvement to code the change already touches, not unrelated refactoring.

### 3. `SqliteBm25Index` forwards the filter — there is no SQL path to push into

**Corrected 2026-08-25, after reading the implementation.** An earlier draft of this section weighed
filtering in SQL over `metadata_json` against deserialising and using the shared matcher, and chose
the matcher on the strength of #299/#304 — where SQLite's `COLLATE NOCASE` folded ASCII while the
callers folded Unicode, so an index's comparison semantics and the predicate's disagreed.

**That trade-off does not exist here.** `SqliteBm25Index` is a *write-through wrapper* around
`InMemoryBm25Index`:

```csharp
private readonly InMemoryBm25Index _memory;
...
public IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    EnsureInitialised();
    return _memory.Search(query, topK);
}
```

SQLite is persistence and rehydration only; **every search already runs in memory**. So this
implementation gains one parameter and forwards it. The `metadata_json` column is how chunks are
restored on load, not something `Search` ever queries.

The #299/#304 reasoning still stands as the rule for the day someone *does* add a SQL search path —
it is recorded here for that reader rather than deleted, because the trap is real even though this
change does not walk near it.

### 4. `InMemoryBm25Index` filters after score accumulation, before the sort and truncation

`_docs[docId] = (chunk, length)` already holds the whole `TextChunk`, so the filter is applied
while the per-candidate score is added to the result list — after BM25 has scored the candidate,
but before the results are sorted and truncated to `topK`. That is what makes `topK` come back
full of eligible hits, and it is the entire advantage of this approach over post-filtering.

## Testing

- **A test that fails against today's code:** a filtered hybrid query that returns a chunk the
  filter excludes. Without this the change is unverified — and this milestone's recurring failure is
  a green suite over a real defect (#332, #333, and a #332 regression test that turned out to pass
  against the unfixed code).
- **One test per trigger of the client-side path** — `MinScore != 0`, `EnsembleOptions` supplied,
  sparse arm running — because the leak is wider than the original report and each trigger is a
  separate way in.
- **A parity test:** the dense arm and the BM25 arm agree on which chunks match a given filter. This
  guards §2's divergence risk, and it is the test most likely to still be earning its keep in a year.
- Both `IBm25Index` implementations get the filter tests, not only the in-memory one.

## Out of scope

- **Native hybrid paths.** They filter server-side, and correctly.
- **`MetadataFilter` semantics.** Typed equality, ordinal strings, AND across pairs — unchanged.
- **#342's isolation.** That is 6.2.7, which depends on this: how much of an isolation story a
  caller-supplied filter can honestly carry is exactly what this defect decides.
- **#328 and #360.** Same phase, no shared design; bounded work with their own approval.
