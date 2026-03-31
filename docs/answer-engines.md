---
id: answer-engines
title: Answer Engines
sidebar_label: Answer Engines
sidebar_position: 6
---

# Answer Engines

Rag.NET ships with four answer engines. All implement `IAnswerEngine` and produce a string answer from the query and the retrieved source chunks.

## ChatAnswerEngine (default)

Included in `Rag.NET` core, registered automatically. Builds a single prompt from all source chunks and sends one LLM call.

**Best for:** Queries with a small number of source chunks and typical question-answering.

No registration needed — it is the default when `AddRagNet()` is called.

## MapReduceAnswerEngine

Install: `dotnet add package Rag.NET.AnswerEngines`

Runs one LLM call per source chunk in parallel (map). Filters "not found" responses. Combines surviving partial answers in a single reduce call.

**Best for:** Large document sets where each chunk may individually contain part of the answer. More LLM calls than Chat, but scales with number of chunks.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseMapReduceAnswerEngine());
```

## RefineAnswerEngine

Install: `dotnet add package Rag.NET.AnswerEngines`

Generates an initial answer from the first source chunk, then iteratively refines it with each subsequent chunk. Sequential — not parallelised.

**Best for:** When answer coherence matters more than throughput, or when chunks must be incorporated in order.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseRefineAnswerEngine());
```

## DispatchingAnswerEngine

Install: `dotnet add package Rag.NET.AnswerEngines`

Routes to MapReduce, Refine, or Chat at call time based on `RagOptions.SynthesisStrategy`. Allows runtime switching without re-registration.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseDispatchingAnswerEngine());
```

**Runtime selection:**
```csharp
var result = await pipeline.AskAsync(query, new RagOptions
{
    SynthesisStrategy = SynthesisStrategy.MapReduce
});
```

`SynthesisStrategy` values: `Default` (Chat), `MapReduce`, `Refine`.

## Comparison

| Engine | LLM calls | Parallelism | Best for |
|--------|-----------|-------------|----------|
| Chat | 1 | — | Default, small source sets |
| MapReduce | N + 1 | Yes (map phase) | Large doc sets |
| Refine | N | No | Order-sensitive synthesis |
| Dispatching | Varies | Depends on strategy | Mixed workloads |
