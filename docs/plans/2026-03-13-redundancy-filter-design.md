# Redundancy Filter Design

**Goal:** Drop near-duplicate chunks from search results post-retrieval so downstream prompts don't waste context on repeated content.

**Approach:** Option A — re-embed retrieved chunks. No changes to `IVectorStore`, `SearchResult`, or any vector store implementation.

**New type:** Static class `RedundancyFilter` in `src/Rag.NET/PostRetrieval/` (mirrors `LostInTheMiddleReorderer`).

**Algorithm:** Re-embed all retrieved chunk texts in one batch call. Greedily accept each chunk if its cosine similarity to every already-accepted chunk is below `threshold`. Preserves original relevance order among accepted chunks.

**API changes:**
- `RetrievalOptions`: add `UseRedundancyFilter` (bool, default false) and `RedundancyThreshold` (float, default 0.95f)
- `RagOptions`: same two properties, propagated into `RetrievalOptions` in `AskAsync`/`AskStreamingAsync`

**Cost:** One extra `IEmbeddingGenerator.GenerateAsync` batch call per `RetrieveAsync` when enabled. For TopK=5–20, this is negligible vs. the vector search latency.
