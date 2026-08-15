# Design — Phase 5.2.2: Does GraphRAG help *answers*? (2026-08-15)

**Status:** design, written before any code or any spend. **Owner's question, verbatim:** *"I'm not
happy with the result that the multihop is not helping. Is this because the dataset is not good
enough for it? Did we make an implementation mistake?"*

## Why this phase exists

Phase 5.2 and 5.2.1 measured GraphRAG on MultiHop-RAG in one currency — **document nDCG@10** — and
found it does not help there: −0.043 of store pollution (#232) and −0.028 of behaviour, the latter
plausibly the PageRank blend alone (#239, being measured). That currency cannot see what GraphRAG is
for. Microsoft's claim was never that local search beats dense on retrieval metrics; it was that
graph context and global search produce better *answers* to questions that need more than one
passage. MultiHop-RAG was built to test exactly that — every judged query carries a short gold
answer — and this repository has never scored an answer against one.

So the question the owner asked is answerable, and it has not been asked. This phase asks it in the
dataset's own terms, with the dataset's own protocol.

## The dataset's protocol, read from the source rather than remembered

Read 2026-08-15 from the paper (arXiv 2401.15391, HTML) and the authors' `qa_evaluate.py`
(`github.com/yixuantt/MultiHop-RAG`, `main`, ODC-BY):

- **Query types and gold answers** — inference **816** (the entity, e.g. `"YouTube"`), comparison
  **856** (`yes`/`no`), temporal **583** (`yes`/`no` or `before`/`after`), null **301**
  (`"Insufficient information"`). 2,556 total; the 301 null queries carry no evidence and are the
  ones our qrels judge by nothing.
- **Response evaluation** — *"comparing the LLM response with the ground truth answer of the
  query"*; gold answers *"restricted to simple responses … to facilitate the use of a
  straightforward accuracy metric"*. The script prompts the model to end with
  `The answer to the question is "…"`, extracts the quoted text with a regex, lower-cases both
  sides, and **counts a hit if the prediction and the gold answer share any word**
  (`set(prediction.split()).intersection(gold.split())`). No per-type handling. Accuracy =
  correct / total, overall and per type.
- **Their retrieval setting** — top-6 chunks, `voyage-02` + `bge-reranker-large`; **Table 6**
  reports GPT-4 **0.56** with retrieved chunks against **0.89** with the ground-truth chunks,
  ChatGPT 0.44 / 0.57. The retrieval leg is the ceiling: even GPT-4 loses a third of its accuracy
  to retrieval.

Two consequences. The metric is deterministic and free, so the whole experiment's spend is
generation. And "any shared word" is lenient — `"YouTube Music"` matches `"YouTube"`, `"no, …"`
matches `"no"` — so we report the paper's rule **and** a strict normalised-equality rule beside it,
and say which is which.

## The three arms

Same corpus, same 2,255 judged queries (null queries as a fourth group, reported separately — they
test abstention, and the paper counts them), same embedder (`all-MiniLM-L6-v2`), same answering
model, same prompt, same top-k of context. Only the retrieval path differs.

| Arm | Store | Retrieval | Context handed to the model |
|---|---|---|---|
| **A. Dense** | article chunks only (the Real leg's 17,648) | dense top-*k* | the *k* chunks |
| **B. GraphRAG local** | the graph run's 321,151 units | dense top-500 → `GraphLocalSearchBehavior` (default `w = 0.3`) → top-*k* | the *k* results — article chunks, entity/relationship chunks and reports as they come |
| **C. GraphRAG global** | the graph run's store | `GraphGlobalSearchBehavior` map/reduce over community reports | the synthesised answer chunk it prepends, plus the top-*k* |

*k* = **6**, the paper's, so Table 6 is at least the same shape (different embedder, different
model — comparable in shape, not in number, and the entry will say so every time).

**B is deliberately run at the default `w = 0.3`**, not the `w = 0` that #239 predicts recovers the
retrieval deficit: the question is whether GraphRAG *as shipped* helps answers. If #239's ablation
lands as predicted, a `B′` at `w = 0` is one extra arm at generation cost only, and the entry says
which arm is the shipped one.

**C is the arm the retrieval measurement could not score at all** — its output is an answer, not a
document ranking — and it is the one Microsoft's claim is about. It is also the expensive one:
map over `GlobalReportCandidates` (default 50) reports in batches of `GlobalBatchSize` (default 5)
plus one reduce is ~11 calls per query. The pilot decides whether it runs at 50 or at 20.

## Answering, judging, caching

- **Answering model:** `openai/gpt-4o-mini` at temperature 0 through OpenRouter, the identity every
  other cached generation in this programme uses. One prompt for all arms — question, context,
  the paper's instruction to end with `The answer to the question is "…"`. Committed in the harness,
  versioned into the cache key.
- **Judge:** the authors' rule, re-implemented in C# from the script and unit-tested against a
  handful of hand-worked cases; the strict rule beside it. **No LLM judge** — the paper does not
  use one, the gold answers are one to three words, and adding a second model would add a second
  thing to explain.
- **Every model call is cached** in a `GraphExtractionCache` sibling directory (`graph-answers`),
  keyed on the rendered prompt and the model identity like extractions and reports are, filled once
  by the generation tool and **replayed refuse-on-miss** by the test — so the figure is a
  reproduction like every other in `BeirReproduction`, and the guard makes no model calls.
- **Cost, derived and stated as derived** (#200 still records no `Usage`): A and B are one call per
  query with ~6 chunks of context — 2,255 × 2 × ~$0.0004 ≈ **$2**. C at 50 reports is ~25,000
  map/reduce calls ≈ **$15–25**; at 20 reports ≈ $6–10. **Pilot first: 100 queries stratified by
  type, all three arms, ≈ $1.** If the pilot's per-call cost puts the full C above ~$30, C runs at 20
  reports and the entry says so; if the pilot's *accuracies* are all within noise of each other on
  100 queries, the full run still goes ahead — 100 is calibration, not the experiment.

## What comes out

Per arm, per query type and overall: **paper-rule accuracy** and **strict accuracy**, over the 2,255
judged queries; the 301 null queries reported separately as an abstention rate. Pinned in
`BeirReproduction` under new protocols (`AnswerDense`, `AnswerGraphLocal`, `AnswerGraphGlobal`) —
three, because three figures with three costs — with `BeirRunBudget` cells and the descriptor
declaring them applicable to MultiHop-RAG only, the same registry discipline as every other cell.
The three arms are one test class over one graph build; the budget cell prices the build once.

**How the result reads, decided in advance so it is not decided by the number:**

- **A ≥ B and A ≥ C** — GraphRAG does not help answers here either. The finding from 5.2 stands in
  both currencies, and the owner's two questions get their answer: on this dataset, with this
  implementation and this model, no — and the entry says which parts of "this implementation" are
  design (#239's blend, #232's shared store) and which are the graph.
- **B > A** — the graph context helps the *answer* even though it hurt the *ranking*; the retrieval
  metric was the wrong instrument. That reverses 5.2's headline and the entry says so plainly.
- **C > A** — the claim Microsoft actually made holds here for the question types it holds for;
  per-type accuracy says which. Expect it, if anywhere, on comparison and temporal queries.
- **A > B and C ≈ A on nulls** — abstention: whichever arm says "insufficient information" when it
  should is worth knowing regardless of the rest.

Any of these is a completion. The phase is done when the three arms have run in full, are pinned,
and the entry says which of the four readings the numbers support and with what caveats.

## Not in this phase, said now

- Tuning any arm (`w`, `k`, `LocalTopEntities`, prompt) to make it win. One shipped configuration
  per arm; anything else is a follow-up with its own name.
- A different answering model. The paper's Table 6 shows the model is worth 0.28–0.56 on its own;
  ours is fixed so the arms differ in retrieval only.
- Fixing #239 or #232 first. This phase measures what ships. If the ablations say the blend costs
  the deficit, `B′` at `w = 0` is the cheap way to include that, and it is named as an extra arm.
- HotpotQA / MuSiQue / 2Wiki. Same reasons as 5.2: cost, or no shared corpus.

## Sequence

1. Sidecar: the conversion writes `answers.jsonl` (`id`, `answer`, `question_type`) beside
   `queries.jsonl` from the raw `MultiHopRAG.json`; the loader reads it when present; the counts
   are pinned in `MultiHopRagCounts` like everything else. An existing cache without it re-converts.
2. Judge: the paper's rule and the strict rule as pure functions, unit-tested.
3. Harness: one test class, three arms, one graph build, replay refuse-on-miss; the generation tool
   gains `--stage answers [--arm dense|local|global] [--max-queries N]`, plan-only first.
4. Pilot: 100 stratified queries, all arms, plan-only then spend (~$1). Read the per-call cost and
   the accuracies. Decide C's report count.
5. Full run, pins, cells, ROADMAP/MILESTONE, `features.md` and the retrieval-quality page.
