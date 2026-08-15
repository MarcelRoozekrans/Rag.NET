# Rag.NET.GraphRag

GraphRAG for Rag.NET: an LLM extracts entities and relationships during ingestion, Leiden
community detection organises them (via `Rag.NET.Graph`), and retrieval answers entity
questions with local graph search or corpus-wide questions with community-report
map-reduce.

## Install

```bash
dotnet add package Rag.NET.GraphRag
```

## Setup

GraphRAG adds behaviors to both pipelines, and `UseGraphRag` places them:

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.GraphRag;

services.AddRagNet(rag => rag.UseGraphRag(
    options => options.GleaningPasses = 1,
    retrieval: options => options.PageRankWeight = 0.3,
    graph: store => store.UseSqlite("graphrag.db")));
```

`GraphEntityExtractionBehavior` lands after `EmbeddingBehavior`, `CommunityDetectionBehavior`
after that, and `GraphLocalSearchBehavior` before `RerankingBehavior`. `GraphGlobalSearchBehavior`
is deliberately left out: it runs an LLM map-reduce over community reports on every query, so it
stays opt-in. Add it — or choose different positions — with the pipeline delegates. `Add` is
idempotent and those delegates run first, so your placement wins and each behavior appears once:

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.GraphRag;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Retrieval.Behaviors;

services.AddRagNet(
    configure: rag => rag.UseGraphRag(
        graph: store => store.UseSqlite("graphrag.db")),
    ingestion: p => p
        .Add<GraphEntityExtractionBehavior>(after: typeof(EmbeddingBehavior))
        .Add<CommunityDetectionBehavior>(after: typeof(GraphEntityExtractionBehavior)),
    retrieval: p => p
        .Add<GraphLocalSearchBehavior>(before: typeof(RerankingBehavior))
        .Add<GraphGlobalSearchBehavior>(before: typeof(RerankingBehavior)));
```

## Example

Constrain extraction and route cheap models to the high-volume LLM work:

```csharp
rag.UseGraphRag(options =>
{
    options.GleaningPasses             = 1;                        // follow-up extraction passes
    options.EntityTypes                = ["Person", "Organization"]; // null = open set
    options.MaxEntityDescriptionLength = 500;                      // summarisation threshold
    options.CommunityReportConcurrency = 4;                        // report LLM calls in flight; must be > 0
});
```

Community reports are generated up to `CommunityReportConcurrency` at a time, and the result is
the same at any value: every prompt is built first, in order, and each answer is written back to
the community whose prompt produced it. The provider's rate limit is the real ceiling — measure
before raising it.

Tune the clustering itself through `options.Leiden`:

```csharp
rag.UseGraphRag(options =>
{
    options.Leiden.Resolution    = 1.0;   // higher splits into more, smaller communities
    options.Leiden.MaxIterations = 10;    // local-moving passes per level
    options.Leiden.MaxLevels     = null;  // null = aggregate until no further improvement
    options.Leiden.RandomSeed    = 42;    // fixed, so clustering is reproducible
    options.Leiden.Randomness    = 0.01;  // θ in the refinement's merge draw; must be > 0
});
```

The clusterer behind it is `Rag.NET.Graph`'s `Leiden` — Traag/Waltman/van Eck's algorithm over
modularity, Louvain with the paper's refinement phase between local moving and aggregation — so
every returned community is connected in the subgraph it induces. Its XML remarks give where that
guarantee comes from and what it does not promise.

`Resolution` is the one worth reaching for: it scales modularity's penalty term, so raise it
when communities come out too large to summarise usefully and lower it when the graph
fragments into many small ones. Values are checked when you configure them — a resolution of
zero or below is rejected at that line rather than silently returning one community.

Which search runs is decided by the behaviors in the retrieval pipeline:
`GraphLocalSearchBehavior` for entity questions (placed for you), `GraphGlobalSearchBehavior` for
"what are the main themes?" questions over community reports (opt-in, as above).
`UseMindMapExtraction` adds hierarchical mind-map nodes instead of flat entities; it places its
own ingestion behavior, and `ExtractAtIngestion = true` switches it on.

## Full guide

- [GraphRAG](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/graphrag.md)
