# Rag.NET Gap Fixes Design

## Overview

This document captures the approved design for fixing 8 gaps identified in the Rag.NET codebase. Each fix is scoped to be minimal and non-breaking.

---

## 1. Fix RecursiveChunkingStrategy Overlap & Position Tracking

**Problem:** `RecursiveChunkingStrategy` ignores `ChunkingOptions.Overlap` entirely, and `StartPosition`/`EndPosition` on `TextChunk` are always wrong (`StartPosition = 0`, `EndPosition = text.Length` relative to chunk, not source).

**Fix:**
- Track a `position` cursor through the source text to compute correct `StartPosition`/`EndPosition` relative to the original `DocumentSection.Text`.
- After yielding each chunk, look back `Overlap` characters into the previous chunk's text and prepend that overlap to the next chunk's input before splitting continues.
- Implementation stays inside `RecursiveChunkingStrategy.cs` only. The `SplitRecursively` method returns raw strings; the `ChunkAsync` method handles overlap and position tracking at the top level.

**Files:** `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs`

---

## 2. Propagate DocumentMetadata.Tags to TextChunk.Metadata

**Problem:** `DocumentMetadata.Tags` are never copied to `TextChunk.Metadata` during ingestion, so stored chunks lose document-level tags.

**Fix:**
- In `RagPipeline.IngestAsync`, after chunking, copy `metadata.Tags` into each `TextChunk.Metadata` (only keys that don't already exist in the chunk's metadata, to allow parser-set metadata to win).
- Also add `"document_id"` and `"file_name"` to chunk metadata automatically.

**Files:** `src/Rag.NET/Pipeline/RagPipeline.cs`

---

## 3. Expose DeleteAsync on IRagPipeline

**Problem:** `IVectorStore.DeleteByDocumentIdAsync` exists but there's no pipeline-level delete. Users must resolve `IVectorStore` directly.

**Fix:**
- Add `Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)` to `IRagPipeline`.
- Implement in `RagPipeline` as a direct delegation to `vectorStore.DeleteByDocumentIdAsync`.

**Files:** `src/Rag.NET/Abstractions/IRagPipeline.cs`, `src/Rag.NET/Pipeline/RagPipeline.cs`

---

## 4. Wire Metadata Filtering Through to Vector Stores

**Problem:** `SearchOptions.MetadataFilter` and `RetrievalOptions.MetadataFilter` exist as `IDictionary<string, string>?` but no vector store implementation reads them.

**Fix per store:**
- **PgVector:** Add `WHERE metadata @> @filter::jsonb` clause when `MetadataFilter` is non-null. Build a JSON object from the dictionary and pass as a parameter.
- **Qdrant:** Build `Must` conditions with `MatchValue` for each key-value pair in the filter, pass as `Filter` on `SearchPoints`.
- **Azure AI Search:** Build an OData `$filter` string like `metadata/key eq 'value'` joined with `and`, pass via `SearchOptions.Filter`.

**Files:** `src/Rag.NET.PgVector/PgVectorStore.cs`, `src/Rag.NET.Qdrant/QdrantVectorStore.cs`, `src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs`

---

## 5. Wire IHybridSearchable into Pipeline

**Problem:** `IHybridSearchable` interface exists but the pipeline never calls it. There's no way for users to opt into hybrid search.

**Fix:**
- Add `bool UseHybridSearch { get; set; }` to `RagOptions`, `RetrievalOptions`, and `SearchOptions`.
- In `RagPipeline.RetrieveAsync`, check if `UseHybridSearch` is true AND `vectorStore is IHybridSearchable hybrid`. If so, call `hybrid.HybridSearchAsync(query, embedding, searchOptions)` instead of `vectorStore.SearchAsync(embedding, searchOptions)`.
- If `UseHybridSearch` is requested but the store doesn't implement `IHybridSearchable`, throw `InvalidOperationException`.
- Azure AI Search already has hybrid capabilities — implement `IHybridSearchable` on `AzureAISearchVectorStore`.

**Files:** `src/Rag.NET/Models/Options/RagOptions.cs`, `src/Rag.NET/Models/Options/RetrievalOptions.cs`, `src/Rag.NET/Models/Options/SearchOptions.cs`, `src/Rag.NET/Pipeline/RagPipeline.cs`, `src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs`

---

## 6. Implement ICollectionManageable in Vector Stores

**Problem:** `ICollectionManageable` interface exists but no vector store implements it.

**Fix:**
- **PgVector:** `CreateCollectionAsync` → `CREATE TABLE IF NOT EXISTS {name} (...)`, `DeleteCollectionAsync` → `DROP TABLE IF EXISTS {name}`, `CollectionExistsAsync` → query `information_schema.tables`.
- **Azure AI Search:** `CreateCollectionAsync` → `CreateOrUpdateIndexAsync`, `DeleteCollectionAsync` → `DeleteIndexAsync`, `CollectionExistsAsync` → `GetIndexAsync` with try/catch.
- Register via DI as `ICollectionManageable` (resolved from the same instance as `IVectorStore`, not exposed on `IRagPipeline`). Users resolve `ICollectionManageable` directly for infrastructure operations.

**Files:** `src/Rag.NET.PgVector/PgVectorStore.cs`, `src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs`, DI registration files

---

## 7. Add Multi-Turn Conversation Support

**Problem:** `AskAsync` and `AskStreamingAsync` are single-turn only. No way to pass conversation history.

**Fix:**
- Add `IList<ChatMessage>? ConversationHistory { get; set; }` to `RagOptions`.
- In `RagPipeline.AskAsync` and `BuildRagMessages`, when `ConversationHistory` is non-null, insert the history messages between the system prompt and the new user message with context.
- Message order: `[System] → [History...] → [User with RAG context + question]`.

**Files:** `src/Rag.NET/Models/Options/RagOptions.cs`, `src/Rag.NET/Pipeline/RagPipeline.cs`

---

## 8. Gaps Not Fixed (Intentional)

- **Qdrant `ICollectionManageable`:** Qdrant's gRPC client already has `CreateAsync`/`DeleteAsync` on the collection level. Adding a wrapper adds no value.
- **Qdrant `IHybridSearchable`:** Qdrant's hybrid search requires sparse vectors and a different indexing strategy. Out of scope for this iteration.

---

## Summary

| # | Gap | Scope |
|---|-----|-------|
| 1 | Overlap + position tracking | RecursiveChunkingStrategy |
| 2 | Tag propagation | RagPipeline.IngestAsync |
| 3 | DeleteAsync on pipeline | IRagPipeline + RagPipeline |
| 4 | Metadata filtering | All 3 vector stores |
| 5 | Hybrid search wiring | Pipeline + AzureAISearch |
| 6 | Collection management | PgVector + AzureAISearch |
| 7 | Multi-turn conversation | RagOptions + RagPipeline |
