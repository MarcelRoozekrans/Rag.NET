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

Which search runs is decided by the behaviors you add to the retrieval pipeline:
`GraphLocalSearchBehavior` for entity questions, `GraphGlobalSearchBehavior` for
"what are the main themes?" questions over community reports. `UseMindMapExtraction`
adds hierarchical mind-map nodes instead of flat entities.

## Full guide

- [GraphRAG](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/graphrag.md)
