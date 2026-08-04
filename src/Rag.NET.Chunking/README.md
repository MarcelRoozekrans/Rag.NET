# Rag.NET.Chunking

Advanced chunking strategies for Rag.NET: embedding-based semantic chunking, token-aware
windowing, late chunking, LLM proposition extraction, hierarchical merging and code-aware
chunking — pick per corpus what the core package's recursive default cannot express.

## Install

```bash
dotnet add package Rag.NET.Chunking
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the strategies register into.

## Setup

Inside your `AddRagNet(...)` builder callback — one strategy at a time; the last
registration wins:

```csharp
using Rag.NET.Chunking.Semantic;

rag.UseSemanticChunking();
```

## Example

Semantic chunking groups sentences by embedding similarity; token-aware chunking counts
real tokenizer tokens instead of characters:

```csharp
using Rag.NET.Chunking.Semantic;
using Rag.NET.Chunking.TokenAware;
using Rag.NET.Models.Options;

// Tuned semantic chunking:
rag.UseSemanticChunking(new SemanticChunkingOptions
{
    BreakpointPercentile = 0.25f,  // lower = more, smaller chunks
    MinChunkSize = 100,            // characters; undersized groups merge with neighbours
    MaxChunkSize = 1500,           // characters; oversized groups split at sentences
});

// Or: token-aware windows sized for your embedding model.
rag.UseTokenAwareChunking(o =>
{
    o.ModelName        = "gpt-4";  // selects the tokenizer encoding
    o.WindowSizeTokens = 256;
    o.OverlapTokens    = 32;
});
```

`UseHierarchicalMerging`, `UsePropositionChunking`, `UseLateChunking` (pair it with
`Rag.NET.Embeddings.Onnx` token embeddings) and `UseCodeChunking` follow the same shape.

## Full guide

- [Chunking](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/chunking.md)
