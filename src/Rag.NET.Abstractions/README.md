# Rag.NET.Abstractions

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Abstractions.svg?logo=nuget&label=Rag.NET.Abstractions)](https://www.nuget.org/packages/Rag.NET.Abstractions)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Abstractions.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Abstractions)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

The contract layer of Rag.NET: the `IRagPipeline`, `IVectorStore`, `IDocumentParser` and
`IChunkingStrategy` interfaces plus the shared models (`DocumentMetadata`, `TextChunk`,
`SearchResult`, `RagError`) that every other Rag.NET package builds against.

## Install

```bash
dotnet add package Rag.NET.Abstractions
```

Reference this package directly when you implement your own parser, chunking strategy,
vector store or reranker in a library that should not drag in the full pipeline — the
`Rag.NET` core package already includes it transitively.

## Setup

There is nothing to register: this package only declares the shapes. A custom parser, for
example, is one interface away:

```csharp
using Rag.NET.Abstractions;

public sealed class CsvDocumentParser : IDocumentParser
{
    public bool CanParse(string contentType) => contentType == "text/csv";

    // ParseAsync turns the stream into the plain text the chunking stage consumes.
}
```

## Example

The models carry a document through the pipeline. `DocumentMetadata` identifies the
document, and its `Tags` travel to the vector store for metadata filtering later:

```csharp
using Rag.NET.Models;

var metadata = new DocumentMetadata
{
    DocumentId  = new DocumentId("policy-hr-001"),
    FileName    = "hr-policy.docx",
    ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    Tags = new Dictionary<string, MetadataValue>
    {
        ["category"] = "hr",
        ["version"]  = "2024-01",
    },
};
```

## Full guide

- [Extending Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/extending.md)
- [Architecture](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/architecture.md)
