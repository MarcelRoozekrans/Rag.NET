# Rag.NET.Mediator

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Mediator.svg?logo=nuget&label=Rag.NET.Mediator)](https://www.nuget.org/packages/Rag.NET.Mediator)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Mediator.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Mediator)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

ZeroAlloc.Mediator integration for Rag.NET: exposes the pipeline as `IngestCommand`,
`RetrieveQuery` and `DeleteCommand` requests so applications built on the mediator pattern
can drive RAG without holding an `IRagPipeline` reference.

## Install

```bash
dotnet add package Rag.NET.Mediator
```

## Setup

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Mediator.DependencyInjection;

services.AddRagNet(rag => rag.UseMediator());
```

`UseMediator()` registers the three request handlers; the requests resolve the same
pipeline your other registrations configured.

## Example

```csharp
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;

using var stream = File.OpenRead("report.pdf");
var metadata = new DocumentMetadata
{
    DocumentId  = new DocumentId("report-2024-q4"),
    FileName    = "report.pdf",
    ContentType = "application/pdf",
};

var result = await mediator.Send(new IngestCommand(stream, metadata));
if (result.IsSuccess)
    Console.WriteLine($"Stored {result.Value.ChunksStored} chunks");

var retrieved = await mediator.Send(new RetrieveQuery("key findings", new RetrievalOptions { TopK = 5 }));
if (retrieved.IsSuccess)
    foreach (var r in retrieved.Value)
        Console.WriteLine($"[{r.Score:F2}] {r.Chunk.Text}");

await mediator.Send(new DeleteCommand(new DocumentId("report-2024-q4")));
```

## Full guide

- [Mediator](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mediator.md)
