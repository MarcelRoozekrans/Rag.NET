# Rag.NET.AnswerEngines

Alternative answer synthesis engines for Rag.NET: MapReduce for large context sets,
Refine for iterative drafts, FLARE for confidence-gated re-retrieval, and a dispatching
engine that picks per question — each with self-assessment confidence scoring.

## Install

```bash
dotnet add package Rag.NET.AnswerEngines
```

## Setup

```csharp
using Rag.NET.AnswerEngines;
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag.UseMapReduceAnswerEngine());
```

The engine replaces the default single-prompt synthesis; retrieval is untouched.

## Example

FLARE re-retrieves mid-generation whenever the model's own confidence drops below the
threshold:

```csharp
using Rag.NET.AnswerEngines;
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag.UseFlare(o =>
{
    o.ConfidenceThreshold = 0.6;  // default: re-retrieve below this
    o.MaxRetrievals       = 3;    // default: cap the loop
}));
```

`UseRefineAnswerEngine()` folds chunks into an evolving draft one at a time;
`UseDispatchingAnswerEngine()` routes each question to the engine its shape suits.

## Full guide

- [Retrieval and answering](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/retrieval.md)
