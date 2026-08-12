# Rag.NET.Graph

A standalone knowledge-graph library: modularity community detection, PageRank, and an
`IGraphStore` abstraction with a SQLite implementation. No pipeline dependency — this is
the graph engine `Rag.NET.GraphRag` builds on, usable on its own.

## Install

```bash
dotnet add package Rag.NET.Graph
```

## Setup

There is nothing to register — construct the store you want and go:

```csharp
using Rag.NET.Graph;

await using var store = new SqliteGraphStore("graph.db");
```

## Example

```csharp
using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;

var entities = new[]
{
    new GraphEntity("Ada Lovelace", "Person", "Wrote the first published algorithm"),
    new GraphEntity("Analytical Engine", "Machine", "Babbage's proposed mechanical computer"),
};
var relationships = new[]
{
    new GraphRelationship("Ada Lovelace", "Analytical Engine", "wrote programs for", Weight: 2.0),
};

await using var store = new SqliteGraphStore("graph.db");
await store.AddEntitiesAsync(entities);
await store.AddRelationshipsAsync(relationships);

var neighbors = await store.GetNeighborsAsync("Ada Lovelace", depth: 2);

// Community detection and PageRank operate on a snapshot of the whole graph.
var graph = await store.GetFullGraphAsync();
var communities = LouvainWithRefinement.Detect(graph, new LouvainWithRefinementOptions { Resolution = 1.0 });
var ranks = PageRank.Compute(graph);
```

`LouvainWithRefinement` was called `Leiden` until 0.1.0. It is Louvain's local moving and
aggregation with a refinement pass, not Traag/Waltman/van Eck's Leiden algorithm, and it does not
provide that paper's guarantee that every returned community is internally connected — a ten-node
counterexample is pinned in the test suite. `Leiden` and `LeidenOptions` remain as `[Obsolete]`
forwarders; the type's own XML remarks give the three places it departs from the paper.

## Full guide

- [GraphRAG](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/graphrag.md)
