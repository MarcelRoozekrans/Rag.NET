# Rag.NET.Graph

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Graph.svg?logo=nuget&label=Rag.NET.Graph)](https://www.nuget.org/packages/Rag.NET.Graph)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Graph.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Graph)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
