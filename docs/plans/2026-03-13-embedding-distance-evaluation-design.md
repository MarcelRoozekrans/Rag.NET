# Embedding Distance Evaluation Design

**Goal:** Provide a simple way to score RAG answer quality by comparing the semantic similarity between predicted and reference answers.

**New project:** `src/Rag.NET.Evaluation/` — separate from core library, no coupling to vector stores or pipeline internals. References only `Microsoft.Extensions.AI.Abstractions`.

**Types:**
```csharp
record EvaluationSample(string Question, string PredictedAnswer, string ReferenceAnswer);
record EvaluationResult(double MeanScore, IReadOnlyList<double> Scores);
interface IRagEvaluator { Task<EvaluationResult> EvaluateAsync(...); }
class EmbeddingDistanceEvaluator : IRagEvaluator
```

**Algorithm:** Embed all predicted answers in one batch, embed all reference answers in one batch, compute pairwise cosine similarity, return mean score.

**Score interpretation:** 1.0 = semantically identical, 0.0 = completely unrelated. Typical acceptable quality is ≥ 0.85.

**Test project:** `tests/Rag.NET.Evaluation.Tests/` using NSubstitute to mock `IEmbeddingGenerator`.
