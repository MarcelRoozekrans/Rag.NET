---
id: answer-engines
title: Answer Engines
sidebar_label: Answer Engines
sidebar_position: 6
---

# Answer Engines

Rag.NET ships with five answer engines. All implement `IAnswerEngine` and produce a string answer from the query and the retrieved source chunks.

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

## FlareAnswerEngine

Install: `dotnet add package Rag.NET.AnswerEngines`

FLARE (Forward-Looking Active Retrieval) generates the answer one sentence at a time. Each sentence is scored by an `IConfidenceScorer` against the current context; when confidence drops below `FlareOptions.ConfidenceThreshold` (default 0.6), the engine runs a lookahead retrieval (original query + low-confidence sentence, `LookaheadTopK` results), merges the fresh sources into the context (deduplicated by document/chunk, max score kept), and regenerates the sentence once. Generation stops when the model signals completion, after `MaxSentences` (default 15), and mid-generation retrievals are hard-capped at `MaxRetrievals` (default 3).

The default scorer is `SelfAssessmentConfidenceScorer` — one small LLM call per sentence that works with every `IChatClient` and fails open (score 1.0) on any failure. A logprob-based scorer is a documented extension point: implement `IConfidenceScorer` and assign it to `FlareOptions.Scorer`.

Lookahead retrievals default to a **plain** retrieval — HyDE and multi-query expansion are disabled (the lookahead query already embeds a synthetic document, so expanding it again multiplies hidden LLM calls), reranking stays active. To customize, set `FlareOptions.LookaheadRetrievalOptions`; it is used verbatim, including `TopK` (in that case `LookaheadTopK` is ignored).

**Best for:** Long-form answers and multi-step reasoning where a single up-front retrieval misses information needed mid-answer. Costs roughly 2 LLM calls per sentence (generation + scoring) plus up to `MaxRetrievals` plain retrievals and one regeneration call each.

**Limitations:** FLARE does not consult `IConversationMemory` (unlike Chat/MapReduce/Refine) — routing a call to Flare via the dispatching engine drops the processed conversation history from the prompt. FLARE also always appends its own fragment protocol (roughly 60 words instructing the model to reply with exactly one sentence, or `<DONE>`) after any `RagOptions.SystemPrompt` you supply — it cannot be displaced, because a caller instruction written for a complete reply (e.g. "end with exactly this sentence") is actively harmful applied per fragment: the model would satisfy it every call and never emit `<DONE>`.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseFlare(o =>
{
    o.ConfidenceThreshold = 0.6;
    o.MaxRetrievals = 3;
    o.MaxSentences = 15;
    o.LookaheadTopK = 3;
    // o.Scorer = new MyLogprobScorer(); // custom IConfidenceScorer
}));
```

`RagResponse.Sources` reflects the full merged context including mid-generation retrievals; streaming delegates to the non-streaming path for the same reason (no token-incremental streaming).

## DispatchingAnswerEngine

Install: `dotnet add package Rag.NET.AnswerEngines`

Routes to MapReduce, Refine, Flare, or Chat at call time based on `RagOptions.SynthesisStrategy`. Allows runtime switching without re-registration. Routing to Flare requires `UseFlare()` to be registered as well (it provides the confidence scorer and options); requesting `SynthesisStrategy.Flare` without it throws.

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

`SynthesisStrategy` values: `Default` (Chat), `MapReduce`, `Refine`, `Flare`.

## Comparison

| Engine | LLM calls | Parallelism | Best for |
|--------|-----------|-------------|----------|
| Chat | 1 | — | Default, small source sets |
| MapReduce | N + 1 | Yes (map phase) | Large doc sets |
| Refine | N | No | Order-sensitive synthesis |
| Flare | ~2 per sentence + 1 per lookahead (plain retrieval: no HyDE/multi-query) | No | Long-form, multi-step reasoning |
| Dispatching | Varies | Depends on strategy | Mixed workloads |
