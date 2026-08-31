# Milestone 6 audit — Hardening & v1.0, Battle-Tested

**Date:** 2026-08-31
**Audited at:** `main` @ `3640637b`
**Verdict: FAIL** — four of seven criteria unmet, one skipped as not applicable.

Run mechanically against git history, the package ledger, `features.md`, and a full test run — not
against recollection. Every count below was measured at audit time.

## Criteria

### 1. Milestone 5 complete — ✅ MET

Closed 2026-08-15 by audit, verdict PASS on all five criteria
(`docs/plans/2026-08-15-milestone-5-audit.md`).

### 2. All planned phases complete — ❌ NOT MET

Of Milestone 6's 16 phase entries: **13 complete**, and three are not.

| phase | status | note |
| --- | --- | --- |
| 6.1 Recorded Responses | **postponed** | blocked on accounts, gates the tag |
| 6.2 Raise the Floor | **substantially** complete | remainder is 6.1's credential-blocked connectors plus 6.2.1's |
| 6.2.1 Retrieval & Answer Sweep | **active** | see criterion 5 |
| 6.3 Release v1.0 | **pending** | only the tag remains |

Complete: 6.0, 6.2.2, 6.2.3, 6.2.4, 6.2.5, 6.2.6, 6.2.7, 6.2.8, 6.2.9, 6.2.10, 6.2.11, 6.2.12.

### 3. Every `✅ Done` row in `features.md` names what exercises it — ❌ NOT MET

**59 Done rows. 9 carry an `**Exercised by:**` line. 50 do not.**

The DoD recorded "56 rows, 0 pointers" when 6.0 wrote it, so nine have been added since — but the
row count has also grown by three. **This is the least-progressed criterion in the milestone and
nothing is scheduled against it.**

The conventions guard `EveryDoneSectionSaysWhatExercisesIt` still reports `[SKIP]` behind a
non-empty allowlist, which is the "failing behind a work list" shape 6.0 deliberately built. **It
must not be read as green.**

### 4. No package remains at bare `VerifiedBy=unit` — ❌ NOT MET

**73 packages. 20 at bare `unit`.**

| level | count |
| --- | --- |
| integration | 31 |
| unit | 22 (**20 bare**, 2 with `<VerifiedByReason>`) |
| container | 11 |
| benchmark | 6 |
| recorded | 2 |
| live | 1 |

Down from 57 bare when 6.0 wrote the list, and from 22 bare at the last recorded count — so two have
moved since. `NoPackageStaysAtBareUnit` likewise reports `[SKIP]` behind its allowlist.

### 5. Every retrieval technique and answer engine has a pinned figure with a control — ⚠️ PARTIAL

**The answer-engine half closed on 2026-08-31.** All three named engines carry a pinned figure
against a properly-instructed control, and `UnmeasuredEngineArms` is empty:

| engine | vs `chatengine` | verdict |
| --- | --- | --- |
| MapReduce | +0.0142, p=0.2955 | no measurable difference |
| Refine | −0.1055, `p<0.0001` | significantly worse |
| FLARE (lookahead) | +0.0075, p=0.0135 | helps, slightly |

Also pinned this milestone: RAPTOR's four arms (Task 5), and the pipeline-parity test runs both legs.

**Still owed by this criterion:**

- HyDE and reranking — re-measurements under the Real protocol; both already have parity cells.
- Hybrid BM25, SPLADE — local, no model spend.
- **Late chunking — has no protocol at all**, and needs the token-level embedding path built before
  one can be written. The largest single piece of engineering left in the phase.
- Every vector store reproducing the SciFact parity figure through itself.
- The second-corpus RAPTOR arm (settles a *hold*, not a clause).
- Local search's unexplained yes/no abstention — open since 2026-08-20, neither answered nor
  explicitly deferred.

**And one open question inside what is already pinned:** `refine`'s −0.1055 was pinned with a caveat
that some deficit may be structural rather than mechanism. MapReduce has since shown a per-chunk
shape concealing a defect worth 0.45, so that caveat is a live question, not a hedge.

### 6. The release commit is green on both `ci.yml` matrices — ❌ NOT MET (not yet applicable)

CI on `main` is green — latest `CI`, `Nightly` and `Docs` runs all succeeded — and the full suite
passes at audit time:

```
Rag.NET.Tests                     1468 total, 0 failed
Rag.NET.Benchmarks.Quality.Tests   405 total, 0 failed
Rag.NET.RepoConventions.Tests       96 total, 0 failed (2 pre-existing skips)
dotnet build Rag.NET.slnx -c Release — 0 warnings, 0 errors
```

The criterion asks for this **of the release commit**, stated as such. There is no release commit
yet, so it cannot be met — but nothing obstructs it either.

### 7. Release tagged v1.0 — ❌ NOT MET

One tag exists: `v0.1.0`. Gated on 6.1 by the operator's 2026-08-20 decision.

### Release tagging — SKIPPED, not a gap

`docs/planning/CONVENTIONS.md` records **`Milestone completion tags a release: no`** — releases are
owned by release-please. Per the audit protocol this criterion is skipped rather than counted as a
failure; the absence of a milestone tag is the correct state.

## Code-quality review coverage — GAP (warning, not a hard fail)

**No `docs/pre-push-review-*.md` reports exist.** `audit-milestone` does not itself re-run
code-quality review, and this milestone has run without `pre-push-review` on file. Recorded as a gap
per the protocol: code quality has not been independently reviewed in this milestone. Not a hard
fail — the milestone pre-dates that skill's adoption here — but the operator should decide whether
to run it on the remaining feature branches.

## Verdict: FAIL

Four criteria unmet, one partial, one skipped as not applicable, one met.

## The gaps, ordered by what actually blocks the tag

1. **6.1's 18 connector cassettes — blocked on accounts, not effort.** The only gap engineering
   cannot clear. If the accounts do not arrive, v1.0 does not either. This is the tag's real gate.
2. **50 `features.md` rows without an *Exercised by* pointer.** Zero model spend, pure effort, and
   the least-progressed criterion in the milestone.
3. **20 packages at bare `VerifiedBy=unit`.** Zero model spend.
4. **6.2.1's remaining techniques.** Mostly local; late chunking needs real engineering first.

**Money is no longer the constraint.** Everything remaining that costs anything is estimated at
$20–40 total, and the expensive runs are behind. Items 2 and 3 together are plausibly more work than
everything else on this list, and neither has a phase scheduled against it.

## Recommendation

Run `plan-milestone-gaps` to schedule phases for items 2 and 3, which are the largest unscheduled
blocks of work in the milestone and currently live only as allowlists inside two skipping guards.
