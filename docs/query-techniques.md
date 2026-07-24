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

**Cost:** `HypothesisCount` LLM calls (default 3, at most 4 in parallel) plus `HypothesisCount` embedding inputs in one batch call per retrieval. Set `HypothesisCount = 1` to restore the single-LLM-call cost — note that the sampling temperature is still explicitly `HypothesisTemperature` (0.8) rather than the provider default. Dominated by LLM latency, not pipeline overhead.

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
    o.HypothesisCount = 3;         // hypotheses per query (>= 1); > 1 enables averaging
    o.HypothesisTemperature = 0.8f; // sampling temperature for hypothesis diversity
}));
```

The default `PromptTemplate` includes a `{query}` placeholder that is replaced with the user's query at runtime.

When `HypothesisCount > 1`, the generated hypotheses are embedded in a single batch and the L2-normalized mean of their embeddings is used for dense search (multi-hypothesis HyDE v2), smoothing out the variance of a single badly-angled hypothesis. Individual generation failures are tolerated as long as at least one hypothesis survives; if all fail, retrieval falls back to embedding the original query. See the [retrieval guide](guide/retrieval.md#hypothetical-document-embeddings-hyde) for details.

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

Costs multiply when combined: each of the `VariantCount + 1` query branches runs its own HyDE generation, for `(VariantCount + 1) x HypothesisCount` LLM calls per retrieval — **12 with both defaults** (`VariantCount = 3`, `HypothesisCount = 3`). Lower `HypothesisCount` (or `VariantCount`) if that call volume is a concern.
