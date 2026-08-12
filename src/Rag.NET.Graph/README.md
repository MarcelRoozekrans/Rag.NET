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
// Every community in the result is connected in the subgraph its members induce.
var ranks = PageRank.Compute(graph);
```

`Leiden` is Traag/Waltman/van Eck's algorithm over modularity: Louvain's local moving and
aggregation with the paper's refinement phase between them, so **every returned community is
connected in the subgraph it induces** — the guarantee that paper exists to supply, measured at 0
disconnected communities in 3,359,331. The refinement is randomised (`Randomness`, θ, default 0.01)
but every draw comes from `RandomSeed`, so a fixed seed gives a fixed partition. Two documented
departures from the paper: local moving is a repeated sweep rather than its queue-driven
`MoveNodesFast`, and a node is never offered an empty community to move into. The type's own XML
remarks give where the guarantee comes from and what it does not promise.

## Full guide

- [GraphRAG](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/graphrag.md)
