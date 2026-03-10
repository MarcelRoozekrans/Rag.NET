# Rag.NET Design

## Overview

A modular .NET 10 library for building RAG (Retrieval-Augmented Generation) pipelines. Core abstractions in one package, implementations in separate packages. Uses `Microsoft.Extensions.AI` for provider-agnostic embedding/LLM integration. Everything wired through standard `Microsoft.Extensions.DependencyInjection`.

## Decisions

- **Target:** .NET 10
- **AI abstractions:** `Microsoft.Extensions.AI` (`IEmbeddingGenerator`, `IChatClient`)
- **Vector store (v1):** pgvector (PostgreSQL) — others later via pluggable `IVectorStore`
- **Document parsing:** Pluggable `IDocumentParser`, text + Markdown built-in, PDF as optional package
- **Chunking:** Pluggable `IChunkingStrategy`, fixed-size + recursive built-in
- **Pipeline scope:** Full pipeline (ingest + retrieve + generate), generate is optional
- **Package structure:** Split NuGet packages, not monolithic

## Package Structure

| Package | Purpose |
|---|---|
| `Rag.NET` | Core abstractions, interfaces, built-in chunking strategies, text/Markdown parser, pipeline orchestration |
| `Rag.NET.PgVector` | pgvector `IVectorStore` implementation |
| `Rag.NET.Parsers.Pdf` | Optional PDF `IDocumentParser` (via UglyToad.PdfPig or similar) |

## Core Abstractions

```csharp
IDocumentParser
  ParseAsync(Stream, DocumentMetadata) -> IAsyncEnumerable<DocumentSection>

IChunkingStrategy
  ChunkAsync(DocumentSection, ChunkingOptions) -> IAsyncEnumerable<TextChunk>

IVectorStore
  StoreAsync(IEnumerable<EmbeddedChunk>) -> Task
  SearchAsync(ReadOnlyMemory<float> queryEmbedding, SearchOptions) -> Task<IReadOnlyList<SearchResult>>
  DeleteByDocumentIdAsync(string documentId) -> Task

IRagPipeline
  IngestAsync(Stream document, DocumentMetadata metadata) -> Task<IngestionResult>
  RetrieveAsync(string query, RetrievalOptions?) -> Task<IReadOnlyList<SearchResult>>
  AskAsync(string query, RagOptions?) -> Task<RagResponse>  // optional generate step
```

## Key Models

- `DocumentSection` — parsed section with text + structural metadata (heading level, page number)
- `TextChunk` — chunk of text with position info, parent document reference
- `EmbeddedChunk` — `TextChunk` + `ReadOnlyMemory<float>` embedding vector
- `SearchResult` — chunk + similarity score + metadata
- `RagResponse` — LLM answer + source chunks used (for citations)
- `ChunkingOptions` — max chunk size, overlap, separator preferences
- `SearchOptions` — top-k, minimum score threshold, metadata filters

## Data Flow

### Ingestion

```
Stream -> IDocumentParser -> DocumentSection[] -> IChunkingStrategy -> TextChunk[]
  -> IEmbeddingGenerator -> EmbeddedChunk[] -> IVectorStore
```

### Query (retrieve only)

```
string query -> IEmbeddingGenerator -> embedding -> IVectorStore.SearchAsync -> SearchResult[]
```

### Ask (retrieve + generate)

```
SearchResult[] -> prompt template + user query -> IChatClient -> RagResponse (answer + sources)
```

## DI Registration

```csharp
services.AddRagNet(rag => rag
    .UseChunkingStrategy<RecursiveChunkingStrategy>(options => {
        options.MaxChunkSize = 512;
        options.Overlap = 50;
    })
    .UsePgVector(connectionString)
);

// Users register their own embedding/chat providers via MS.Extensions.AI
services.AddEmbeddingGenerator(...)
services.AddChatClient(...)  // optional, only needed for AskAsync
```

Rag.NET resolves `IEmbeddingGenerator` and `IChatClient` from DI — it never creates them.

## Solution Structure

```
Rag.NET/
  Rag.NET.slnx
  Directory.Build.props
  src/
    Rag.NET/
      Abstractions/
        IDocumentParser.cs
        IChunkingStrategy.cs
        IVectorStore.cs
        IRagPipeline.cs
      Models/
        DocumentSection.cs
        TextChunk.cs
        EmbeddedChunk.cs
        SearchResult.cs
        RagResponse.cs
        Options/
          ChunkingOptions.cs
          SearchOptions.cs
          RagOptions.cs
      Chunking/
        FixedSizeChunkingStrategy.cs
        RecursiveChunkingStrategy.cs
      Parsers/
        TextDocumentParser.cs
        MarkdownDocumentParser.cs
      Pipeline/
        RagPipeline.cs
      DependencyInjection/
        RagBuilder.cs
        ServiceCollectionExtensions.cs
    Rag.NET.PgVector/
      PgVectorStore.cs
      PgVectorBuilderExtensions.cs
    Rag.NET.Parsers.Pdf/
      PdfDocumentParser.cs
      PdfParserBuilderExtensions.cs
  samples/
    Rag.NET.Sample/
  tests/
    Rag.NET.Tests/
    Rag.NET.PgVector.Tests/
```
