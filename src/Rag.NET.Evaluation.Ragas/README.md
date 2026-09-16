# Rag.NET.Evaluation.Ragas

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Evaluation.Ragas.svg?logo=nuget&label=Rag.NET.Evaluation.Ragas)](https://www.nuget.org/packages/Rag.NET.Evaluation.Ragas)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Evaluation.Ragas.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Evaluation.Ragas)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

RAGAS-style metrics for Rag.NET pipelines: faithfulness, answer relevance, context
precision and context recall, computed natively in .NET with your own chat and embedding
clients — no Python sidecar.

## Install

```bash
dotnet add package Rag.NET.Evaluation.Ragas
```

## Setup

```csharp
using Rag.NET.Evaluation.Ragas;

var suite = new RagasEvaluationSuiteBuilder(chatClient, embeddingGenerator)
    .AddFaithfulness()
    .AddAnswerRelevance()
    .AddContextPrecision()
    .AddContextRecall()
    .Build();
```

## Example

Samples carry the question, the pipeline's answer and the chunks it used; metrics that
lack their required fields return `null` rather than a fake score:

```csharp
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;

RagasReport report = await suite.EvaluateAsync(samples);

if (report.Faithfulness is { } faithfulness)
    Console.WriteLine($"Faithfulness: {faithfulness:F2}");
```

`RagAbTester` compares two pipeline variants over the same samples with confidence
intervals — feed it live traffic captured by `UseShadow` from `Rag.NET.Evaluation`.

## Full guide

- [Evaluation](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/evaluation.md)
