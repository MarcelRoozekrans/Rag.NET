# BM25 In-Memory Hybrid Search Design

**Goal:** Enable hybrid search (keyword + semantic) on all vector stores, not just Azure AI Search, by building an in-memory BM25 index inside `RagPipeline`.

**Architecture:** `RagPipeline` (singleton) holds an `InMemoryBm25Index` field. During `IngestAsync`, chunks are added to the index. During `RetrieveAsync`, when `UseHybridSearch = true`:
- If the vector store implements `IHybridSearchable` → existing path (Azure AI Search native BM25)
- Otherwise → dense search + BM25 search run synchronously, results merged via Reciprocal Rank Fusion (RRF)

No new DI surface — `InMemoryBm25Index` is an internal implementation detail of `RagPipeline`.

**New files:**
- `src/Rag.NET/Search/InMemoryBm25Index.cs` — BM25 index with Add, Search, Remove
- `src/Rag.NET/Search/RrfMerger.cs` — Reciprocal Rank Fusion merger

**BM25 Index:**
- Tokenizer: lowercase + split on non-alphanumeric (no external dependencies)
- Inverted index: `Dictionary<string, List<(int docId, int tf)>>`, document length array
- Parameters: k1=1.5, b=0.75 (Lucene defaults)
- Thread safety: `ReaderWriterLockSlim` — concurrent reads, exclusive writes
- `Add(int docId, TextChunk chunk)` — index a chunk by integer id
- `Search(string query, int topK) → IReadOnlyList<(int docId, double score)>` — returns BM25-ranked results
- `Remove(string documentId)` — remove all postings for a document (called from `DeleteAsync`)

**RRF Merge:**
- Formula: `score(chunk) = Σ 1/(60 + rank_i)` over dense and BM25 result lists
- k=60 (standard constant, stable across score magnitudes)
- Deduplicates by chunk identity, returns top-K

**RagPipeline changes:**
- Add `private readonly InMemoryBm25Index _bm25Index = new()`
- `IngestAsync` — after chunking, call `_bm25Index.Add(...)` for each chunk
- `DeleteAsync` — also call `_bm25Index.Remove(documentId)`
- `RetrieveAsync` — when `UseHybridSearch = true` and store is not `IHybridSearchable`, run both searches and merge with `RrfMerger.Merge(dense, bm25, topK)`

**Overwrite support:** `IngestAsync` with `options.Overwrite = true` calls `_bm25Index.Remove(documentId)` before re-indexing.
