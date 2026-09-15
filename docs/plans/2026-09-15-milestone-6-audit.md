# Milestone 6 close audit — Hardening & v1.0: Battle-Tested

**Date:** 2026-09-15
**Milestone:** 6 — Hardening & v1.0 — Battle-Tested (opened 2026-08-15)
**Auditor:** `project-orchestration:audit-milestone`
**Verdict:** **FAIL** — four of eight criteria unmet

The Definition of Done is read from the ROADMAP's Milestone 6 section, which `MILESTONE.md`
declares authoritative: *"when they do not agree, this file is the one that is wrong."*

## Criteria

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | Milestones 4 and 5 complete | **PASS** | Audited and archived 2026-08-11 and 2026-08-15 |
| 2 | All planned phases complete | **FAIL** | 6.1 `postponed`, 6.2 `substantially`, 6.3 `pending` |
| 3 | Every `✅ Done` row in `features.md` names what exercises it | **FAIL** | 29 sections claim Done with no `Exercised by:` line |
| 4 | Every retrieval technique and answer engine has a pinned figure with a control | **PASS** | Phase 6.2.1 complete 2026-09-06, every clause of its exit condition met |
| 5 | Every live-service package has a recording **or** a recorded reason | **PASS** | 19 packages carry a `<VerifiedByReason>`; 2 carry recordings; zero live-service packages have neither |
| 6 | No package remains at `VerifiedBy=unit` without a stated reason | **FAIL** | `Rag.NET.Chunking.Templates` is bare `unit`, owed by 6.2 |
| 7 | The release commit is green on both `ci.yml` matrices | **PASS** | `main` run 34958443101, 2026-09-15T10:32, success |
| 8 | Release tagged v1.0 | **FAIL** | Tags are `v0.1.0` and `graphrag-cache-2026-09-14` |

## What changed today

**Criterion 5 moved from FAIL to PASS**, and it is the one that has gated this milestone since
2026-08-20. Seventeen live-service packages sat on `PackagesAllowedToStayUnit` holding an IOU that
named the phase owing them a real run. They now carry a `<VerifiedByReason>` naming the service, why
no recording exists, and what that leaves unverified — which is what the criterion asked for all
along. #631.

The criterion's own wording is why this is a satisfaction rather than a loophole: *"so the gap is
visible per package instead of blocking the release on credentials that may never arrive."*

**It does not make the connectors verified.** It makes the gap legible in the package that ships.
#283 remains open for anyone who can supply an account.

## Gaps, in the order they should be closed

### 1. Criterion 3 — 29 `✅ Done` claims name nothing that exercises them

The largest gap and the cheapest to misread. Phase 6.0 required every Done row to name its evidence;
29 sections do not, including `Cohere Rerank`, `Content-Hash Record Manager`, `Recursive Web
Crawler`, `Sitemap Loader`, `RSS Feed Loader` and `SaaS Connectors`.

`FeatureClaimSymbolTests.EveryDoneFeatureNamesASymbolThatShips` enforces something weaker and
adjacent — that a Done feature names a **symbol that ships**, resolved against the produced packages.
A feature can satisfy that and still name nothing that exercises it. The guard is not the criterion.

**Recommended:** a phase that adds the missing `Exercised by:` lines, or downgrades the claim where
nothing exercises it. The second outcome is the valuable one.

### 2. Criterion 6 — one bare `unit` remains

`Rag.NET.Chunking.Templates`, owed by 6.2: *"a real document of each template's kind."* It is on the
allowlist, so the suite is green; the criterion is not.

**Recommended:** either the real run 6.2 owes it, or a `<VerifiedByReason>` on the same terms as the
seventeen.

### 3. Criterion 2 — three phases not complete

- **6.1 Recorded Responses** — `postponed`. With criterion 5 satisfied by reasons, the phase's
  *gate* is discharged, but its status is not. It needs closing as complete-by-reason or explicitly
  descoped; leaving it `postponed` means criterion 2 can never pass.
- **6.2 Raise the Floor** — status reads `substantially`, which is not a status this workflow
  recognises. 43 sub-phases are complete. It needs a real status before any audit can key on it.
- **6.3 Release v1.0** — `pending`, and correctly so: it is the tag itself.

### 4. Criterion 8 — and a conflict worth resolving before it bites

The DoD says *"Release tagged v1.0."* `CONVENTIONS.md` says **`Released by: release-please`** and
**`Milestone completion tags a release: no`**.

So `complete-milestone` will correctly create **no tag**, and v1.0 must come from release-please.
The criterion and the conventions describe two different mechanisms for the same event. Left
unresolved, criterion 8 can only be satisfied by someone tagging by hand — which is precisely what
the conventions say this project does not do.

**Recommended:** restate criterion 8 in terms of the release-please run, or record a deliberate
exception for the 1.0 tag.

## Not assessed

- **Regression testing** — no web UI in scope; the documentation site is static.
- **Pre-push review reports** — zero are committed. Two were, in `docs/`, and were removed on
  2026-09-15 as session artefacts that the Docusaurus content root would have published. The skill's
  criterion expects at least one PASS report on file; this project treats them as per-session
  artefacts instead. Recorded as a **warning, not a gap** — the review process ran, its outputs are
  simply not part of the repository.

## Verdict

**FAIL.** Four criteria unmet: 2, 3, 6 and 8.

None is blocked on credentials. Criterion 5 was the one that was, and it is now met. What remains is
bookkeeping the project owes itself — 29 evidence lines, one package's reason, three phase statuses,
and one contradiction between the DoD and the release conventions.

**Next:** `plan-milestone-gaps` to turn these four into phases.
