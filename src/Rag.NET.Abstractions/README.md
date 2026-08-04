# Rag.NET.Abstractions

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
    Tags = new Dictionary<string, string>
    {
        ["category"] = "hr",
        ["version"]  = "2024-01",
    },
};
```

## Full guide

- [Extending Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/extending.md)
- [Architecture](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/architecture.md)
