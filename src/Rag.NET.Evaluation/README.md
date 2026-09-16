# Rag.NET.Evaluation

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Evaluation.svg?logo=nuget&label=Rag.NET.Evaluation)](https://www.nuget.org/packages/Rag.NET.Evaluation)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Evaluation.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Evaluation)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Quality measurement for Rag.NET pipelines: embedding-distance and LLM-judge evaluators,
A/B comparison with confidence intervals, synthetic dataset generation, and the shadow
pipeline that captures production traffic for offline comparison.

## Install

```bash
dotnet add package Rag.NET.Evaluation
```

## Setup

Evaluators are constructed directly from the same Microsoft.Extensions.AI clients your
pipeline uses:

```csharp
using Rag.NET.Evaluation;

var evaluator = new EmbeddingDistanceEvaluator(embeddingGenerator);
// or, criterion-scored by a model:
var judge = new LlmJudgeEvaluator(chatClient);
```

## Example

Score predicted answers against references — and make the score a regression gate in CI:

```csharp
using Rag.NET.Evaluation;

var samples = new[]
{
    new EvaluationSample(
        Question:        "What is Retrieval-Augmented Generation?",
        PredictedAnswer: response.Answer,
        ReferenceAnswer: "RAG combines a retrieval system with a language model to " +
                         "generate answers grounded in retrieved documents."),
};

var result = await evaluator.EvaluateAsync(samples);
Console.WriteLine($"Mean score: {result.MeanScore:F4}");

if (result.MeanScore < 0.85)
    throw new InvalidOperationException("RAG quality regression");
```

`UseShadow<TCandidate>()` mirrors a sample of production questions through a candidate
pipeline and captures both answers for later A/B comparison; RAGAS-style metrics live in
`Rag.NET.Evaluation.Ragas`.

## Full guide

- [Evaluation](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/evaluation.md)
- [Shadow mode](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/shadow-mode.md)
