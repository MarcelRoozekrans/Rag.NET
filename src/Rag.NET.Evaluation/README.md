# Rag.NET.Evaluation

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
