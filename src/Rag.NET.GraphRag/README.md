# Rag.NET.GraphRag

GraphRAG for Rag.NET: an LLM extracts entities and relationships during ingestion, modularity
community detection organises them (via `Rag.NET.Graph`), and retrieval answers entity
questions with local graph search or corpus-wide questions with community-report
map-reduce.

## Install

```bash
dotnet add package Rag.NET.GraphRag
```

## Setup

GraphRAG adds behaviors to both pipelines:

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.GraphRag;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Retrieval.Behaviors;

services.AddRagNet(
    configure: rag => rag.UseGraphRag(
        options => options.GleaningPasses = 1,
        retrieval: options => options.PageRankWeight = 0.3,
        graph: store => store.UseSqlite("graphrag.db")),
    ingestion: p => p
        .Add<GraphEntityExtractionBehavior>(after: typeof(EmbeddingBehavior))
        .Add<CommunityDetectionBehavior>(after: typeof(GraphEntityExtractionBehavior)),
    retrieval: p => p
        .Add<GraphLocalSearchBehavior>(before: typeof(RerankingBehavior)));
```

## Example

Constrain extraction and route cheap models to the high-volume LLM work:

```csharp
rag.UseGraphRag(options =>
{
    options.GleaningPasses             = 1;                        // follow-up extraction passes
    options.EntityTypes                = ["Person", "Organization"]; // null = open set
    options.MaxEntityDescriptionLength = 500;                      // summarisation threshold
});
```

Tune the clustering itself through `options.CommunityDetection`:

```csharp
rag.UseGraphRag(options =>
{
    options.CommunityDetection.Resolution    = 1.0;   // higher splits into more, smaller communities
    options.CommunityDetection.MaxIterations = 10;    // local-moving passes per level
    options.CommunityDetection.MaxLevels     = null;  // null = aggregate until no further improvement
    options.CommunityDetection.RandomSeed    = 42;    // fixed, so clustering is reproducible
    options.CommunityDetection.Randomness    = 0.01;  // θ in the refinement's merge draw; must be > 0
});
```

This property was called `options.Leiden` until 0.1.0, and the clusterer behind it `Leiden`. It is
Louvain with Traag/Waltman/van Eck's refinement phase, so every returned community is connected in
the subgraph it induces — the old names remain as `[Obsolete]` forwarders, and
`LouvainWithRefinement`'s XML remarks give where that guarantee comes from and what it does not
promise.

`Resolution` is the one worth reaching for: it scales modularity's penalty term, so raise it
when communities come out too large to summarise usefully and lower it when the graph
fragments into many small ones. Values are checked when you configure them — a resolution of
zero or below is rejected at that line rather than silently returning one community.

Which search runs is decided by the behaviors you add to the retrieval pipeline:
`GraphLocalSearchBehavior` for entity questions, `GraphGlobalSearchBehavior` for
"what are the main themes?" questions over community reports. `UseMindMapExtraction`
adds hierarchical mind-map nodes instead of flat entities.

## Full guide

- [GraphRAG](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/graphrag.md)
