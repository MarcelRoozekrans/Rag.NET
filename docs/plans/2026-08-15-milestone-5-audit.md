# Milestone 5 — Evaluation Depth: audit

**Date:** 2026-08-15
**Verdict:** **PASS** — all five definition-of-done criteria verified, on `main` at `32fa597f`.

Run the day the milestone's last two phases closed. The ROADMAP is the authoritative copy of the
DoD, as `MILESTONE.md` states; four of its five boxes were already ticked with dated evidence and
were re-read rather than re-run, and the fifth — the clean-restore check — is what this audit *is*.
Every criterion below names what was run or read.

## Criteria

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | Phases 5.1–5.4 complete, sub-phases included | PASS | 5.1, 5.1.1, 5.2, 5.2.1, 5.2.2, 5.3, 5.4 all carry `[status: complete]` in the ROADMAP; 5.5 schedules nothing by design. 5.2.1 and 5.2.2 were both added and closed on 2026-08-15 (#232, #226, #174, #239, #241; PRs #234, #235, #238, #240, #242, #245) |
| 2 | No cross-ecosystem latency figure published without the confound statement beside it | PASS | Met 2026-08-10, re-verified 2026-08-11 (`docs/reference/library-comparison.md`); unchanged since |
| 3 | `IrMetrics`' graded gain has scored a real dataset | PASS | TREC-COVID, 2026-08-12: 14,217 qrels rows at grade 2 through `Evaluate` to nDCG@10 = 0.45427; pinned |
| 4 | Every dataset landed carries the full per-dataset checklist | PASS | TREC-COVID and MultiHop-RAG: descriptor counts from the archive, budget timing measured (MultiHop-RAG re-taken idle-cold twice, #174), published reference or a recorded determination that none exists, licence from upstream, figures pinned at ±0.005 |
| 5 | All test projects passing; solution builds 0 warnings / 0 errors from a clean restore | PASS | Every `bin/` and `obj/` under `src`, `tests`, `benchmarks` deleted; `dotnet restore --force` exit 0; `dotnet build Rag.NET.slnx -c Release --no-restore` **0 Warning(s), 0 Error(s)**; all **73 test suites** green (`Rag.NET.Testing` is a support library, not a suite). Second pass for 12 projects, see below |

## The first pass reported twelve failures, and none was a defect

Recorded because a reader comparing this verdict against the first log would otherwise conclude it
was massaged — the same reason the Milestone 4 audit recorded its two.

1. **Eleven Testcontainers projects** (E2E, Service Bus, Security integration, seven vector stores
   and the vector-store integration suite) failed in their collection fixtures with
   `DockerUnavailableException: Docker is either not running or misconfigured`. Docker Desktop was
   not running on the audit machine. Started; the daemon answered in 10 s; **all eleven re-run
   green** — 11, 79, 7, 17 (+1 skipped), 12, 22, 63, 15 (+1 skipped), 17, 7, 19 tests respectively.
2. **`PackageValidation.Tests.EveryPackageCarriesTheVersionGitVersionDerives`** failed because
   `artifacts/packages` held 70 packages packed on an old branch (`0.1.1-docs-192-193-196-…`) and
   GitVersion derives `0.1.1-preview.50` for this commit. That is the test doing its job on a stale
   local artefact directory. Repacked on this commit with `-p:Version=0.1.1-preview.50` (70
   packages, 0 warnings); **23 of 23 green**.

Corroboration outside this machine: CI on the same commit (`32fa597f`) is green on both
`build-test` matrices, the ubuntu one including its Docker tier.

## Debts this milestone leaves, each with a home

- **#247 — one shared store for article and graph-derived chunks** — the largest measured cost of
  the graph path (−0.043 nDCG, −0.21 answer accuracy). Milestone 6.3, first item.
- **#239 — the PageRank blend on the wrong scale, the discarded traversal.** Measured; the fix is
  a design decision. Milestone 6.3.
- **#176 singleton communities, #200 usage recording, #104 routing, #246 Service Bus flake** —
  Milestone 6, per the re-plan.
- **TREC-COVID's agreement with published is the weakest of the datasets** (−0.018 in a ±0.02
  band) and nothing is scheduled to look into it. Carried into Milestone 6.2's parity-through-stores
  work, where the same leg runs many more times.
- **Generation lives in the answer test class rather than the tool** (Phase 5.2.2). Recorded on
  the class; a refactor with its own name when the tool grows a real embedder.

## What this milestone actually established, in one paragraph

Retrieval quality was measured in a currency GraphRAG does not claim (nDCG) and then in the one it
does (answers), and the two agree on the mechanism: what hurts is a design choice — 300k synthetic
chunks in the store the article text lives in — not the graph; local search as shipped is worse
than plain dense in both currencies; global search is better than dense on the questions where an
answer must be found (0.844 vs 0.772 on entity questions) and no better where it can be guessed;
and every part of that is pinned, replayable and decomposed. Milestone 6 starts from #247.

## No tag

No milestone here has carried a `vN.0` tag (only `v0.1.0` exists, from Milestone 4's publish), and
`v1.0` belongs to Milestone 6 by the 2026-08-03 decision. The milestone is closed by this audit,
the archived `MILESTONE.md`, and the ROADMAP status — the same way Milestone 4 was.
