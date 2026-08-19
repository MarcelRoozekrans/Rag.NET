# Microsoft GraphRAG local search: the specification, the gap, and the decision it forces

**Date:** 2026-08-18
**Status:** design — no code written
**Supersedes, in part:** `docs/plans/2026-03-30-graphrag-design.md` (local search section)
**Issues:** #247, #239

## Why this document quotes source instead of describing it

The 2026-03-30 design doc described local search in five steps. Step 3 — *collect* — never
shipped. Step 4 — the PageRank blend — did. The result was a behaviour that re-scored whatever
dense retrieval happened to return, and the −0.02761 nDCG@10 that Milestone 5.2 attributed to
"GraphRAG" turned out to be that blend and nothing else: at `PageRankWeight = 0` the ranking was
identical to the control on **2,255 of 2,255 queries** (`docs/guide/graphrag.md:273`).

A paraphrase is what lost step 3. So this document quotes the implementation, with the paths to
re-fetch it, and marks clearly where Microsoft's prose docs are silent and the code is the only
authority.

## Primary sources

| What | Where | Authority |
|---|---|---|
| Prose overview | `microsoft.github.io/graphrag/query/local_search/` | Names the five candidate categories. Silent on priority and budget. |
| Defaults | `packages/graphrag/graphrag/config/defaults.py` → `LocalSearchDefaults` | The values a default install runs with. |
| The algorithm | `packages/graphrag/graphrag/query/structured_search/local_search/mixed_context.py` → `LocalSearchMixedContext.build_context` | **The specification.** Everything below is read from here. |
| Entity selection | `packages/graphrag/graphrag/query/context_builder/entity_extraction.py` → `map_query_to_entities` | |

Re-fetch any of them with
`gh api "repos/microsoft/graphrag/contents/<path>" --jq '.content' | base64 -d`.

## The specification

Local search **builds a context window**. It does not rank documents, and it does not re-score
anything. The output is a string assembled from four sections under a token budget.

### 1. Entity selection

`map_query_to_entities(query, k=top_k_entities, oversample_scaler=2)` — a similarity search over
**entity-description embeddings**, asking for `k * 2` and then filtering by the exclude/include
lists. Note two things the code does that a paraphrase would smooth over:

- The oversample is **not** truncated back to `k` afterwards. With no exclusions the function
  returns 20 entities for `k = 10`. This is the behaviour, not an approximation of it.
- With an empty query it falls back to the `k` highest-`rank` entities, where `rank` is the
  entity's **relationship count** (degree).

### 2. The budget split

```
community_tokens  = max_context_tokens * community_prop
local_tokens      = max_context_tokens * (1 - community_prop - text_unit_prop)
text_unit_tokens  = max_context_tokens * text_unit_prop
```

with `community_prop + text_unit_prop > 1` a `ValueError`. Conversation history, when present, is
built **first** and its tokens are subtracted from `max_context_tokens` before the three
proportions are applied.

### 3. Community context — `_build_community_context`

Reports for the communities the selected entities belong to, ranked by community rank, filtered by
`min_community_rank`, truncated to `community_tokens`. Uses the full report by default
(`use_community_summary=False`).

### 4. Local context — `_build_local_context`

Entities first, rendered as a table and **always included** — the entity table's tokens are counted
but the loop below cannot evict it. Then, *entity by entity in selection order*, relationships and
covariates are added for the entities accumulated so far, and the moment
`entity_tokens + relationship_tokens + covariate_tokens > max_context_tokens` the loop **reverts to
the previous state and breaks**. The relationship table is rebuilt from scratch on each iteration
from the growing prefix, which is quadratic and is what the code does.

Relationship selection (`_filter_relationships`) is the part a paraphrase would flatten to "top 10":

1. **In-network** — both endpoints among the selected entities. Sorted by
   `relationship_ranking_attribute` (default `"rank"`, which the indexer sets to `combined_degree`,
   the sum of the two endpoints' degrees). **Never truncated.**
2. **Out-of-network** — exactly one endpoint selected. Each non-selected endpoint gets a `links`
   count: how many distinct out-network partners it has. Sorted by `(links, rank)` descending, so
   an outside entity shared by several selected entities outranks one reached from a single seed.
3. The cap applies **only to the out-network list**, and it is
   `top_k_relationships × len(selected_entities)` — 200 for the defaults, not 10.

### 5. Text-unit context — `_build_text_unit_context`

The source chunks that produced the selected entities, via `entity.text_unit_ids`, deduplicated,
and sorted by `(entity_order, -num_relationships)` — that is: **the first selected entity's chunks
come first**, and within one entity, chunks covered by more of that entity's relationships come
first. Truncated to `text_unit_tokens`, `shuffle_data=False`.

`count_relationships` is the secondary key: how many of *that entity's* relationships list this
chunk among their own source chunks. It needs relationship→chunk provenance, which
`GraphRelationship` does not carry today — see 6.x.4.

### 5a. Rendered form

Every section is a `|`-delimited table under a `-----Name-----` banner:

```
-----Entities-----
id|entity|description
0|ÅNGSTRÖM|Swedish physicist...
```

Headers by section: entities `id|entity|description` (plus the rank column only when
`include_entity_rank`, default off); relationships `id|source|target|description`; sources
`id|text`. The header row's tokens count against the section budget, and a row that would exceed
the budget **breaks** the loop rather than being skipped — so one overlong row ends the section.

### 6. Assembly order

`[conversation history] + [communities] + [entities, relationships, covariates] + [text units]`

### 7. Defaults

| Setting | `LocalSearchDefaults` | `build_context` signature |
|---|---|---|
| `text_unit_prop` | 0.5 | 0.5 |
| `community_prop` | 0.15 | **0.25** |
| `max_context_tokens` | 12,000 | **8,000** |
| `top_k_entities` | 10 | 10 (`top_k_mapped_entities`) |
| `top_k_relationships` | 10 | 10 |
| `conversation_history_max_turns` | 5 | 5 |

The two disagree. The config values win in a real run — the factory passes them — so
**0.15 / 0.5 / 12,000** is the specification and the signature defaults are dead. Worth recording
because copying the signature is the easy mistake.

### 8. What is *not* in it

No centrality score is blended into any similarity score anywhere in `build_context` or its four
helpers. Entity `rank` (degree) appears in exactly two places: sorting the empty-query fallback,
and an optional *display column* (`include_entity_rank`, default `False`). PageRank does not appear
in local search at all.

## What Rag.NET does today

`GraphLocalSearchBehavior` (after #312) fetches the top-`LocalTopEntities` entity chunks from the
graph's own store, walks their neighbours to collect PageRank scores, blends those into the
candidate scores from dense retrieval, deduplicates on `(DocumentId, ChunkIndex)`, and returns the
list.

## Gap

| Specification | Rag.NET | |
|---|---|---|
| Select entities by description-embedding similarity, `k×2` oversample | Selects by similarity, no oversample, truncates at `k` | partial |
| Community reports as a budgeted context section | Reports reach the model only if they win a dense-retrieval slot | **missing** |
| Entity table always in context | Entities compete for rank against article chunks | **missing** |
| Relationships: in-network uncapped, out-network by (links, rank) capped at `top_k × |selected|` | **Never reach the model at all** | **missing** |
| Covariates (claims) | Not extracted; no prompt, no table, no store column | **missing** |
| Source chunks via `entity.text_unit_ids`, ordered by entity then relationship coverage | Chunks come from dense retrieval, unrelated to the selected entities | **missing** |
| Token budget, split 0.15 / 0.35 / 0.5 | No budget concept | **missing** |
| Conversation history folded into the query and the context | Not plumbed | **missing** |
| — | PageRank blended into similarity scores | **not in the specification** |
| — | `CollectTopEntities` is dead code since #312 | remove |

Every "missing" row is the same missing thing: **the context builder**. Rag.NET implemented the
ranking arithmetic and skipped the assembly the arithmetic was supposed to serve.

## The decision this forces

`IRetrievalBehavior` returns `IReadOnlyList<SearchResult>` — a ranked candidate list. Microsoft's
local search returns a **rendered context string**. That is not a detail to paper over; it is why
the behaviour ended up as a re-ranker in the first place, and any implementation that keeps the
re-ranker shape will lose the specification again.

Three ways out:

**(a) Render, and inject as one synthetic chunk.** Local search stays an `IRetrievalBehavior`,
assembles the four sections, and returns a single `SearchResult` carrying the whole context.
Cheapest; but it makes one chunk that is 12,000 tokens wide, defeats every downstream reranker and
budget, and reports a chunk that corresponds to no document.

**(b) Return the assembled records as ordered `SearchResult`s.** The budget selects *what*, the
pipeline renders it. Keeps composability and the existing contract. Costs faithfulness: the
section structure and delimiters — which the local-search *prompt* is written against — are lost
unless the generation step is taught to rebuild them.

**(c) A separate entry point, `IGraphRagSearch`, outside the retrieval pipeline.** Mirrors
Microsoft's own structure, where `LocalSearch` is a search *strategy*, not a step in someone
else's. Faithful, and it stops pretending graph search is a filter over dense results. Costs a
second public surface, and GraphRAG stops composing with hybrid search and reranking.

**Recommendation: (c).** Local search is not a re-ranker. Two attempts to express it as one have
now produced a behaviour that cost −0.02761 and a behaviour that costs nothing because it has
nothing to do. (b) is the honest fallback if keeping one pipeline matters more than fidelity.

### Decided, 2026-08-18

**(c) — a separate `IGraphRagSearch` entry point.** Local search becomes a search *strategy*, as
it is in Microsoft's own code, and stops being a filter over dense results. The accepted cost is
that GraphRAG no longer composes with hybrid search and reranking: a caller picks local search or
picks the retrieval pipeline. That is the honest shape of the thing — the composition on offer
before was never real, since the blend only ever re-scored candidates the graph had no say in.

Two further calls made with it:

- **Tokenizer: `Microsoft.ML.Tokenizers`.** The budget is stated in tokens, so it is measured in
  tokens. A character estimate would put the eviction points somewhere near Microsoft's rather than
  on them, and "near" is not a thing this exercise can verify against anything. The cost is one
  package reference on `Rag.NET.GraphRag`.
- **Covariates: implemented, not skipped.** Microsoft defaults them off and the extraction pass is
  not free (#300 measured 152.9 s cold over 609 documents). Building them anyway, because the point
  of this work is the full specification rather than the parts that were cheap. They stay off by
  default, as upstream.

## Phases

| Phase | Content | Verifiable by |
|---|---|---|
| 6.x.1 | Delete the PageRank blend and `CollectTopEntities`; `PageRankWeight` obsoleted with the measurement in the message | unit + the existing harness reproducing the control |
| 6.x.2 | Context builder: entity + community + text-unit sections under a token budget, ordered per §6 | unit, with a fixture graph asserting section order and eviction |
| 6.x.3 | Relationships: store-side ranked query by endpoint degree, capped, budgeted | unit + integration against `SqliteGraphStore` |
| 6.x.4 | `entity.text_unit_ids` — requires entity→chunk provenance to survive ingestion, which `source_chunk_ids` already carries | unit |
| 6.x.5 | Covariates: extraction prompt, store table, context section. Off by default, as upstream | integration |
| 6.x.6 | Conversation history | unit |
| 6.x.7 | Re-measure on MultiHop-RAG against the same control as 5.2 | benchmark |

Phase 6.x.7 is the point of all of it. The 5.2 finding — "GraphRAG does not help on this corpus" —
was measured against a local search that had never implemented local search. It is not yet an
answer about GraphRAG.

## 9. Conversation history

Read from `packages/graphrag/graphrag/query/context_builder/conversation_history.py` on
2026-08-19, at blob `570751fe342fcc5bdf53d040db3e4db44dc1452f`, plus the `conversation_history`
handling inside `LocalSearchMixedContext.build_context` in
`packages/graphrag/graphrag/query/structured_search/local_search/mixed_context.py`, at blob
`ad5d2888b9687b754d78f4e3559c01d912231467`. Quoted, not paraphrased, for the reason in the opening
section.

**The two headline findings, stated first because they overturn assumptions the next task is
written against:**

- **Entity selection sees the history.** `mixed_context.py` concatenates the last
  `conversation_history_max_turns` user turns onto the query *before* calling
  `map_query_to_entities`. History is folded into the query before entity selection — the opposite
  of the assumption this document previously carried.
- **The rendered table is two columns, not three.** The banner text is right
  (`-----Conversation History-----`), but there is no `role` column. The DataFrame has columns
  `turn` and `content`; `turn` *holds* the role string (`user`/`assistant`), it does not sit
  alongside one.

### 9.1 The turn model

```python
class ConversationRole(str, Enum):
    """Enum for conversation roles."""

    SYSTEM = "system"
    USER = "user"
    ASSISTANT = "assistant"
```

Three values, `str`-backed (`ConversationRole.USER == "user"` is `True`). `from_string` raises
`ValueError` on anything else — there is no fourth role, and in particular no `tool` role, unlike
`Microsoft.Extensions.AI.ChatRole`.

```python
@dataclass
class ConversationTurn:
    role: ConversationRole
    content: str
```

One role, one content string. `__str__` is `f"{self.role}: {self.content}"` — not used by
`build_context`, which renders through a DataFrame instead (§9.3).

```python
@dataclass
class QATurn:
    user_query: ConversationTurn
    assistant_answers: list[ConversationTurn] | None = None

    def get_answer_text(self) -> str | None:
        return (
            "\n".join([answer.content for answer in self.assistant_answers])
            if self.assistant_answers
            else None
        )
```

A `QATurn` pairs one user turn with zero or more "assistant" turns, newline-joined when rendered.

`ConversationHistory` is built by repeated `add_turn(role, content)` (or `from_list` over
`[{"role": ..., "content": ...}, ...]`) onto a flat `turns: list[ConversationTurn]`, oldest first —
there is no separate storage for pairs; pairing happens on demand:

```python
def to_qa_turns(self) -> list[QATurn]:
    qa_turns = list[QATurn]()
    current_qa_turn = None
    for turn in self.turns:
        if turn.role == ConversationRole.USER:
            if current_qa_turn:
                qa_turns.append(current_qa_turn)
            current_qa_turn = QATurn(user_query=turn, assistant_answers=[])
        else:
            if current_qa_turn:
                current_qa_turn.assistant_answers.append(turn)  # type: ignore
    if current_qa_turn:
        qa_turns.append(current_qa_turn)
    return qa_turns
```

Only `USER` starts a new pair; **every other role (`ASSISTANT` or `SYSTEM`) between two user turns
is appended to that pair's `assistant_answers`**, whatever it actually is. A `SYSTEM` turn placed
mid-history renders under the literal string `assistant` in the output table. This is a real
behaviour of the source, not a hypothetical — worth carrying into Task 3's grouping logic rather
than "fixing" it.

### 9.2 What reaches the context

`ConversationHistory.build_context` signature:

```python
def build_context(
    self,
    tokenizer: Tokenizer | None = None,
    include_user_turns_only: bool = True,
    max_qa_turns: int | None = 5,
    max_context_tokens: int = 8000,
    recency_bias: bool = True,
    column_delimiter: str = "|",
    context_name: str = "Conversation History",
) -> tuple[str, dict[str, pd.DataFrame]]:
```

`include_user_turns_only=True` (default, and how `mixed_context.py` calls it via
`conversation_history_user_turns_only`, itself default `True`) strips `assistant_answers` before
rendering, so **by default only user questions reach the rendered table** — QA pairing exists in
the data model but is normally invisible in the output.

`max_qa_turns` caps how many *QA turns* (not raw messages) are kept, default 5, matching
`conversation_history_max_turns` in `LocalSearchDefaults` and in `build_context`'s own signature —
this row in §7's table was already correct.

**The recency direction is not what the docstring implies, and this is the second real finding of
this section.** The docstring for `recency_bias` reads *"If True, reverse the order of the
conversation history to ensure last QA got prioritized"* — default `True`. But the only caller,
`mixed_context.py`, calls it with `recency_bias=False` explicitly:

```python
(
    conversation_history_context,
    conversation_history_context_data,
) = conversation_history.build_context(
    include_user_turns_only=conversation_history_user_turns_only,
    max_qa_turns=conversation_history_max_turns,
    column_delimiter=column_delimiter,
    max_context_tokens=max_context_tokens,
    recency_bias=False,
)
```

Inside `build_context`, the order of operations is:

```python
qa_turns = self.to_qa_turns()          # oldest first
if include_user_turns_only: ...        # order unchanged
if recency_bias:
    qa_turns = qa_turns[::-1]          # reverse — SKIPPED, since recency_bias=False here
if max_qa_turns and len(qa_turns) > max_qa_turns:
    qa_turns = qa_turns[:max_qa_turns] # takes the FIRST max_qa_turns of whatever order survives
```

With `recency_bias=False` (the real call path), `qa_turns` stays oldest-first, and the
`[:max_qa_turns]` slice therefore keeps the **oldest** `max_qa_turns` QA pairs, not the most
recent ones, whenever the history has more turns than the cap. This only matters once history
exceeds 5 QA pairs; below that, every turn survives regardless of direction. Recorded verbatim
because it reads as a bug (the parameter is named for the opposite behaviour) but it is upstream's
actual, exercised behaviour — Task 3 should match it, not the docstring.

### 9.3 Rendering

```python
header = f"-----{context_name}-----" + "\n"

turn_list = []
current_context_df = pd.DataFrame()
for turn in qa_turns:
    turn_list.append({
        "turn": ConversationRole.USER.__str__(),
        "content": turn.user_query.content,
    })
    if turn.assistant_answers:
        turn_list.append({
            "turn": ConversationRole.ASSISTANT.__str__(),
            "content": turn.get_answer_text(),
        })

    context_df = pd.DataFrame(turn_list)
    context_text = header + context_df.to_csv(sep=column_delimiter, index=False)
    if tokenizer.num_tokens(context_text) > max_context_tokens:
        break

    current_context_df = context_df
context_text = header + current_context_df.to_csv(
    sep=column_delimiter, index=False
)
return (context_text, {context_name.lower(): current_context_df})
```

With defaults, this renders as:

```
-----Conversation History-----
turn|content
user|<first question>
assistant|<its answer, if include_user_turns_only=False>
user|<next question>
```

It **is** a table, but it is a `pandas.DataFrame.to_csv` table with **two columns**, `turn` and
`content` — `turn` is not a turn index, it is the role string. There is no third `role` column and
no separate turn-number column. The header row is emitted by `to_csv` from the DataFrame's own
column names, not hand-written. This contradicts the assumed `turn|role|content` three-column
format: the assumption should be dropped in favour of `turn|content`.

The loop is break-not-skip, same pattern as Rag.NET's `ContextTable`: it keeps the last
`context_df` that fit (`current_context_df`) and stops accumulating on the first one that doesn't,
so a mid-history overlong QA pair truncates everything after it rather than being skipped over.

One rendering detail this document cannot confirm in this environment: `pandas.to_csv`'s default
quoting is `QUOTE_MINIMAL` — a documented pandas default, meaning a cell containing the delimiter
(`|`), a quote character, or a newline gets wrapped in double quotes in the real output. No Python
was available in this sandbox to execute `to_csv` and verify the exact bytes, so this is recorded
as a plausible, unverified detail rather than a confirmed one — flagged for Task 3 in §9.6.

### 9.4 The budget interaction

From `LocalSearchMixedContext.build_context`, conversation history is handled **first**, before
any of the three proportions are computed, using the full (not yet divided) `max_context_tokens`
as its own ceiling:

```python
if conversation_history:
    (
        conversation_history_context,
        conversation_history_context_data,
    ) = conversation_history.build_context(
        ...,
        max_context_tokens=max_context_tokens,   # the whole budget, unreduced
        recency_bias=False,
    )
    if conversation_history_context.strip() != "":
        final_context.append(conversation_history_context)
        final_context_data = conversation_history_context_data
        max_context_tokens = max_context_tokens - len(
            self.tokenizer.encode(conversation_history_context)
        )

# only now:
community_tokens = max(int(max_context_tokens * community_prop), 0)
...
local_tokens = max(int(max_context_tokens * local_prop), 0)
...
text_unit_tokens = max(int(max_context_tokens * text_unit_prop), 0)
```

So the subtraction happens **before** `community_prop`/`local_prop`/`text_unit_prop` are applied —
each of the three proportions is a fraction of what's left *after* history, not of the original
`max_context_tokens`. This confirms the existing clause in the opening section
("built first, tokens subtracted before the proportions").

**When history alone exceeds `max_context_tokens`:** inside `ConversationHistory.build_context`,
`current_context_df` starts as an empty `pd.DataFrame()` before the loop. If even the *first* QA
pair already pushes `header + context_df.to_csv(...)` over the token limit, the loop breaks before
ever assigning `current_context_df = context_df` — so the function returns `header +
<csv of an empty DataFrame>`, i.e. just the banner line, no rows. That string's `.strip()` is
still non-empty (the banner text itself is non-blank), so `mixed_context.py`'s
`if conversation_history_context.strip() != "":` guard is still `True`: **the banner-only text is
still appended to `final_context`**, and its (small) token cost is still subtracted from the
remaining budget. History does not get dropped or error out when it doesn't fit — it degrades to a
header line that still costs a few tokens against the community/local/text-unit split.

The only case where history contributes nothing at all is when there are zero QA turns to begin
with (`len(qa_turns) == 0`), where `build_context` returns `("", {...})` before the header is even
built, and the `.strip() != ""` guard in `mixed_context.py` then skips it and leaves
`max_context_tokens` untouched.

### 9.5 Entity selection

```python
# map user query to entities
# if there is conversation history, attached the previous user questions to the current query
if conversation_history:
    pre_user_questions = "\n".join(
        conversation_history.get_user_turns(conversation_history_max_turns)
    )
    query = f"{query}\n{pre_user_questions}"

selected_entities = map_query_to_entities(
    query=query,
    ...
)
```

**History text is concatenated into the query before `map_query_to_entities` runs.** This is the
opposite of the assumption this document previously carried, and it happens through a *different*
code path from §9.3's rendered table:

- It uses `get_user_turns(conversation_history_max_turns)`, not `build_context`. `get_user_turns`
  walks `self.turns` in reverse and collects **only `USER`-role turns** (never assistant answers,
  regardless of `include_user_turns_only`), stopping once it has `conversation_history_max_turns`
  of them — so it is always the most-recent-first, unlike §9.2's oldest-first slicing quirk.
- Those questions are newline-joined and appended, unlabelled, directly after the current query:
  `f"{query}\n{pre_user_questions}"` — no banner, no delimiter, no role marker, just raw text
  concatenation.
- This happens **before** `conversation_history.build_context()` is called to build the rendered
  table (§9.3/§9.4), and the two draw from the same `conversation_history_max_turns` count but
  through unrelated methods with unrelated ordering rules.

### 9.6 What this library cannot reproduce

No field-level gap was found between upstream's turn model and Rag.NET's:
`Microsoft.Extensions.AI.ChatMessage`'s `Role` (`ChatRole.System`/`User`/`Assistant`, plus a
`Tool` value upstream has no equivalent of) and text content map directly onto
`ConversationRole`/`ConversationTurn`'s role and content. `QATurn`'s pairing (§9.1) is an algorithm
over a flat list, not a field upstream's model carries and Rag.NET's cannot — it is reproducible in
C# by grouping the same flat list on "is this a `User` turn".

Two things flagged as **Deviation** candidates for Task 3, not as impossibilities:

- **CSV quoting.** `pandas.to_csv`'s default (`QUOTE_MINIMAL`) quotes a cell containing the
  delimiter, a quote character, or a newline. Rag.NET's `ContextTable`/`Clean()` only strips
  `\r`/`\n` from cells and does not quote an embedded `|` — its own doc comment already names this
  exact gap for the other sections ("Upstream does not do this; upstream also writes CSV through
  pandas rather than joining strings"). It applies identically here: a user question containing
  `|` would upstream-render as a quoted cell and Rag.NET-render as a row with an extra column. Not
  executed against a real pandas install in this environment (§9.3) — a real finding, but an
  unverified one.
- **No conversation-history field exists yet.** `LocalSearchInputs` has no conversation-history
  property and `LocalSearchContext` has no corresponding `SectionFill`/records output. This is not
  a reproducibility gap, just unbuilt — it is exactly the shape of work 6.x.6 and Task 3 add.

## Open questions

1. ~~Does the answer prompt change?~~ — decided: the 6.x.7 measurement uses
   `BeirGraphRagAnswerTests`' shared `PromptTemplate`, not `LocalSearchPrompt`. Every other
   measurement arm uses that shared template; changing the context format and the prompt in the
   same measurement would confound the two, the same isolation argument that made the `filtered`
   arm interpretable on its own. `LocalSearchPrompt` remains the library default for
   `LocalSearchAsync` — measuring it against the other arms is a separate question with its own
   arm, not part of 6.x.7.
2. ~~Tokenizer~~ — decided above.
3. ~~Covariates~~ — decided above.
