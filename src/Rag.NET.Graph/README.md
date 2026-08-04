# Rag.NET.Graph

A standalone knowledge-graph library: Leiden community detection, PageRank, and an
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
var communities = Leiden.Detect(graph, new LeidenOptions { Resolution = 1.0 });
var ranks = PageRank.Compute(graph);
```

## Full guide

- [GraphRAG](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/graphrag.md)
