# Rag.NET.Evaluation.Ragas

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
