# Design: Redis stores, returns and filters on metadata — the contract it already advertises

**Issue:** #513 · **Phase:** 6.2.31 · **Date:** 2026-09-09

## 0. What is actually broken

`RedisVectorStore.StoreAsync` writes four hash fields — `document_id`, `chunk_index`, `text`,
`embedding` — and no metadata. Two consequences follow, and **the issue as filed names only the
smaller one.**

**Neither read path can return metadata.** `MapChunk` and `SearchAsync`'s projection both build a
`TextChunk` with no `Metadata`, so a caller gets an empty dictionary from either. That is
indistinguishable from a document that genuinely carried none, and nothing fails. 6.2.26 found this
while building the keyed lookup and **asserted the limitation rather than skipping the test**, so
that test goes red the day `StoreAsync` starts storing metadata. That day is this phase: **its
failure is the entry point, not a regression.**

**`SearchAsync` never reads `options.MetadataFilter`.** The query is
`*=>[KNN {k} @embedding $vec AS vector_score]` — `RedisVectorStore.cs:235` — with no filter clause
anywhere. Redis is the only one of the seven vector-store packages that does not reference
`MetadataFilter` at all; the other six do.

**Nothing re-checks the filter downstream.** `VectorStoreBehavior` copies `MetadataFilter` into
`SearchOptions`, hands it to the store, and is terminal (`VectorStoreBehavior.cs:24-30`). So **a
filtered search against Redis silently returns unfiltered results.** `TagRetriever` injects filter
entries and depends entirely on the store honouring them, and `TextChunk.Metadata`'s own remarks
name "RBAC/trust retrieval guards" as a use of this filter. This design stops short of calling it an
access-control bypass — `TagRetriever` is a relevance mechanism, not authorization — but the library
documents the pattern and this backend does not implement it.

**Two claims in the codebase are false today.** `TextChunk.Metadata`'s remarks say *"every store
persists and filters on that type"* (`TextChunk.cs:61-62`). `SearchOptions.MetadataFilter` says it
*"restricts results to chunks whose metadata matches"*. On Redis, neither holds.

**And no test can see any of it.** `MetadataFilterParityTests` compares the in-memory dense arm
against the in-memory BM25 arm. **No test asserts that any remote store honours `MetadataFilter`.**

This is the defect shape this milestone keeps finding: code that succeeds while doing nothing. The
comparable score defect (#56) was caught because an inverted ranking is visible; an ignored filter
returns plausible chunks in a plausible order.

## 1. Why this backend is not a translation of the six before it

The other six take an arbitrary metadata key with no prior declaration. PgVector puts the whole
dictionary in a `jsonb` column and filters with `metadata @> $4::jsonb`
(`PgVectorStore.cs:352`); Qdrant, Weaviate, Pinecone, Chroma and Azure AI Search each have a native
payload or document field.

**RediSearch filters only on attributes declared in the index schema.** `CreateCollectionAsync`
declares four. Metadata keys are open-ended and typed four ways — `String`, `Number`, `Boolean`,
`DateTimeOffset`. There is no `jsonb @>` equivalent, and that gap is the entire design problem.

Three routes were considered.

**A — grow the schema on demand.** Write each entry as a prefixed flat field and `FT.ALTER SCHEMA
ADD` the first time a key appears. Any key becomes filterable with no configuration. `Alter` and
`AlterAsync` exist in NRedisStack 1.7.4, so the API is there. **Rejected on an unverified
behaviour:** whether `FT.ALTER` indexes documents that already exist. If it does not, chunks written
before a key was declared are invisible to a filter on it — the same silent miss this phase exists
to remove, reintroduced through a different door. Docker was unavailable at design time, so this was
not settled by measurement; it is recorded as the one fact that could promote A over B later.

**C — post-filter in process with over-fetch**, using the existing `MetadataFilterMatcher`. Any key
works with no schema change. **Rejected:** a selective filter then under-fills the page, which is
exactly the defect 6.2.4 fixed in `RaptorRetrievalBehavior` and 6.2.18 fixed again in deep research.
Reintroducing it inside a store is worse than either, because the caller cannot see it happening.

**RedisJSON** was considered and rejected without a letter: `FT.CREATE ... ON JSON` still requires
declared JSONPath attributes, so it does not solve the open-key problem, while breaking every read
path and widening the module dependency.

## 2. The decision — B: declared filterable keys, and a throw for everything else

Filterable metadata keys are declared when the store is constructed. They become TAG attributes in
the index schema. **A filter naming an undeclared key throws** rather than returning unfiltered
results. The empty default — no declared keys — means the store throws on *any* filter, which is the
honest statement of what it can do today rather than today's silence.

This is what STATE.md draws out of #490: where a guard is cheap, prefer a throw to a tolerant
return. It costs a configuration surface no other store has, and Redis becomes the one backend where
a filter key must be known at index-creation time. That cost is accepted deliberately, in exchange
for a store that cannot answer a filtered query with the wrong set of chunks.

## 3. Shape

### 3.1 Storage

`StoreAsync` writes a fifth hash field, `metadata`, holding
`MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata)` — the same serializer PgVector writes
into its `jsonb` column, so all four kinds round-trip identically across stores.

Written **unconditionally**: an empty dictionary serialises to `{}`, and a *missing* field therefore
means the hash predates this change. That distinction is what the upgrade path reads.

One JSON blob rather than flat fields for the return path, so a metadata key named `text`,
`embedding`, `document_id` or `chunk_index` cannot collide with the chunk's own fields.

### 3.2 Both read paths

`MapChunk` decodes the blob. `SearchAsync` adds `metadata` to its `ReturnFields` and decodes it the
same way. **Both change or neither should**: a store that returns metadata from a keyed lookup and
none from a search is the divergence 6.2.25 pulled Qdrant's mapping into one `MapChunk` to prevent.

**On a blob that will not deserialize, throw**, naming the document and chunk — matching
`WeaviateVectorStore.cs:444-449`.

**Corrected 2026-09-09, after this section was first approved.** It originally specified an empty-
dictionary fallback "matching PgVector, for cross-store consistency". Checking the provenance showed
the argument was backwards. Four stores deserialize a metadata blob: Weaviate throws; PgVector,
Qdrant and Azure AI Search return an empty dictionary. But the three are not three judgements — they
are the pre-review default from `179e4f8e` (2026-04-11, a mechanical serializer migration), and
Weaviate's throw is `98b327fd` (2026-07-25), a **review finding that deliberately replaced** that
default. Its commit message names what the other three still do: *"silently returning the chunk with
empty metadata"*.

So the majority carries no reasoning, and copying it would have copied the shape a review already
rejected — into the one store where a chunk reading as "no metadata" is indistinguishable from the
defect this phase exists to fix.

The inconsistency in the other three is filed as **#521** and is not touched here.

Numbers, booleans and dates are rendered by `MetadataValue.ToString()` on both the write and the
filter path — it already emits `InvariantCulture` for numbers, `true`/`false` for booleans and
`MetadataDateFormat.Format` for dates. **This supersedes §3.3's hand-rolled
`double.ToString(CultureInfo.InvariantCulture)`**: one existing accessor cannot drift from itself,
which is stronger than two formatters a test has to keep in agreement.

### 3.3 Filtering — TAG for every kind, with the kind inside the value

`SearchOptions.MetadataFilter` promises exact matching only. No ranges are required, which collapses
the TAG-versus-NUMERIC question entirely: **every filterable key is declared as a TAG field**, and
the stored token carries its kind:

| kind | token |
| --- | --- |
| `String` | `s:` + Base64Url(`acme`) |
| `Number` | `n:` + Base64Url(`3`) |
| `Boolean` | `b:` + Base64Url(`true`) |
| `DateTimeOffset` | `d:` + Base64Url(`2026-01-01T00:00:00Z`) |

The textual form of every value comes from `MetadataValue.ToString()`, which already emits
`InvariantCulture` for numbers, `true`/`false` for booleans and `MetadataDateFormat.Format` for
dates — so the write path and the filter path cannot drift, because they are one accessor.

The kind prefix is what makes `Metadata["page"] = 3` fail to match a filter of `"3"`, which
`SearchOptions` requires in as many words. It also removes the case where one key arrives as a
`Number` in one chunk and a `String` in another: those are two tokens on one TAG field, not a schema
conflict.

Numbers are written with `double.ToString(CultureInfo.InvariantCulture)` on **both** the write and
the filter path — round-trippable on .NET and identical on both sides, so `3` and `3.0` cannot
produce two tokens for one value. The two paths must call one shared helper rather than format
independently; two formatters that agree today are the mutation this design expects to survive
review and fail in production.

**Two RediSearch TAG defaults would break this silently, and both are amended in 2026-09-09.**

**A TAG field splits its value on a separator, `,` by default.** A metadata string containing a
comma would therefore store as two tags and match neither the whole value nor, reliably, anything
else. No separator character is safe, because a string value can contain any character. **So the
value part of the token is Base64Url-encoded** — `System.Buffers.Text.Base64Url.EncodeToString`,
the same API 6.2.30 used on the Azure key, and for the same reason: its alphabet is a subset of
what the field accepts, so it is uniformly safe rather than safe for whichever values someone
happened to test. The token is `kind` + `:` + `Base64Url(value.ToString())`.

Dropping that encoding is the simplification a later reader reaches for, and it passes every test
whose metadata values are well-behaved words — which is exactly what 6.2.30 recorded about raw
concatenation on the Azure key.

**TAG fields are case-insensitive by default.** `MetadataValue.Equals` compares strings with
`StringComparison.Ordinal`, so a store folding `ACME` into `acme` would answer a filter with chunks
the contract says do not match. **The `md_*` fields are declared `caseSensitive: true`.** Base64Url
does not rescue this on its own: its alphabet uses both cases, so case folding could match two
tokens that decode to different values.

The query becomes:

```text
(@md_tenant:{s\:YWNtZQ} @md_page:{n\:Mw})=>[KNN 5 @embedding $vec AS vector_score]
```

with **TAG escaping applied to the value** — the escaping the keyed lookup deliberately sidesteps by
never issuing a query (`RedisVectorStore.cs:278-279`). Here there is a query, so it applies.

Field naming: a declared key `tenant` becomes the hash field and index attribute `md_tenant`. The
`md_` prefix keeps the metadata namespace disjoint from the four structural fields, so a declared
key named `text` is representable.

An empty or null `MetadataFilter` is not a filter: the query keeps its `*` prefix unchanged.

### 3.4 The guard that makes the promise real

`InitializeAsync` **deliberately leaves an existing index alone**, because dropping it discards
every stored vector (`RedisVectorStore.cs:95-97`). So on an upgraded deployment the live index still
carries the old four attributes, the `md_*` TAG fields are absent, and a filter on a correctly
declared key would fail at the server — or be dropped.

**Initialisation therefore reads `FT.INFO` and throws when the live index is missing any configured
filterable key**, naming the key and stating that the index needs recreating.

Without this, design B's guarantee holds only on a freshly created index — which is not where this
defect lives. This check, not the undeclared-key throw, is the load-bearing guard.

### 3.5 Configuration

Filterable keys are a constructor parameter defaulting to empty, surfaced through both `UseRedis`
overloads in `RedisBuilderExtensions`. Both existing constructors keep their current signatures for
callers who pass nothing.

## 4. What is deliberately not in scope

- **A seven-store conformance test asserting every remote store honours `MetadataFilter`.** It is
  the guard that would have caught this, and it is filed separately: it needs all seven services in
  one CI tier, which is a test-infrastructure decision rather than a Redis one.
- **Making arbitrary undeclared keys filterable.** That is approach A, and it is gated on the
  `FT.ALTER` measurement recorded in §1.
- **RediSearch text scoring / hybrid search.** Still declined, for the reasons already in the
  store's own remarks: its TF-IDF-shaped scoring is not the BM25 the hybrid arm expects.
- **Range or partial-match filtering.** `SearchOptions.MetadataFilter` is exact-match by contract.

## 5. How it will be proved

Against a real Redis (`redis/redis-stack-server`) via testcontainers, as all seven keyed lookups
were, and mutation-tested. The mutations that must each be caught by a named test:

| mutation | expected to be caught by |
| --- | --- |
| drop the kind prefix from the token | a filter of `"3"` against `Metadata["page"] = 3` |
| drop the TAG escaping | a filter value containing `-` and `:` |
| ignore `MetadataFilter` entirely (today's behaviour) | any filtered-search test |
| drop `metadata` from `ReturnFields` | a search asserting metadata, where the lookup still passes |
| skip the `FT.INFO` check | an index created before the keys were declared |
| treat an empty filter dictionary as a filter | a search with an empty filter returning the full page |
| accept an undeclared key instead of throwing | a filter on a key not declared |

**6.2.26's existing assertion that this store returns no metadata is expected to fail** and is
rewritten to assert the opposite. It was written to fail on this day.

The negative-`chunk_index` test stays green throughout: on all seven backends it has been the only
thing catching an unsigned-index implementation, and nothing here should disturb it.

## 6. Consequences

**Breaking.** The index schema gains the declared `md_*` attributes, so an existing Redis index
needs recreating and re-ingesting — the same break 6.2.30 took on Azure AI Search, and taken now for
the same reason: pre-1.0 is when it is cheap. The `FT.INFO` check turns it from a silent behaviour
change into a startup error naming the missing key.

**Two false claims become true.** `TextChunk.Metadata`'s "every store persists and filters on that
type" holds once this ships — for declared keys. The store's own remarks stating that these chunks
carry no metadata, and that the search path returns none, are rewritten.

**Redis remains the only store with a declaration step**, and its README and the guide must say so
plainly: an undeclared key is a throw, not a silent pass.
