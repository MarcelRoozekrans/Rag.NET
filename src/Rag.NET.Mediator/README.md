# Rag.NET.Mediator

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
