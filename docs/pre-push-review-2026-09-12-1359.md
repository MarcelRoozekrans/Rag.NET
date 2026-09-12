# Pre-Push Review — Phase 6.2.42 (mechanical guards)

| Metric | Value |
|---|---|
| Date | 2026-09-12 13:59 |
| Branch | `feat/6242-mechanical-guards` |
| Base Branch | `main` (`origin/main` @ `6be62654`) |
| Commits Reviewed | 9 (`6be62654..37928dbf`) |
| Files Changed | 16 committed, +1 uncommitted (this session's doc correction, to be committed with the phase-close commit) |
| Lines Added | 1838 committed (+15 uncommitted) |
| Lines Removed | 17 committed (+7 uncommitted) |
| Verdict | **PASS** |

## Scope of this review

This is Task 4 of the phase's own subagent-driven implementation (verification and the written
record). Tasks 1-3 each carried their own TDD evidence and a self-review, and each was independently
re-reviewed with a fix round recorded in its report (`task-1-report.md` through `task-3-report.md`,
same directory as this phase's briefs). This review re-verifies from the outside — running every
suite fresh, reading every diff against origin/main, and checking commit hygiene and plan adherence —
rather than re-doing per-task review.

## Phase 1: Setup & Context

Base branch: `main`, no upstream configured for the feature branch (expected — not yet pushed).
Nine commits ahead of `origin/main` (`6be62654`), HEAD `37928dbf`. Plan documents:
`docs/plans/2026-09-12-mechanical-guards-design.md` and
`docs/plans/2026-09-12-mechanical-guards-implementation.md`.

## Phase 2: Plan Adherence

**Design → implementation, mapped:**

- **§1 Guard A (commit-header hook)** → Task 2: `.githooks/commit-msg`, `CommitMessageHookTests.cs`,
  `docs/reference/ci.md` section. Delivered.
- **§2 Guard B (BEIR skip message)** → Task 3: `BeirDatasetCache.DescribeUnreferencedConventionalCache`
  (three overloads), `BeirHarness.SkipReason` and the two Onnx test files' `SkipReason` wired to it,
  `SkipMessageTests.cs`. Delivered. No skip *condition* changed anywhere — checked directly (see
  Code Quality below).
- **§3 Guard C (CI failure-log dump)** → Task 1: identical failure-branch block in all four loops of
  `ci.yml` and `nightly.yml`, `CiFailureReportingTests.cs`. Delivered.
- **§4 the third rule (enumerate suites) stays prose** → correctly, no task built a guard for it.
- **§6 Verifiability** — Guard A has `CommitMessageHookTests` executing the real hook via `Process.Start`
  (not grepping the script); Guard B has `SkipMessageTests` with `Assert.Equal` on exact sentences for
  both branches, deterministic via temp directories; Guard C was verified against a deliberate failure
  in a throwaway project, both on Windows and (added during review) inside a fresh Linux container —
  not by a green run.

**No unplanned changes.** `git diff origin/main...HEAD --name-only` shows exactly the files the three
task briefs named, plus the STATE/ROADMAP/MILESTONE/design/implementation planning documents this
task (Task 4) is scoped to touch. Nothing in `src/` outside `BeirDatasetCache.cs`, and that file's
diff is additive only (three new overloads; the existing `ResolveCacheDirectoryFromEnvironment` and
skip-condition logic are untouched — confirmed by direct diff read).

**Missing implementations:** none. All three guards, plus the amendment (Guard C, added the morning
after scoping per #571), are present and tested.

## Phase 3: Code Quality

Read the full diff (`git diff origin/main...HEAD`, 2073 lines) file by file.

- **`.githooks/commit-msg`** — scoped to header length only, as designed; does not reimplement
  commitlint's other rules. Fails closed on a missing UTF-8 locale via a behavioural probe (measures
  a known 1-character/3-byte string) rather than trusting `export`'s exit code or `locale charmap`,
  which the commit history shows was a real defect caught by review (see Task 2's fix round 1/5).
  Shebang is `bash`, invoked directly, never through `sh`.
- **`ci.yml` / `nightly.yml`** — the four failure-branch blocks are byte-identical (diffed against
  each other, confirmed). Each reaches exactly one `echo "::endgroup::"` on all three paths
  (never-built / success / failure-dump). **Minor observation, not a defect:** the inline comment in
  both files still reads "The file is UTF-16LE with a BOM on Windows" — accurate as written (it does
  not claim Linux differs), but now that Task 1's review closed the Linux question with container
  evidence, the comment could say plainly that both platforms produce the same encoding rather than
  naming only Windows. Cosmetic; does not affect behavior, since the BOM sniff already runs
  unconditionally on any platform. Not a blocker.
- **`BeirDatasetCache.cs`** — three overloads, each documented with *why* it exists (test seams
  around process-wide environment state and the real filesystem), not just what it does. No behavior
  change to the parameterless overload production callers use.
- **`SkipMessageTests.cs`, `CiFailureReportingTests.cs`, `CommitMessageHookTests.cs`** — deterministic,
  no reliance on ambient machine state for the tests that assert exact sentences (temp directories,
  GUID-suffixed). `CommitMessageHookTests` explicitly documents in its class remark that **adoption
  of the hook is untestable** — "What this cannot test is adoption. The hook does nothing until
  someone runs `git config core.hooksPath .githooks`... recorded in the design, not an oversight
  here." This is stated in code, not just in prose elsewhere.
- **No debug code, no secrets, no dead imports, no TODO/FIXME** — grepped the full diff for
  `TODO|FIXME|console\.log|debugger;|api[_-]?key|secret|password|BEGIN (RSA|PRIVATE)`: zero matches
  outside plan-document prose describing the phase's own subject matter.

**Findings: 1 Info (cosmetic comment wording), 0 Warnings, 0 Blockers.**

## Phase 4: Commit Hygiene

Nine commits, `git log origin/main..HEAD --format="%s" | awk '{ print length($0)"\t"$0 }'`:

| Length | Header |
|---|---|
| 69 | `test(beir): cover both hint sentences on a machine without the corpus` |
| 75 | `test(beir): say the corpus is here when the skip is only a missing variable` |
| 74 | `fix(build): fail closed when the commit-msg hook cannot get a UTF-8 locale` |
| 71 | `build: add a tracked commit-msg hook that refuses a header over the cap` |
| 68 | `test: tighten iconv assertion to per-loop count, not Contains (#571)` |
| 71 | `ci: dump the failure log so a red job names the test that failed (#571)` |
| 82 | `docs(plans): plan 6.2.42 — three guards, one of which needs a deliberate failure` |
| 93 | `docs(plans): amend 6.2.42 with a third guard, for the blindness the last phase shipped (#571)` |
| 77 | `chore(state): record 6.2.42 scoped, and a red build the log could not explain` |

All nine headers are under the 100-character cap (max observed: 93). No nested parentheses in any
commit body (checked programmatically with a regex over `%b` for each commit — zero matches). No
`claude.ai/code/session_*` URL anywhere in any commit (grepped `%B` across all nine — zero matches).

Four commits (`59f153b1`, `a483cf90`, `2a411441`, `96254a73`) are single-line messages with no body
and therefore no `Co-Authored-By` trailer; the other five carry the trailer. This is a stylistic
inconsistency, not a violation of any stated rule — each task report explicitly notes the choice
(e.g. Task 1: "single-line message, no body, so no nested-paren risk... no session URL included").
Not flagged as a Warning.

**Secrets scan:** grepped full diff for key/token/password/private-key patterns — zero matches.
**Unintended files:** none — `git diff --name-only` lists exactly the 16 files the task briefs and
this task's own scope name; no `node_modules`, `bin/`, `obj/`, `.env`. **Merge conflict markers:**
none. **Large files:** largest new file is the 1013-line implementation plan document (a planning
artifact, not code); largest new code file is `CommitMessageHookTests.cs` at 6.1 KB. No binaries.

**Stray review-file check (explicit instruction for this task):** `git status --short` before and
after this review shows ten untracked `docs/pre-push-review-*.md` files from earlier phases/sessions
(`2026-09-09-0925` through `2026-09-12-0914`). None of them, and none of this file, have been staged
by any command run in this session — every git operation used was read-only (`diff`, `log`, `status`)
until the phase-close commit, which will stage this file by its explicit name only.

**Findings: 0 Warnings, 0 Blockers.**

## Phase 5: Regression Testing

Every suite in the brief's Step 1 was run as a literal command, in full (no `--filter`, which raises
`RAGNET0001` in this repository).

| Suite | Result | Expected | Match |
|---|---|---|---|
| `Rag.NET.RepoConventions.Tests` | 107 passed / 2 skipped / 109 total | 107 / 2 | Yes |
| `Rag.NET.PackageValidation.Tests` | 23 passed / 0 skipped / 23 total | 23 | Yes |
| `Rag.NET.Benchmarks.Quality.IntegrationTests` (unprovisioned) | 154 passed / 118 skipped / 272 total | 154/118/272 | Yes |
| `Rag.NET.Embeddings.Onnx.Tests` (unprovisioned) | 141 passed / 10 skipped / 151 total | 141/10/151 | Yes |
| `Rag.NET.Tests` | 1499 passed / 0 skipped / 1499 total | 1499 | Yes |
| `dotnet build Rag.NET.slnx -c Release` | Build succeeded, 0 warnings, 0 errors | 0 warnings | Yes |
| `npm run build` (docs site) | `[SUCCESS] Generated static files in "build"` (pre-existing Docusaurus deprecation warning only, unrelated to this phase) | success | Yes |

`PackageValidation` passed at 23/23 without needing the repack recipe — `artifacts/packages` was not
stale on this run.

**BEIR-gated projects, sourced `~/.cache/ragnet-beir/env.sh` in the same shell, re-run:**

| Suite | Unprovisioned | Provisioned |
|---|---|---|
| `Rag.NET.Embeddings.Onnx.Tests` | 141 passed / 10 skipped / 151 total | **151 passed / 0 skipped / 151 total** |
| `Rag.NET.Benchmarks.Quality.IntegrationTests` | 154 passed / 118 skipped / 272 total | **180 passed / 92 skipped / 272 total** |

Sourcing took the Onnx project's skips to zero and moved 26 previously-unverified benchmark tests
into "passed" (154 → 180, skip count unchanged in composition — the 92 residual skips are gated on
`RAGNET_BEIR_LONG_RUNS`, API keys, or capability probes, per the design). This is exactly the evidence
Guard B's message exists to convey: the corpus was present the whole time. Confirmed the conventional
directory `C:\Users\MarcelRoozekrans\.cache\ragnet-beir` contains `env.sh`, and that sourcing only
affects the shell it is sourced in — the unprovisioned runs above were taken in a separate shell
invocation from the provisioned ones.

**`Rag.NET.E2ETests` did not run.** It is gated by `<RequiresLlm>true</RequiresLlm>` and runs only in
`nightly.yml`'s LLM tier, which this review does not exercise. Not covered here, correctly — this is
outside this task's scope, not a gap in it.

### Guard C's deliberate-failure evidence — the phase's central claim, quoted verbatim from Task 1's report

A green run of any of the suites above says nothing about whether a *failing* test would be
diagnosable from CI output; that is what Guard C exists to fix, and it can only be demonstrated by a
deliberate failure. From `task-1-report.md`, Step 5, reproduced exactly as recorded (Windows run):

Console-output check confirming the defect being fixed (Microsoft.Testing.Platform prints nothing
useful on failure):

```
grep -c "ThisOneFailsOnPurpose\|ALPHA\|BETA" console.txt
0
```

Raw `dotnet test` console output showed only:

```
Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 252ms - probe.dll (net10.0|x64)
```

The decoded dump, running the exact workflow dump logic against the produced `.log` file:

```
for log in ./bin/Release/*/TestResults/*.log; do
  [ -f "$log" ] || continue
  if [ "$(head -c 2 "$log" | od -An -tx1 | tr -d ' ')" = "fffe" ]; then
    iconv -f UTF-16LE -t UTF-8 "$log" || cat "$log"
  else
    cat "$log"
  fi
done | grep -E "^failed |Assert|Expected|Actual"
```

Output:

```
failed Probe.ThisOneFailsOnPurpose (80ms)
  Assert.Equal() Failure: Strings differ
  Expected: "marker-ALPHA"
  Actual:   "marker-BETA"
```

Task 1's fix round then repeated this exact probe **inside a fresh `mcr.microsoft.com/dotnet/sdk:10.0`
Linux container**, with no reused Windows build output, and got the identical `fffe` BOM and the
identical readable decoded output (test name, assertion type, expected, actual — no mojibake). This
is what closes the Linux-encoding question referenced below: verified twice, by two different people
in two different environments, not assumed.

## Adoption of Guard A — stated plainly, not implied

**Guard A's adoption is untested and untestable by this review or any automated one.** The hook at
`.githooks/commit-msg` does nothing on a fresh clone until a human runs
`git config core.hooksPath .githooks`. `CommitMessageHookTests` executes the hook script directly via
`Process.Start` and proves the *script* rejects and accepts what it should — it does not and cannot
prove that any given contributor, or CI runner's git configuration, has wired it in.
`core.hooksPath` happens to be set to `.githooks` in this clone (verified: `git config
core.hooksPath` → `.githooks`), which is why this session's own commits above were checked by the
hook — but that is a fact about this one clone, not a fact about the repository's contributors in
general. The phase's own record (`CommitMessageHookTests.cs` class remark, and the design and
implementation plans) says the same thing. This review does not imply the rule is now enforced for
everyone; it is enforced only where someone has run that one command.

## Two stale-caveat corrections made in this task

`docs/plans/2026-09-12-mechanical-guards-implementation.md` said, in two places, that "the Linux
encoding was not verified" and that the BOM sniff exists "rather than assuming either way." Both are
now false: Task 1's review closed the question with evidence (two independent runs, one Windows, one
inside a fresh Linux container, identical `FF FE` BOM and identical readable decode). Both passages
were corrected in this session to say the question was closed by evidence rather than left open, and
that the `iconv` branch fires unconditionally on every platform checked while the `else: cat` branch
is dead code on that evidence, kept only in case a future runner disagrees.

`docs/plans/2026-09-12-mechanical-guards-design.md` was checked for the same claim (grepped for
`Linux`, `unverified`, `verif`, `risk`, `assum`, `unknown`) and does not contain it — it states the
BOM trap generally without asserting a platform it had not checked. No correction was needed there;
this is recorded so a reader does not wonder why only one of the two documents named in the task
description was edited.

## Verdict

**PASS.** No blockers. One Info-level cosmetic finding (a workflow comment that could be worded more
generally now that Linux is verified, but is not incorrect and has no behavioral effect). All seven
Step-1 suites match their stated expectations exactly. Both BEIR-gated projects show the expected
unprovisioned-vs-provisioned gap, confirming Guard B's premise. Guard C's central claim — a red build
naming the failing test — is demonstrated by a deliberate failure on two platforms, not asserted from
a green run. Guard A's adoption is stated as untested and untestable, matching the design's own
stance. All nine commits pass the header-length cap, the nested-parenthesis check, and the
session-URL check. No stray files staged.

## Addendum — final whole-branch review and fix wave, 2026-09-12

A later, final whole-branch review of this same branch (ten commits at the time, `305db773`) found
ten findings this Task-4 review above did not catch, because they lay outside what this document
checked:

- **Important 1** — `CiFailureReportingTests` read the raw workflow YAML with `File.ReadAllText`
  instead of `TestProject.ReadWorkflowCommands`, the reader this project already built and
  documented for exactly this mistake (see `TestProjectTierTests`'s own remark on the vacuous
  `Contains("RequiresDocker", yaml)` precedent). Not yet vacuous — the needle counts were 2/2/2,
  all from command lines — but one wording change to the very comment this addendum's sibling
  finding (Minor 4, below) touches would have made it so.
- **Important 2** — none of Guard B's three "present but unreferenced" hint call sites
  (`BeirHarness.SkipReason`, and the two duplicated `ConventionalCacheHint()` helpers in
  `Rag.NET.Embeddings.Onnx.Tests`) was covered by anything that would notice the hint call being
  deleted, on any CI runner: the live hint is empty on every machine without
  `~/.cache/ragnet-beir`, so a runtime test comparing `SkipReason`'s value against the live hint
  cannot distinguish "called and returned empty" from "never called." Closed with a new
  `RepoConventions` source-text guard, `SkipReasonWiringTests`, deterministic on any machine because
  it never touches the environment; the runtime composition tests were kept and tightened, useful
  whenever the live hint is non-null.
- **Minor 3** — a doc comment on `BeirDatasetCache`'s two-parameter overload, and this very
  document's Code Quality section above, both stated the overload relationship backwards. Fixed in
  both places.
- **Minor 4** — the workflow comment this review's own cosmetic finding named is now reworded to
  say the encoding is confirmed identical on Windows and Linux, done after Important 1 so the
  reworded prose cannot satisfy that guard.
- **Minor 5–7** — `ROADMAP.md` claimed a design-document correction that never happened;
  `STATE.md` contradicted its own parenthetical about which document needed correcting; and both
  `STATE.md` and this document undercounted the branch by one commit (nine stated, ten actual).
  All corrected.
- **Minor 8** — `OnnxEmbeddingGeneratorSmokeTests`, the project's third skip site reading the same
  environment variables, never got the hint its two siblings did. Given the same hint, the same way.
- **Minor 9–10** — `docs/reference/ci.md` did not mention that the hook forces `LC_ALL=C.UTF-8` and
  refuses to run if its probe fails; `README.md` had no pointer to the hook at all. Both added.

**Suites re-run after the fix wave:** `RepoConventions` 111 passed / 2 skipped (was 107/2, +4 from
the new wiring guard), `Embeddings.Onnx.Tests` unchanged at 141/10 unprovisioned / 151/0 provisioned,
`Rag.NET.Benchmarks.Quality.IntegrationTests` unchanged at 154/118 unprovisioned / 180/92 provisioned,
`dotnet build Rag.NET.slnx -c Release` 0 warnings / 0 errors. No skip *condition* was changed anywhere
— checked directly, the same way the Task-4 review above checked it for Guard B's original diff.

Full account, including which of Important 2's two offered approaches was chosen and why, and proof
that each new assertion fails when its target is deleted, in
`.superpowers/sdd/2026-09-12-mechanical-guards-implementation/final-fix-report.md`.
