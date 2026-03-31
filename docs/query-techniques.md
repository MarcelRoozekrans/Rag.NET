---
id: query-techniques
title: Query Techniques
sidebar_label: Query Techniques
sidebar_position: 7
---

# Query Techniques

`Rag.NET.QueryTechniques` provides two retrieval-expansion techniques that improve recall by transforming queries before embedding them.

Install: `dotnet add package Rag.NET.QueryTechniques`

## HyDE — Hypothetical Document Embedding

Generates a hypothetical document that _would_ answer the query using the LLM, then embeds that document instead of the raw query. This bridges the vocabulary gap between a short question and a long document passage.

**When to use:** Short queries against long technical documents. Expect improved recall in asymmetric retrieval.

**Cost:** One extra LLM call per retrieval. Dominated by LLM latency, not pipeline overhead.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseHyde());
```

**Per-call opt-out:**
```csharp
var result = await pipeline.RetrieveAsync(query, new RetrievalOptions { UseHyde = false });
```

**Options:**
```csharp
services.AddRagNet(rag => rag.UseHyde(o =>
{
    o.PromptTemplate = "Generate a passage from a technical manual that answers: {query}";
}));
```

The default `PromptTemplate` includes a `{query}` placeholder that is replaced with the user's query at runtime.

## MultiQuery — LLM Query Expander

Expands the query into `VariantCount` alternative phrasings using the LLM, fans out to the vector store in parallel for each variant, then merges and deduplicates results.

**When to use:** Queries with ambiguous wording, or when users phrase things differently than the documents do.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseMultiQueryRetrieval());
```

**Per-call opt-out:**
```csharp
var result = await pipeline.RetrieveAsync(query, new RetrievalOptions { UseMultiQuery = false });
```

**Options:**
```csharp
services.AddRagNet(rag => rag.UseMultiQueryRetrieval(o => o.VariantCount = 5));
```

## Using Both Together

HyDE and MultiQuery can be combined:

```csharp
services.AddRagNet(rag => rag
    .UseMultiQueryRetrieval()
    .UseHyde());
```
