# RAPTOR — Recursive Abstractive Processing for Tree-Organized Retrieval

RAPTOR builds a hierarchical tree of summaries at ingestion time so that retrieval can match at both fine-grained (leaf chunk) and abstract (summary) levels simultaneously. This addresses a core limitation of flat chunking: questions about the overall theme of a document may not match any individual chunk well.

## When to Use RAPTOR

- **Long documents** (10+ pages) where high-level questions are expected
- **Multi-topic documents** where readers may ask about themes that span sections
- **Knowledge bases** where both specific facts and broad overviews matter

Avoid RAPTOR for short documents (< 5 chunks) or when latency at ingestion time is critical — tree building requires LLM calls per cluster per level.

## How It Works

### Ingestion (Tree Building)

1. **Start with leaf chunks** — your normal chunked + embedded document
2. **UMAP reduction** — reduce embedding dimensions (e.g. 1536 → 10) for efficient clustering
3. **GMM clustering** — soft-cluster chunks using Gaussian Mixture Models; BIC selects optimal cluster count
4. **Summarize each cluster** — concatenate chunk texts, call LLM to produce a summary
5. **Embed summaries** — generate embeddings for each summary
6. **Recurse** — repeat steps 2-5 on the summaries until one cluster remains (or MaxTreeDepth reached)
7. **Store everything** — leaf chunks + all summary levels go to the vector store

Each summary chunk carries metadata:
- `raptor_level` — tree depth (1 = first summary, 2 = summary of summaries, etc.)
- `raptor_cluster_id` — which cluster within the level
- `raptor_child_ids` — comma-separated chunk indices of children

### Retrieval

Three modes control how RAPTOR chunks participate in search:

| Mode | Behaviour | Best for |
|------|-----------|----------|
| **Blend** (default) | All levels participate via natural vector similarity | General use — let the embeddings decide |
| **Boost** | Multiply summary chunk scores by `SummaryBoostFactor` | When broad questions are common |
| **Filter** | Restrict to specific levels via `MinRaptorLevel` / `MaxRaptorLevel` | When you know the abstraction level needed |

## Quick Start

```csharp
// Install: dotnet add package Rag.NET.Raptor

services.AddRagNet(
    configure: rag => rag.UseRaptor(),
    ingestion: pipeline => pipeline
        .Add<RaptorIngestionBehavior>(after: typeof(EmbeddingBehavior)),
    retrieval: pipeline => pipeline
        .Add<RaptorRetrievalBehavior>(before: typeof(RerankingBehavior))
);
```

## Configuration

### Ingestion Options

```csharp
rag.UseRaptor(options =>
{
    options.Enabled = true;                  // Toggle RAPTOR on/off
    options.MinChunksForRaptor = 5;          // Skip for small documents
    options.ReducedDimensionality = 10;      // UMAP target dims — must be greater than 0
    options.MaxClusters = null;              // null = BIC auto-selects; when set, must be greater than 1
    options.MaxTreeDepth = null;             // null = recurse until 1 cluster; when set, must be greater than 0
    options.StoreLeafChunks = true;          // Keep originals alongside summaries
    options.SummaryChatClient = cheapModel;  // Optional: cheaper model for summaries
    options.SummaryEmbedder = fastEmbedder;  // Optional: separate embedder
});
```

`UseRaptor` validates the configured options at registration and throws `ArgumentException` from the configuring line. The bounds are not pedantry: `MaxClusters = 1` or `MaxTreeDepth = 0` would build no summary levels at all — RAPTOR silently disabled while `Enabled` still reads `true` — and a non-positive `ReducedDimensionality` would leave clustering nothing to work on or crash mid-ingestion.

### Retrieval Options

```csharp
rag.UseRaptor(
    retrieval: options =>
    {
        options.Mode = RaptorRetrievalMode.Boost;
        options.SummaryBoostFactor = 1.5;    // Score multiplier for summaries — must be greater than 0, and finite
        options.MinRaptorLevel = null;       // Level filter lower bound — must not exceed MaxRaptorLevel
        options.MaxRaptorLevel = null;       // Level filter upper bound — when set, must be zero or positive
    }
);
```

These are validated at registration too: `SummaryBoostFactor = 0` would bury every summary and a negative factor would invert their ranking — the opposite of what Boost mode is for — while an empty Filter window (`MinRaptorLevel > MaxRaptorLevel`, or a negative `MaxRaptorLevel`) would remove every result on every retrieval.

## Cost and Performance

### Ingestion Cost

RAPTOR adds LLM calls at ingestion time:

| Document size | Typical clusters | LLM calls (1 level) | LLM calls (2 levels) |
|---------------|-----------------|---------------------|---------------------|
| 5-10 chunks | 2-3 | 2-3 | 3-4 |
| 20-50 chunks | 3-6 | 3-6 | 6-9 |
| 100+ chunks | 5-10 | 5-10 | 10-15 |

**Mitigation strategies:**
- Use a cheaper/faster model via `SummaryChatClient` (e.g. GPT-4o-mini, Haiku)
- Cap tree depth with `MaxTreeDepth = 1` for single-level summaries
- Increase `MinChunksForRaptor` to skip small documents

### Retrieval Cost

RAPTOR adds **zero** latency at retrieval time in Blend mode — summary chunks are just additional vectors in the store. Boost mode adds negligible post-processing. Filter mode may reduce result count.

### Storage

Summary chunks are stored alongside leaf chunks. Typical overhead: 10-30% more vectors depending on document structure and tree depth.

## Pipeline Positioning

```
Ingestion:  Parse → Chunk → Embed → [RAPTOR] → Store
Retrieval:  VectorStore → Ensemble → Filter → [RAPTOR] → Rerank → ...
```

RAPTOR ingestion runs **after** EmbeddingBehavior (needs embeddings) and **before** StorageBehavior (adds summary chunks to the batch).

RAPTOR retrieval runs **before** RerankingBehavior (score adjustments should happen before reranking) and after the vector store returns results.

## Retrieval Modes in Detail

### Blend (Default)

No score adjustment. Summary chunks compete with leaf chunks purely on vector similarity. This works well because:
- Broad queries naturally match broad summaries
- Specific queries naturally match specific leaf chunks
- The embedding space handles the routing

### Boost

Multiplies scores of chunks where `raptor_level > 0` by `SummaryBoostFactor`:

```csharp
options.Mode = RaptorRetrievalMode.Boost;
options.SummaryBoostFactor = 1.5; // 50% boost for summaries
```

Use when your query workload skews toward overview/theme questions.

### Filter

Restricts results to specific tree levels:

```csharp
// Only summaries (no leaf chunks)
options.Mode = RaptorRetrievalMode.Filter;
options.MinRaptorLevel = 1;

// Only top-level summaries
options.Mode = RaptorRetrievalMode.Filter;
options.MinRaptorLevel = 2;

// Only leaf chunks (disable RAPTOR retrieval effectively)
options.Mode = RaptorRetrievalMode.Filter;
options.MaxRaptorLevel = 0;
```

## Troubleshooting

**RAPTOR is not creating any summary chunks**
- Check that `Enabled = true` (default)
- Ensure your document produces at least `MinChunksForRaptor` chunks (default 5)
- Verify `IChatClient` is registered in DI (or `SummaryChatClient` is set)

**Too many/few clusters**
- Set `MaxClusters` to cap the number of clusters per level
- Adjust `ReducedDimensionality` — lower values = coarser clustering

**Summaries are too generic**
- Customize `SummaryPrompt` to be more specific to your domain
- Reduce cluster sizes by increasing the number of clusters

**High ingestion latency**
- Use a cheaper model via `SummaryChatClient`
- Set `MaxTreeDepth = 1` to limit to one summary level
- Increase `MinChunksForRaptor` to skip small documents
