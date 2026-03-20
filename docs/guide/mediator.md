---
id: mediator
title: Mediator Integration
sidebar_position: 10
---

# Mediator Integration

`Rag.NET.Mediator` wires the three core pipeline operations — ingest, retrieve, and delete — into [ZeroAlloc.Mediator](https://github.com/zeroalloc/mediator) request/handler pairs. This lets you dispatch pipeline calls through `IMediator` from any layer of your application without taking a direct dependency on `IRagPipeline`.

## Installation

```bash
dotnet add package Rag.NET.Mediator
```

`Rag.NET.Mediator` depends on both `Rag.NET` (for the pipeline interfaces and models) and `ZeroAlloc.Mediator 1.1.7`.

## Registration

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Mediator.DependencyInjection;

services.AddRagNet(rag => rag
    .UsePgVector(connectionString, vectorDimensions: 1536)
    .AddPdfParser()
    .UseMediator());
```

`.UseMediator()` registers `IMediator` (ZeroAlloc.Mediator) together with the three built-in handlers, inline with the rest of the pipeline configuration.

## Request types

### `IngestCommand`

```csharp
public sealed record IngestCommand(
    Stream Content,
    DocumentMetadata Metadata,
    IngestionOptions? Options = null,
    IProgress<IngestionProgress>? Progress = null)
    : IRequest<Result<IngestionResult, RagError>>;
```

```csharp
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
```

### `RetrieveQuery`

```csharp
public sealed record RetrieveQuery(string Query, RetrievalOptions? Options = null)
    : IRequest<Result<IReadOnlyList<SearchResult>, RagError>>;
```

```csharp
var result = await mediator.Send(new RetrieveQuery("key findings", new RetrievalOptions { TopK = 5 }));
if (result.IsSuccess)
    foreach (var r in result.Value)
        Console.WriteLine($"[{r.Score:F2}] {r.Chunk.Text}");
```

### `DeleteCommand`

```csharp
public sealed record DeleteCommand(DocumentId DocumentId)
    : IRequest<Result<Unit, RagError>>;
```

```csharp
var result = await mediator.Send(new DeleteCommand(new DocumentId("report-2024-q4")));
if (!result.IsSuccess)
    Console.WriteLine($"Delete failed: {result.Error}");
```

## When to use

The mediator integration is useful when:

- You want to keep application layers decoupled from `IRagPipeline` directly.
- You are already using ZeroAlloc.Mediator for CQRS-style dispatch in your application.
- You want to attach cross-cutting behaviors (logging, validation, authorization) via mediator pipeline behaviors.

For simple applications where `IRagPipeline` is injected directly, adding the mediator layer is unnecessary overhead.

> **Note:** `AskAsync` and `AskStreamingAsync` are not exposed as mediator requests. They are available directly on `IRagPipeline` because streaming responses (`IAsyncEnumerable`) do not fit the request/response mediator model well.
