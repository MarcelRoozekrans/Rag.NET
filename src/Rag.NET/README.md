# Rag.NET

The core Retrieval-Augmented Generation pipeline for .NET: `AddRagNet()` wires ingestion
(parse → chunk → embed → store) and retrieval (query → search → rerank → answer) into any
`IServiceCollection`, exposed to your code as one `IRagPipeline`.

## Install

```bash
dotnet add package Rag.NET
```

## Setup

Rag.NET builds on Microsoft.Extensions.AI: register any `IChatClient` and
`IEmbeddingGenerator<string, Embedding<float>>` (OpenAI, Azure OpenAI, Ollama, …) before
calling `AddRagNet()`. Out of the box this package parses text and Markdown, chunks
recursively, and can run entirely in memory — perfect for a first spike before you attach a
real vector store package such as `Rag.NET.VectorStores.PgVector`.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Storage;

// chatClient / embeddingGenerator: your Microsoft.Extensions.AI implementations.
services.AddSingleton(chatClient);
services.AddSingleton(embeddingGenerator);

services.AddSingleton<IVectorStore>(new InMemoryVectorStore()); // nothing persisted
services.AddRagNet();
```

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models;

var pipeline = provider.GetRequiredService<IRagPipeline>();

using var stream = File.OpenRead("notes.md");
var ingested = await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId  = new DocumentId("notes"),
    FileName    = "notes.md",
    ContentType = "text/markdown",
});

var response = await pipeline.AskAsync("What do the notes say about pricing?");
Console.WriteLine(response.Answer);

foreach (var source in response.Sources)
    Console.WriteLine($"[{source.Score:F2}] {source.Chunk.Text}");
```

`RetrieveAsync` returns raw ranked chunks, `AskStreamingAsync` streams the answer
token-by-token, and the `RagBuilder` passed to `AddRagNet(rag => …)` activates self-query,
parent-document retrieval, MMR, corrective RAG, conversation memory and more.

## Full guide

- [Architecture](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/architecture.md)
- [Retrieval options](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/retrieval.md)
- [Ingestion](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
