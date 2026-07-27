# Breaking Changes

Public-API and on-disk breaks, recorded as they happen so Phase 4.1 (NuGet Publishing) can turn
this into release notes rather than reconstructing it from git history.

Rag.NET has not published a NuGet package yet — `v1.0` is Milestone 4. Until then breaks are
taken cleanly rather than carried behind compatibility shims, and each one is recorded here with
the reasoning that justified it.

## Unreleased

### `FileNameSanitizer` moved assembly and namespace

**Phase 2.5, Part B.** `Rag.NET.DataProviders.FileNameSanitizer` → `Rag.NET.FileNameSanitizer`,
moving from `Rag.NET.DataProviders` to `Rag.NET.Abstractions`.

| Break | Affects |
|---|---|
| **Source** | Code naming the type through its old namespace, or reaching it via a `using Rag.NET.DataProviders;` that no longer supplies it. |
| **Binary** | Pre-compiled assemblies referencing `Rag.NET.DataProviders.FileNameSanitizer` fail at **load** with `TypeLoadException`, not at compile. |

**Why the namespace is bare `Rag.NET` rather than `Rag.NET.Abstractions.Naming`:** the assembly
already declares its shared helpers under bare `Rag.NET` (`DeterministicChunkId`,
`MetadataSerializer`, `RagJsonSerializerContext`), and that namespace encloses every connector
namespace. Consumers therefore resolve the type by enclosing-namespace lookup with no `using`
change — all nine connectors compiled untouched.

**No `[TypeForwardedTo]` was added.** A forwarder exists to keep already-compiled consumer
assemblies loading, and no Rag.NET package has ever been published, so there are none. Adding one
now would put permanent compatibility cruft in the first release to preserve compatibility with a
release that does not exist. **Revisit at v1.0:** after that point, moving a public type between
assemblies needs a forwarder or a major-version bump.

**Migration:** drop any `Rag.NET.DataProviders` qualification on the type. Most code needs no
change; only code that fully qualified it does.

### BM25 SQLite schema gains an index

**Phase 2.5, Part A.** `CreateSchema` now issues
`CREATE INDEX IF NOT EXISTS ix_bm25_docs_document_id ON bm25_docs(document_id)`.

Not an API break, but it is a **persisted change to existing user database files**: it applies
silently on the next open of an already-populated database. It is additive and idempotent, so
there is no migration step and no rollback hazard beyond the index remaining if a user downgrades.
It exists because re-ingest now removes prior postings on every ingestion path rather than only
under `Overwrite`, which made `SqliteBm25Index.Remove` a full-table scan per ingest.

### Re-ingest is now a replace for BM25

**Phase 2.5, Part A.** Ingesting the same `DocumentId` twice previously appended a second complete
set of BM25 postings — duplicate hits and inflated term statistics — because the caller never
removed before re-adding and `IngestionOptions.Overwrite` defaults to `false`. Removal is now
unconditional for the BM25 index and the data manager.

**Behaviour users will notice:** keyword and hybrid scores change for any corpus that had been
re-ingested, because the inflated term statistics are gone. This is a correction, but it is not
score-neutral.

**Still not a full replace.** The vector store upserts on `(documentId, chunkIndex)`, so a
re-ingested document that is *shorter* than its predecessor leaves the tail chunks stranded and
retrievable. Making delete-before-insert unconditional would change what `Overwrite` means for
every existing caller, so it is deliberately out of scope. After this phase, re-ingest is a clean
replace for BM25 and a partial replace for vectors.
