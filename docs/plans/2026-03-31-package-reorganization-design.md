# Package Reorganization Design

**Date:** 2026-03-31
**Status:** Approved

## Overview

Full library-wide package reorganization to establish a clean namespace story, introduce `Rag.NET.Abstractions` as the dependency-free contract layer, rename vector store packages to `Rag.NET.VectorStores.*`, and extract chunking strategies, answer engines, query techniques, and memory into their own packages. Breaking change — pre-1.0.

## Motivation

- Discoverability: `Rag.NET.Chunking.*`, `Rag.NET.VectorStores.*` are self-describing
- Composability: extension packages reference only `Rag.NET.Abstractions`, not the full core
- Reusability: individual chunkers/engines usable in solutions that don't need the full RAG stack
- Follows the `Microsoft.Extensions.*` / `Microsoft.Extensions.*.Abstractions` convention

## Package Map

### Foundation

| Package | Contents |
|---|---|
| `Rag.NET.Abstractions` | All 20 interfaces (`IChunkingStrategy`, `IVectorStore`, `IReranker`, etc.), all Models, all Options classes. No implementations. No heavy dependencies. |
| `Rag.NET` | References Abstractions. Bare-minimum defaults: `RecursiveChunkingStrategy`, `FixedSizeChunkingStrategy`, `ChatAnswerEngine`, `TextDocumentParser`, `MarkdownDocumentParser`. Plus: pipeline coordination, DI builder, BM25, SQLite stores, PostRetrieval utilities (MMR, RRF, LostInMiddle, RedundancyFilter). |

### Chunking

| Package | Contents | Key dependency |
|---|---|---|
| `Rag.NET.Chunking` | `HierarchicalMergerChunkingStrategy`, `CodeChunkingStrategy` | None beyond core |
| `Rag.NET.Chunking.Semantic` | `SemanticChunkingStrategy` | MEAI `IEmbeddingGenerator` |
| `Rag.NET.Chunking.TokenAware` | `TokenAwareChunkingStrategy` | `Microsoft.ML.Tokenizers` |
| `Rag.NET.Chunking.CSharp` | *(new)* Roslyn-based C# semantic chunker | `Microsoft.CodeAnalysis.CSharp` |
| `Rag.NET.Chunking.Vision` | *(new)* Image/video description via vision LLM | vision `IChatClient` |
| `Rag.NET.Chunking.Templates` | *(new)* Domain templates: academic, legal, Q&A, books, email, resume | Per-domain |

### Vector Stores (renamed)

| New package | Old package |
|---|---|
| `Rag.NET.VectorStores.PgVector` | `Rag.NET.PgVector` |
| `Rag.NET.VectorStores.Qdrant` | `Rag.NET.Qdrant` |
| `Rag.NET.VectorStores.AzureAISearch` | `Rag.NET.AzureAISearch` |

### Answer Engines

| Package | Contents |
|---|---|
| `Rag.NET.AnswerEngines` | `MapReduceAnswerEngine`, `RefineAnswerEngine`, `DispatchingAnswerEngine` |

`ChatAnswerEngine` stays in `Rag.NET` core as the zero-config default.

### Query Techniques

| Package | Contents |
|---|---|
| `Rag.NET.QueryTechniques` | `LlmHypotheticalDocumentGenerator` (HyDE), `LlmQueryExpander` (MultiQuery), `SelfQueryBehavior` |

### Memory

| Package | Contents |
|---|---|
| `Rag.NET.Memory` | `PersistentConversationMemory` (SQLite-backed multi-turn memory) |

`InMemoryConversationMemory` (if exists) stays in core.

### Unchanged

All of the following keep their current package IDs and structure. Internally they will be updated to reference `Rag.NET.Abstractions` instead of `Rag.NET`:

- `Rag.NET.Parsers.*` (Pdf, Html, Word, Audio, Excel, PowerPoint)
- `Rag.NET.DataProviders.*` (all 15+ providers)
- `Rag.NET.Reranking.Onnx`, `Rag.NET.Reranking.Cohere`
- `Rag.NET.Raptor`, `Rag.NET.Graph`, `Rag.NET.GraphRag` *(reference `Rag.NET` core, not just Abstractions — need pipeline utilities)*
- `Rag.NET.Api`, `Rag.NET.Api.Client`, `Rag.NET.Api.Grpc`, `Rag.NET.Api.Grpc.Client`
- `Rag.NET.Mcp`, `Rag.NET.Mcp.Tool`
- `Rag.NET.Mediator`, `Rag.NET.Evaluation`

## Dependency Graph

```
Rag.NET.Abstractions          (interfaces + models only)
        │
        ├── Rag.NET            (pipeline, DI builder, defaults, utilities)
        │       │
        │       └── Rag.NET.Raptor / Graph / GraphRag  (need pipeline utilities)
        │
        ├── Rag.NET.Chunking
        ├── Rag.NET.Chunking.Semantic
        ├── Rag.NET.Chunking.TokenAware
        ├── Rag.NET.Chunking.CSharp
        ├── Rag.NET.Chunking.Vision
        ├── Rag.NET.Chunking.Templates
        │
        ├── Rag.NET.VectorStores.PgVector
        ├── Rag.NET.VectorStores.Qdrant
        ├── Rag.NET.VectorStores.AzureAISearch
        │
        ├── Rag.NET.AnswerEngines
        ├── Rag.NET.QueryTechniques
        ├── Rag.NET.Memory
        │
        ├── Rag.NET.Reranking.Onnx
        ├── Rag.NET.Reranking.Cohere
        │
        ├── Rag.NET.Parsers.*
        └── Rag.NET.DataProviders.*
```

**Key rule:** All extension packages reference `Rag.NET.Abstractions` directly. Only `Rag.NET.Raptor`, `Rag.NET.Graph`, and `Rag.NET.GraphRag` reference `Rag.NET` core (they use `EmbeddingMath`, behavior infrastructure, and SQLite utilities).

## Breaking Changes

### Package renames (update `<PackageReference>` only)

| Old | New |
|---|---|
| `Rag.NET.PgVector` | `Rag.NET.VectorStores.PgVector` |
| `Rag.NET.Qdrant` | `Rag.NET.VectorStores.Qdrant` |
| `Rag.NET.AzureAISearch` | `Rag.NET.VectorStores.AzureAISearch` |

### Classes moving out of `Rag.NET` core

Users of the following must install the new package:

| Class | New package |
|---|---|
| `SemanticChunkingStrategy` | `Rag.NET.Chunking.Semantic` |
| `TokenAwareChunkingStrategy` | `Rag.NET.Chunking.TokenAware` |
| `HierarchicalMergerChunkingStrategy` | `Rag.NET.Chunking` |
| `CodeChunkingStrategy` | `Rag.NET.Chunking` |
| `MapReduceAnswerEngine` | `Rag.NET.AnswerEngines` |
| `RefineAnswerEngine` | `Rag.NET.AnswerEngines` |
| `DispatchingAnswerEngine` | `Rag.NET.AnswerEngines` |
| `LlmHypotheticalDocumentGenerator` | `Rag.NET.QueryTechniques` |
| `LlmQueryExpander` | `Rag.NET.QueryTechniques` |
| `SelfQueryBehavior` | `Rag.NET.QueryTechniques` |
| `PersistentConversationMemory` | `Rag.NET.Memory` |

### Namespace changes

All interfaces and models move to the `Rag.NET.Abstractions` namespace. Users update:
```csharp
// Before
using Rag.NET.Abstractions;   // was already this in most cases
using Rag.NET.Models;

// After
using Rag.NET.Abstractions;   // covers both — no change for most users
```

### What does NOT break

- `Rag.NET` package still exists and is the primary install
- `RecursiveChunkingStrategy`, `FixedSizeChunkingStrategy`, `ChatAnswerEngine`, `TextDocumentParser`, `MarkdownDocumentParser` stay in core
- All DataProviders and Parsers — package IDs unchanged
- All Reranking packages — package IDs unchanged

## Implementation Approach

Execute in phases, green build required after each:

**Phase 1: Create `Rag.NET.Abstractions`**
- New project with all interfaces + models
- Update all existing packages to reference `Rag.NET.Abstractions`
- `Rag.NET` core re-exports via project reference
- Full solution build + all tests green

**Phase 2: Extract Chunking**
- Create `Rag.NET.Chunking`, `Rag.NET.Chunking.Semantic`, `Rag.NET.Chunking.TokenAware`
- Move classes, update DI extensions, rename test projects
- Remove from `Rag.NET` core
- Full solution build + all tests green

**Phase 3: Rename Vector Stores**
- Rename folders, csproj PackageId, solution references
- Full solution build + all tests green

**Phase 4: Extract Answer Engines + Query Techniques + Memory**
- Create `Rag.NET.AnswerEngines`, `Rag.NET.QueryTechniques`, `Rag.NET.Memory`
- Move classes, update DI extensions, rename test projects
- Full solution build + all tests green

**Phase 5: Update all downstream packages**
- All Parsers, DataProviders, Reranking packages switch project reference from `Rag.NET` → `Rag.NET.Abstractions`
- Full solution build + all tests green

## Solution Structure After

```
/src/
  Rag.NET.Abstractions/
  Rag.NET/
  Rag.NET.Chunking/
  Rag.NET.Chunking.Semantic/
  Rag.NET.Chunking.TokenAware/
  Rag.NET.Chunking.CSharp/          ← new
  Rag.NET.Chunking.Vision/          ← new
  Rag.NET.Chunking.Templates/       ← new
  Rag.NET.VectorStores.PgVector/    ← renamed
  Rag.NET.VectorStores.Qdrant/      ← renamed
  Rag.NET.VectorStores.AzureAISearch/ ← renamed
  Rag.NET.AnswerEngines/
  Rag.NET.QueryTechniques/
  Rag.NET.Memory/
  ... (all others unchanged)

/tests/
  Rag.NET.Abstractions.Tests/       ← if needed
  Rag.NET.Chunking.Tests/
  Rag.NET.Chunking.Semantic.Tests/
  Rag.NET.Chunking.TokenAware.Tests/
  Rag.NET.VectorStores.PgVector.Tests/  ← renamed
  Rag.NET.VectorStores.Qdrant.Tests/    ← renamed
  Rag.NET.VectorStores.AzureAISearch.Tests/ ← renamed
  Rag.NET.AnswerEngines.Tests/
  Rag.NET.QueryTechniques.Tests/
  Rag.NET.Memory.Tests/
  ... (all others unchanged)
```
