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
- **Redundancy filter** - Drop near-duplicate retrieved chunks by cosine similarity before passing to the LLM
- **Cross-encoder reranking** - Rescore search results with ONNX cross-encoder models for higher precision
- **Header-aware metadata** - Propagate Markdown/HTML heading hierarchy into chunk metadata as breadcrumbs
- **Progress reporting** - Track ingestion stages in real time via `IProgress<IngestionProgress>`
- **Evaluation** - Score answer quality with `Rag.NET.Evaluation` using embedding cosine similarity
- **DI-first** - Fluent builder API with `Microsoft.Extensions.DependencyInjection`
- **Extensible** - Implement `IDocumentParser`, `IVectorStore`, or `IChunkingStrategy` to plug in your own

## Packages

Not sure what to install? [Choosing packages](docs/guide/choosing-packages.md) walks
through the two or three decisions and what arrives transitively. A selection:

| Package | Description |
|---------|-------------|
| `Rag.NET` | Core pipeline, abstractions, text/markdown/CSV/JSON parsers, recursive chunking |
| `Rag.NET.VectorStores.PgVector` | PostgreSQL + pgvector vector store |
| `Rag.NET.VectorStores.Qdrant` | Qdrant vector store |
| `Rag.NET.VectorStores.AzureAISearch` | Azure AI Search vector store (with hybrid search) |
| `Rag.NET.VectorStores.Pinecone` | Pinecone vector store (dense and sparse) |
| `Rag.NET.VectorStores.Chroma` | Chroma vector store |
| `Rag.NET.VectorStores.Weaviate` | Weaviate vector store |
| `Rag.NET.Parsers.Pdf` | PDF document parser |
| `Rag.NET.Parsers.Pdf.AzureDocumentIntelligence` | Whole-document OCR for the PDF parser via Azure Document Intelligence (paid, per page) |
| `Rag.NET.Parsers.Html` | HTML document parser (AngleSharp) |
| `Rag.NET.Parsers.Office` | Word, Excel and PowerPoint document parsers (OpenXml) |
| `Rag.NET.Evaluation` | Answer quality evaluation via embedding cosine similarity |
| `Rag.NET.Reranking.Onnx` | ONNX Runtime cross-encoder reranking |
| `Rag.NET.Mediator` | ZeroAlloc.Mediator integration — dispatch ingest/retrieve/delete via `IMediator` |
| [Rag.NET.DataProviders.Confluence](src/Rag.NET.DataProviders.Confluence) | Confluence pages via REST API |
| [Rag.NET.DataProviders.Jira](src/Rag.NET.DataProviders.Jira) | Jira issues via REST API |
| [Rag.NET.DataProviders.Notion](src/Rag.NET.DataProviders.Notion) | Notion pages and blocks via REST API |
| [Rag.NET.DataProviders.Asana](src/Rag.NET.DataProviders.Asana) | Asana tasks and subtasks via REST API |
| [Rag.NET.DataProviders.Slack](src/Rag.NET.DataProviders.Slack) | Slack channel messages via REST API |
| [Rag.NET.DataProviders.Microsoft365](src/Rag.NET.DataProviders.Microsoft365) | SharePoint, OneDrive, Teams and Exchange mail via Microsoft Graph |
| [Rag.NET.DataProviders.Gmail](src/Rag.NET.DataProviders.Gmail) | Gmail messages via IMAP (MailKit) |
| [Rag.NET.DataProviders.GitLab](src/Rag.NET.DataProviders.GitLab) | GitLab repository files via NGitLab |
| [Rag.NET.DataProviders.Bitbucket](src/Rag.NET.DataProviders.Bitbucket) | Bitbucket repository files via REST API |
| [Rag.NET.DataProviders.Zendesk](src/Rag.NET.DataProviders.Zendesk) | Zendesk tickets and help center articles |
| [Rag.NET.DataProviders.Airtable](src/Rag.NET.DataProviders.Airtable) | Airtable rows and attachments |

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
    DocumentId = new DocumentId("my-doc"),
    FileName = "report.pdf",
    ContentType = "application/pdf",
};

using var stream = File.OpenRead("report.pdf");
var result = await pipeline.IngestAsync(stream, metadata);
if (result.IsSuccess)
    Console.WriteLine($"Stored {result.Value.ChunksStored} chunks");
else
    Console.WriteLine($"Ingestion failed: {result.Error}");
```

### Ask a Question

```csharp
var response = await pipeline.AskAsync("What are the key findings?");
Console.WriteLine(response.Answer);
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
if (results.IsSuccess)
    foreach (var r in results.Value)
        Console.WriteLine($"[{r.Score:F2}] {r.Chunk.Text}");
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

### Redundancy Filter

Drop near-duplicate retrieved chunks before sending context to the LLM. Uses a single re-embedding batch call and greedy cosine-similarity filtering:

```csharp
var results = await pipeline.RetrieveAsync("query", new RetrievalOptions
{
    TopK = 10,
    UseRedundancyFilter = true,
    RedundancyThreshold = 0.95f, // default — drop chunks with >95% cosine similarity to an already-accepted chunk
});

// Also available on AskAsync / AskStreamingAsync via RagOptions
var response = await pipeline.AskAsync("question", new RagOptions
{
    TopK = 10,
    UseRedundancyFilter = true,
    RedundancyThreshold = 0.95f,
});
```

### Header-Aware Metadata

When ingesting Markdown or HTML documents, heading hierarchy is automatically propagated into `TextChunk.Metadata` as searchable breadcrumbs:

```csharp
// After ingest, each chunk from a section under "# Chapter 1 > ## Section 2" will carry:
chunk.Metadata["heading"]            // "Section 2"
chunk.Metadata["heading_level"]      // "2"
chunk.Metadata["heading_breadcrumb"] // "Chapter 1 > Section 2"

// Filter retrieval to a specific section:
var results = await pipeline.RetrieveAsync("query", new RetrievalOptions
{
    MetadataFilter = new Dictionary<string, MetadataValue> { ["heading_breadcrumb"] = "Chapter 1 > Section 2" }
});
```

A heading with no content of its own — one immediately followed by the next heading — produces **no
chunk**. Parsers prepend the heading to the section body, so such a section's text is just the
heading, and indexing it yielded entries like `"text": "Section 2"`: they scored on heading-shaped
queries, took up a retrieval slot, and gave the model nothing to answer with
([#366](https://github.com/MarcelRoozekrans/Rag.NET/issues/366)). The heading is still recorded in
the breadcrumb, so chunks nested beneath it keep the full `Chapter 1 > Section 2 > Subsection` path.

### Evaluation

Use `Rag.NET.Evaluation` to score answer quality by cosine similarity between embedded predicted and reference answers — no LLM call required:

```csharp
using Rag.NET.Evaluation;

var evaluator = new EmbeddingDistanceEvaluator(embeddingGenerator);

var result = await evaluator.EvaluateAsync([
    new EvaluationSample(
        Question: "What is RAG?",
        PredictedAnswer: response.Answer,
        ReferenceAnswer: "Retrieval-Augmented Generation combines search with LLMs."),
]);

Console.WriteLine($"Score: {result.MeanScore:F2}"); // e.g. 0.91
```

Score interpretation: 1.0 = semantically identical, 0.0 = completely unrelated. Scores ≥ 0.85 typically indicate acceptable answer quality.

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
