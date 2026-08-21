# Rag.NET.Raptor

RAPTOR for Rag.NET — Recursive Abstractive Processing for Tree-Organized Retrieval:
chunks are clustered (UMAP + GMM), each cluster is summarised, and the summaries are
clustered again, producing a tree whose upper levels answer broad questions that
leaf-only retrieval misses.

## Install

```bash
dotnet add package Rag.NET.Raptor
```

## Setup

RAPTOR adds one behavior to each pipeline, and `UseRaptor` places both. `TreeScope` defaults to
`Corpus` — clustering over the whole corpus, not one document at a time — which needs somewhere to
keep leaf chunks between ingests, so `leafStorePath` is required unless you opt back into
`PerDocument`:

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Raptor;

services.AddRagNet(rag => rag.UseRaptor(leafStorePath: "raptor-leaves.db"));
```

`RaptorIngestionBehavior` lands directly after `EmbeddingBehavior` (it needs the embeddings) and
`RaptorRetrievalBehavior` directly before `RerankingBehavior` (score adjustments settle before
reranking sees them). To choose other positions, name them yourself — `Add` is idempotent and the
pipeline delegates run first, so your placement wins and each behavior appears exactly once:

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Raptor;
using Rag.NET.Retrieval.Behaviors;

services.AddRagNet(
    configure: rag => rag.UseRaptor(leafStorePath: "raptor-leaves.db"),
    ingestion: pipeline => pipeline
        .Add<RaptorIngestionBehavior>(after: typeof(EmbeddingBehavior)),
    retrieval: pipeline => pipeline
        .Add<RaptorRetrievalBehavior>(before: typeof(RerankingBehavior)));
```

Prefer isolated per-document trees instead — no cross-document summaries, no leaf store required:

```csharp
services.AddRagNet(rag => rag.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument));
```

## Example

Tune tree construction and how summaries compete with leaf chunks at query time:

```csharp
rag.UseRaptor(
    options =>
    {
        options.MinChunksForRaptor = 5;      // skip small documents
        options.StoreLeafChunks    = true;   // keep originals alongside summaries
    },
    retrieval: options =>
    {
        options.Mode               = RaptorRetrievalMode.Boost;
        options.SummaryBoostFactor = 1.5;    // score multiplier for summaries
    },
    leafStorePath: "raptor-leaves.db");
```

`RaptorRetrievalMode.Filter` with `MinRaptorLevel`/`MaxRaptorLevel` restricts results to
specific tree levels instead of boosting.

## Full guide

- [RAPTOR](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/raptor.md)
