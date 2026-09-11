# The Feature Family Nobody Documented — design for Phase 6.2.40

**Issue:** [#552](https://github.com/MarcelRoozekrans/Rag.NET/issues/552), filed by 6.2.38 while
writing the security posture. **What that issue describes is real but understates the problem**, and
§1 records the difference.

## 0. What #552 said, and what is actually true

#552 says the security guide documents three of four security feature families and that the
prompt-injection family's detail "lives in `docs/reference/features.md`". The first half is right.
**The second half is wrong, and it is wrong in a way that made things worse rather than merely
incomplete.**

Measured 2026-09-11 against `src/Rag.NET.Security/RagBuilderExtensions.cs`:

| Registration method | `docs/guide/security.md` | any other published page |
|---|---|---|
| `UseRbac` | documented | — |
| `UsePiiDetection` | documented | — |
| `UseLlmPiiDetection` | documented | — |
| `UseChunkSanitiser` | one passing mention | — |
| `UseQuerySanitiser` | **nowhere** | **nowhere** |
| `UseLlmQuerySanitiser` | **nowhere** | **nowhere** |
| `UseLlmChunkSanitiser` | **nowhere** | **nowhere** |
| `UseRetrievalGuard` | **nowhere** | **nowhere** |
| `UseTrustLevelGuard` | **nowhere** | **nowhere** |
| `UsePromptHardening` | **nowhere** | one mention in `extending.md` |

**Six public registration methods ship with no published documentation of any kind.** A reader cannot
discover that `UseTrustLevelGuard` exists, let alone how to configure it.

## 1. The reference page is a design proposal marked ✅ Done

`features.md`'s "Prompt Injection Fortification" section carries `**Status:** ✅ Done` and a body
written in future tense about work to be undertaken:

> Mitigation layers **to consider**: …
>
> **Prior art in codebase:** `Rag.NET.Parsers.Vision` ships an internal `PromptInjectionSanitiser` …
> the **full fortification feature should** promote this to a public, pipeline-level
> `IChunkSanitiser` abstraction and add the semantic classifier and retrieval-time trust tagging on
> top.

That promotion happened. `Rag.NET.Security` ships `RegexChunkSanitiser`, `LlmChunkSanitiser`,
`RegexQuerySanitiser`, `LlmQuerySanitiser`, `RegexRetrievalGuard`, `TrustLevelRetrievalGuard` and
`PromptHardeningAnswerEngineDecorator`. **The entry describes the plan, not the result, and names
none of the shipped registration methods.**

`FeatureClaimTests` does not catch this: it asserts only that a ✅ Done section names packages that
exist under `src/`. `Rag.NET.Security` exists, so the claim passes while its body describes unbuilt
work.

## 2. And 6.2.39's posture section points readers at it

This is the part this phase is obliged to fix rather than merely improve. The posture section written
yesterday says:

> **not on this page** — see [Prompt Injection Fortification](../reference/features.md) in the
> feature reference
>
> … Until that section is written here, the reference is the place to read.

**"The reference is the place to read" sends a reader to a proposal.** The previous phase documented
a gap and, in doing so, introduced a misdirection into the one page whose value is being believed.
Leaving it while writing the real section would be knowingly shipping a wrong cross-reference.

## 3. The registration surface has no XML documentation either

**None of the ten public `Use*` methods in `Rag.NET.Security.RagBuilderExtensions` carries a
`<summary>`** — not the six undocumented ones, and not `UseRbac` or `UsePiiDetection`, which the
guide covers well. The file's 26 `///` lines are all on private helpers.

Nothing guards this: `DocumentationQualityTests` scans `src/Rag.NET.Abstractions` only.

**Decided 2026-09-11 by the operator: fix all ten, not just the six.** A guide section serves the
reader who is reading documentation; most people meet these methods by typing `b.Use` and reading
what the IDE offers, and today that is blank. Half-fixing it — documenting the six this phase is
nominally about while leaving `UseRbac` bare beside them — would leave the surface inconsistent for
no reason other than issue scope.

This makes the phase a `src/` change, comment-only. **No behaviour changes**, and §6 records how that
is checked rather than asserted.

## 4. Scope

In:

1. **`## Prompt injection defences`** in `docs/guide/security.md`, matching the depth of the three
   sibling sections: what each sanitiser and guard does, registration, and how they compose with RBAC
   and PII.
2. **Rewrite `features.md`'s "Prompt Injection Fortification" entry** to describe what shipped and
   link to the new section. Scoped to that one entry.
3. **Correct the posture's family table** to link the new section, and delete "the reference is the
   place to read".
4. **`<summary>` on all ten public `Use*` methods** in `Rag.NET.Security.RagBuilderExtensions` (§3).

Out, and filed rather than done:

- **Whether other ✅ Done entries in `features.md` are stale proposals.** 53 entries; that is an audit,
  and possibly a widening of `FeatureClaimTests` to check prose against reality — which is a hard
  problem, not a chore. §1 is evidence it is worth asking, not evidence of how many.
- **Widening `DocumentationQualityTests` beyond `Rag.NET.Abstractions`.** Considered and rejected for
  this phase: scanning all of `src/` would surface a long tail of existing violations across packages
  and turn a documentation phase into an unbounded cleanup discovered mid-flight.

## 5. What the guide section must contain

**The three layers, in pipeline order**, because the family only makes sense positionally:

| Layer | Interface | Registration | Runs |
|---|---|---|---|
| Chunk sanitisation | `IChunkSanitiser` | `UseChunkSanitiser`, `UseLlmChunkSanitiser` | at ingest, before embedding |
| Query sanitisation | `IQuerySanitiser` | `UseQuerySanitiser`, `UseLlmQuerySanitiser` | before retrieval |
| Retrieval guards | `IRetrievalGuard` | `UseRetrievalGuard`, `UseTrustLevelGuard` | on retrieved chunks |
| Prompt hardening | answer-engine decorator | `UsePromptHardening` | at answer assembly |

**The regex/LLM pairing**, which recurs three times and is the family's main configuration decision:
a cheap deterministic pass and an expensive semantic one, registerable independently or chained. The
PII section already establishes this shape for `UsePiiDetection`/`UseLlmPiiDetection`; the section
should point at that rather than re-explaining it.

**How it composes with what is already documented.** `IChunkSanitiser` is shared with PII redaction —
the same interface, the same ordered chain — which the PII section already says. `UseRbac` registers
an `IRetrievalGuard` (`RbacRetrievalGuard`), so RBAC and the retrieval guards are the same extension
point. **That is not obvious from either section today and is the most useful thing the new one can
say.**

**Trust levels and data providers.** `TrustLevelGuardOptions` and the `trust_level` metadata key
connect to ingestion: a provider pulling from an adversarial source is where the level is set. The
section states the connection and links rather than duplicating the ingestion guide.

## 6. Verifiability

**`DocsCodeExamplesTests` will check every C# example** in the new section against what the produced
packages ship. Unlike 6.2.39's external references these are Rag.NET's own types, so they should
resolve — but 6.2.39 shipped a PR that failed exactly this guard after reasoning that markdown cannot
break packaging validation. **`pack-validate` runs locally this time, after a clean repack, before
the PR.**

**The `src/` change is comment-only and that is checked, not asserted**: the whole-solution build must
stay at 0 warnings, and `git diff` must show only `///` lines added in the one file. A diff touching
executable lines in `RagBuilderExtensions.cs` means the phase exceeded its remit.

> **AMENDED 2026-09-11 during implementation.** This design's §5 outline did not anticipate two facts
> that reading the implementations produced, and both changed what the section leads with:
> `TrustLevelRetrievalGuard` treats absent `trust_level` metadata as `internal` (a second fail-open
> default, so the posture's RBAC subsection had to widen to cover both), and query sanitisation does
> not apply to `RetrieveAsync` (documented, and filed as #559 rather than changed). The design's
> instruction to read every implementation rather than write from method names is what produced both.

**The claim no test can make** is that the new section is *accurate* — that each description matches
what the type actually does. The mitigation is the one 6.2.38 established: read every implementation
rather than writing from the registration method's name. `RegexRetrievalGuard`, `TrustLevelRetrievalGuard`
and `QuerySanitiserPipelineDecorator` carry **zero** doc comments, so their behaviour has to come from
their code.
