# Rag.NET

A modular RAG (Retrieval-Augmented Generation) pipeline library for .NET. Built on [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai-preview/) abstractions, it provides document ingestion, chunking, vector storage, retrieval, and chat with streaming support.

## Features

- **Document ingestion** - Parse, chunk, embed, and store documents in a single pipeline call
- **Multiple parsers** - Text, Markdown, PDF, HTML, Word, Excel, PowerPoint, CSV, JSON
- **Vector stores** - PostgreSQL/pgvector, Qdrant, Azure AI Search
- **Retrieval** - Semantic search with configurable top-K and minimum score filtering
- **Chat** - Ask questions with RAG context via `AskAsync` and streaming via `AskStreamingAsync`
- **Token-aware chunking** - Split by token count (not characters) to respect embedding model limits
- **Lost-in-the-Middle reordering** - Place highest-scoring chunks at context extremes for better LLM attention
- **Progress reporting** - Track ingestion stages in real time via `IProgress<IngestionProgress>`
- **DI-first** - Fluent builder API with `Microsoft.Extensions.DependencyInjection`
- **Extensible** - Implement `IDocumentParser`, `IVectorStore`, or `IChunkingStrategy` to plug in your own

## Packages

| Package | Description |
|---------|-------------|
| `Rag.NET` | Core pipeline, abstractions, text/markdown/CSV/JSON parsers, recursive chunking |
| `Rag.NET.PgVector` | PostgreSQL + pgvector vector store |
| `Rag.NET.Qdrant` | Qdrant vector store |
| `Rag.NET.AzureAISearch` | Azure AI Search vector store (with hybrid search) |
| `Rag.NET.Parsers.Pdf` | PDF document parser |
| `Rag.NET.Parsers.Html` | HTML document parser (AngleSharp) |
| `Rag.NET.Parsers.Word` | Word (.docx) document parser (OpenXml) |
| `Rag.NET.Parsers.Excel` | Excel (.xlsx) document parser (OpenXml) |
| `Rag.NET.Parsers.PowerPoint` | PowerPoint (.pptx) document parser (OpenXml) |

## Quick Start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.PgVector;
using Rag.NET.Parsers.Pdf;

var services = new ServiceCollection();

// Register your AI services (using Microsoft.Extensions.AI)
services.AddChatClient(/* your IChatClient */);
services.AddEmbeddingGenerator(/* your IEmbeddingGenerator<string, Embedding<float>> */);

// Configure Rag.NET
services.AddRagNet(rag => rag
    .UsePgVector(connectionString, vectorDimensions: 1536)
    .AddPdfParser()
    .AddHtmlParser()
    .AddWordParser());

var provider = services.BuildServiceProvider();
var pipeline = provider.GetRequiredService<IRagPipeline>();
```

### Ingest a Document

```csharp
var metadata = new DocumentMetadata
{
    DocumentId = "my-doc",
    FileName = "report.pdf",
    ContentType = "application/pdf",
};

using var stream = File.OpenRead("report.pdf");
var result = await pipeline.IngestAsync(stream, metadata);
Console.WriteLine($"Stored {result.ChunksStored} chunks");
```

### Ask a Question

```csharp
var response = await pipeline.AskAsync("What are the key findings?");
Console.WriteLine(response.Text);
```

### Streaming Responses

```csharp
await foreach (var update in pipeline.AskStreamingAsync("Summarize the report"))
{
    if (update.Sources is { Count: > 0 })
        Console.WriteLine($"[Found {update.Sources.Count} source(s)]");

    if (update.TextDelta is not null)
        Console.Write(update.TextDelta);
}
```

### Retrieve Without Chat

```csharp
var results = await pipeline.RetrieveAsync("key findings", new RetrievalOptions { TopK = 5 });
foreach (var result in results)
    Console.WriteLine($"[{result.Score:F2}] {result.Chunk.Text}");
```

## Vector Store Setup

### PostgreSQL + pgvector

```csharp
services.AddRagNet(rag => rag
    .UsePgVector(connectionString, vectorDimensions: 1536));
```

### Qdrant

```csharp
services.AddRagNet(rag => rag
    .UseQdrant("localhost", 6334, "my-collection", vectorDimensions: 1536));
```

### Azure AI Search

```csharp
services.AddRagNet(rag => rag
    .UseAzureAISearch(
        new Uri("https://my-search.search.windows.net"),
        "my-index",
        new AzureKeyCredential("api-key"),
        vectorDimensions: 1536));
```

## Configuration

### Chunking Options

```csharp
services.AddRagNet(rag => rag
    .UseChunkingStrategy<RecursiveChunkingStrategy>(options =>
    {
        options.MaxChunkSize = 512;
        options.Overlap = 50;
    })
    .UsePgVector(connectionString));
```

### RAG Options

```csharp
var response = await pipeline.AskAsync("question", new RagOptions
{
    TopK = 10,
    MinScore = 0.7,
    SystemPrompt = "You are a helpful assistant. Answer based on the provided context.",
    Temperature = 0.3f,
});
```

### Token-Aware Chunking

Prevents chunks from silently exceeding embedding model token limits by splitting on token boundaries instead of characters:

```csharp
services.AddRagNet(rag => rag
    .UseTokenAwareChunking("gpt-4")          // cl100k_base encoding (default)
    .UseChunkingStrategy<RecursiveChunkingStrategy>(options =>
    {
        options.MaxChunkSize = 512;           // tokens, not characters
        options.Overlap = 50;                 // tokens
    })
    .UsePgVector(connectionString));
```

### Lost-in-the-Middle Reordering

LLMs attend less to content in the middle of their context window ([Liu et al., 2023](https://arxiv.org/abs/2307.03172)). Enable outside-in reordering to place the most relevant chunks at the beginning and end:

```csharp
// On RetrieveAsync
var results = await pipeline.RetrieveAsync("query", new RetrievalOptions
{
    TopK = 10,
    UseLostInTheMiddleReordering = true,
});

// On AskAsync / AskStreamingAsync
var response = await pipeline.AskAsync("question", new RagOptions
{
    TopK = 10,
    UseLostInTheMiddleReordering = true,
});
```

### Progress Reporting

Track ingestion stages in real time via the standard `IProgress<T>` interface:

```csharp
var progress = new Progress<IngestionProgress>(p =>
    Console.WriteLine($"[{p.Stage}] {p.Message}"));

using var stream = File.OpenRead("report.pdf");
var result = await pipeline.IngestAsync(stream, metadata, progress: progress);
```

Four stages are reported: `Parsing` → `Chunking` → `Embedding` → `Storing`.

## Sample App

The [samples/Rag.NET.Sample](samples/Rag.NET.Sample) project is an interactive console app that demonstrates the full pipeline. It supports both **Ollama** (local) and **OpenAI** providers, uses Testcontainers to spin up a pgvector database automatically, and provides a Q&A loop with streaming responses.

**Prerequisites:** Docker (for Testcontainers PostgreSQL)

```bash
# Using Ollama (default)
dotnet run --project samples/Rag.NET.Sample

# Using OpenAI
OPENAI_API_KEY=sk-... RAG_PROVIDER=openai dotnet run --project samples/Rag.NET.Sample
```

## Benchmarks

Full results with methodology and analysis: [docs/benchmarks.md](docs/benchmarks.md)

Quick reference (i9-12900HK, .NET 10, 50-token chunks):

| Strategy | 50 KB input | Allocated |
|----------|------------:|----------:|
| Fixed | 29 us | 158 KB |
| Recursive | 94 us | 316 KB |
| TokenAware | 1,750 us | 389 KB |
| IngestAsync (pipeline, 50 KB) | 378 us | 629 KB |

`TokenAware` carries 20–60× chunking overhead from tiktoken encoding — negligible relative to embedding API latency in production.

## Requirements

- .NET 10+
- A compatible embedding provider (OpenAI, Ollama, Azure OpenAI, etc.)
- A vector store (PostgreSQL+pgvector, Qdrant, or Azure AI Search)

## License

[MIT](LICENSE)
