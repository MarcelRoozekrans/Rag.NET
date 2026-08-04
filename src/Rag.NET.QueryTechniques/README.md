# Rag.NET.QueryTechniques

Query-side retrieval techniques for Rag.NET: HyDE (hypothetical document embeddings),
multi-query expansion, and contextual compression of retrieved chunks.

## Install

```bash
dotnet add package Rag.NET.QueryTechniques
```

The core `Rag.NET` package references this one, so most applications already have it;
install it directly only when composing a custom builder.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.QueryTechniques;

rag.UseHyde()
   .UseMultiQueryRetrieval();
```

Both are then switched per request via `RetrievalOptions.UseHyde` /
`RetrievalOptions.UseMultiQuery`.

## Example

Contextual compression trims each retrieved chunk to the sentences that matter for the
query before the prompt is built:

```csharp
using Rag.NET.QueryTechniques;

rag.UseContextualCompression(o =>
{
    o.KeepTopSentences = 3;  // default: extractive, no extra LLM call
});
```

Switch `o.Strategy` to the LLM-abstractive compressor when you want summarised rather
than extracted context.

## Full guide

- [Retrieval](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/retrieval.md)
