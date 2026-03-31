---
id: oss-libraries
title: Open-Source Libraries
sidebar_position: 3
---

# Open-Source Libraries

Rag.NET is built on a small set of open-source libraries. This page lists each dependency, where it is used, and why it was chosen over the alternatives.

---

## ZeroAlloc ecosystem

All ZeroAlloc packages are authored and maintained by the same team as Rag.NET. They are zero-allocation, source-generator-driven libraries that keep the core pipeline free of reflection and boxing at runtime.

### ZeroAlloc.Inject

**Used in:** `Rag.NET` (core)

Property injection for singleton pipeline behaviors. Annotate a property with `[Inject]` and the source generator wires it to the DI container at build time — no reflection, no constructor parameter lists that grow with every new dependency.

```csharp
[Singleton]
public sealed class EmbeddingBehavior : IIngestionBehavior
{
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> EmbeddingGenerator { get; set; } = null!;
    [Inject(Required = false)] public ILogger<EmbeddingBehavior>? Logger { get; set; }
    ...
}
```

**Why not constructor injection?** Behavior classes are singletons assembled by a pipeline builder. Optional services (loggers, feature-specific clients) would require nullable constructor parameters for every behavior. Property injection with `Required = false` expresses optionality at the declaration site without cluttering constructors.

---

### ZeroAlloc.Pipeline

**Used in:** `Rag.NET` (core)

Source-generates the behavior-chain plumbing for `IIngestionBehavior` and `IRetrievalBehavior`. Each behavior receives a `next` delegate and decides whether to call it, short-circuit, or modify the context before passing it on — identical to ASP.NET Core middleware.

**Why not a hand-rolled chain?** The generator emits the `PipelineIngestor` and `PipelineRetriever` coordinator classes from a simple ordered list of behavior types. Adding or reordering a behavior requires changing one line in the builder, not updating wiring code.

---

### ZeroAlloc.Results

**Used in:** `Rag.NET` (core), `Rag.NET.Mediator`

`Result<T>` (success/failure) and `Result<T, E>` (typed error) discriminated unions. Used for all internal LLM calls, vector store operations, and the public `IRagPipeline` API.

```csharp
var result = await pipeline.IngestAsync(stream, metadata);
if (result.IsSuccess)
    Console.WriteLine($"Stored {result.Value.ChunksStored} chunks");
else
    Console.WriteLine($"Failed: {result.Error}");
```

**Why not exceptions?** Expected failures (LLM parse errors, malformed JSON, ingestion validation) should not unwind call stacks. `Result<T>` forces callers to handle both paths at the call site without try/catch blocks.

---

### ZeroAlloc.Specification

**Used in:** `Rag.NET` (core)

`ISpecification<T>` interface and source-generated implementations. Used for post-retrieval filtering — `HasTagSpec`, `DocumentIdSpec`, and `MinScoreSpec` are composed at runtime from `RetrievalOptions.Filter` and applied in `FilterBehavior`.

```csharp
var filter = new HasTagSpec("language", "csharp")
    .And(new MinScoreSpec(0.7));

var results = await pipeline.RetrieveAsync("async patterns", new RetrievalOptions
{
    Filter = filter
});
```

**Why not LINQ predicates?** `ISpecification<T>` exposes both `IsSatisfiedBy(T)` for in-process filtering and `ToExpression()` for potential push-down to a query engine. Specifications are also composable, testable in isolation, and named — `HasTagSpec("language", "csharp")` is more readable than an anonymous lambda.

---

### ZeroAlloc.Validation

**Used in:** `Rag.NET` (core)

Declarative validation attributes for options and model classes. Applied to `DocumentMetadata`, `ChunkingOptions`, `RetrievalOptions`, and the ingestion pipeline to surface configuration errors early with descriptive messages.

**Why not `FluentValidation`?** ZeroAlloc.Validation is a source generator with zero runtime allocations. For a pipeline library where options objects are constructed on every call, avoiding heap pressure on the validation path matters.

---

### ZeroAlloc.Mediator

**Used in:** `Rag.NET.Mediator`

Source-generated CQRS mediator. `IngestCommand`, `RetrieveQuery`, and `DeleteCommand` are dispatched through `IMediator` without reflection-based handler discovery.

**Why not MediatR?** MediatR uses reflection at dispatch time. ZeroAlloc.Mediator generates direct handler dispatch at build time — no dictionary lookups, no boxing. For applications that already depend on ZeroAlloc.Mediator for CQRS, this package adds Rag.NET pipeline operations as first-class mediator requests with no additional runtime overhead.

---

### ZeroAlloc.ValueObjects

**Used in:** `Rag.NET` (core) — `DocumentId`

Source-generates `Equals`, `GetHashCode`, `==`, `!=`, and `ToString` for `partial class` and `partial struct` types annotated with `[ValueObject]`. `[EqualityMember]` opts specific properties in when the default (all public properties) is not appropriate.

```csharp
[ValueObject]
public sealed partial class DocumentId
{
    [EqualityMember]
    public string Value => _value;   // backing field stays private
    ...
}
```

**Why not `record`?** `DocumentId` needs a custom JSON converter, custom implicit/explicit operators, and a `ToString()` that returns the bare value (not `"DocumentId { Value = ... }"`). Using `record` would add `EqualityContract`, `Deconstruct`, and `with` to the public API — none of which are appropriate for a typed identifier. `[ValueObject]` generates only what is needed.

---

## Third-party libraries

### AngleSharp

**Used in:** `Rag.NET.Parsers.Html`

HTML parsing and DOM traversal for `HtmlDocumentParser`. Produces a typed DOM from raw HTML so the parser can walk headings, extract text, strip navigation/footer elements, and convert anchor hrefs to inline text.

**Why not `HtmlAgilityPack`?** AngleSharp implements the WHATWG HTML5 parsing spec (the same spec browsers use). It handles malformed HTML consistently and provides a CSS selector API (`QuerySelectorAll`) that makes structural traversal straightforward.

---

### DocumentFormat.OpenXml

**Used in:** `Rag.NET.Parsers.Word`, `Rag.NET.Parsers.Excel`, `Rag.NET.Parsers.PowerPoint`

Microsoft's official Open XML SDK for reading `.docx`, `.xlsx`, and `.pptx` files without requiring Office to be installed. Used to extract text, heading levels, and page/sheet structure from Office documents.

---

### PdfPig

**Used in:** `Rag.NET.Parsers.Pdf`

Pure .NET PDF text extraction. Reads PDF content streams to extract text with position information, enabling page-number tracking in `DocumentSection`.

**Why not iTextSharp / PdfSharp?** PdfPig is MIT-licensed, dependency-free, and actively maintained. It produces reliable text extraction with word/line bounding boxes that can be used for layout-aware chunking.

---

### Npgsql + Pgvector

**Used in:** `Rag.NET.VectorStores.PgVector`

`Npgsql` is the standard .NET PostgreSQL driver. `Pgvector` adds pgvector extension support — the `Vector` type, distance operator bindings, and index hints. Together they power `PgVectorStore`.

---

### Qdrant.Client

**Used in:** `Rag.NET.VectorStores.Qdrant`

Official gRPC client for Qdrant vector database. Used by `QdrantVectorStore` for upsert, search, and delete operations against a Qdrant collection.

---

### Azure.Search.Documents

**Used in:** `Rag.NET.VectorStores.AzureAISearch`

Official Azure SDK client for Azure AI Search (formerly Cognitive Search). Used by `AzureAISearchVectorStore` for index management, document upload, vector search, and native hybrid search (BM25 + vector via `SemanticSearch`).

---

### Octokit

**Used in:** `Rag.NET.DataProviders.GitHub`

Official GitHub API client for .NET. Used by the GitHub data provider to list repository contents and fetch file blobs for ingestion.

---

### ModelContextProtocol

**Used in:** `Rag.NET.Mcp`

Official .NET SDK for the Model Context Protocol (MCP). Used to expose Rag.NET ingest, retrieve, and delete operations as MCP tools that can be called from any MCP-compatible client (Claude Desktop, VS Code Copilot, etc.).
