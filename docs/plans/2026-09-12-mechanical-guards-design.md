# Two Rules That Were Written Down and Broken Anyway — design for Phase 6.2.42

**Origin:** not an issue. This comes from `STATE.md`'s 2026-09-12 entry, which recorded three rules
this repository had written down and then broken — twice on the same day, by the sessions that wrote
them.

## 0. The observation this phase acts on

| Rule, as recorded | Where it was written | How it failed |
|---|---|---|
| Enumerate suites; do not reason about which are safe to skip | 6.2.39's plan, then `STATE.md` | 6.2.40's plan asserted `Rag.NET.Security` had no test project. It has **104 tests** |
| Commitlint caps headers at 100 and lints every commit a PR adds | a memory note, and `.commitlintrc.yml`'s own comment | a **104-character** header failed CI on #567, on a commit that was not the tip |
| Source `env.sh` before writing "unprovisioned" | `STATE.md`, after **two** prior occurrences | both 6.2.41 sweeps ran unprovisioned; 26 tests skipped that would have passed |

**The pattern is not that the rules were missing.** Each was written, in a file the author had read,
sometimes in the same document. **Prose rules are not checked at the moment they apply.**

What did work in 6.2.41 was mechanical: a count-keyed before/after comparison caught what a
text-keyed one would have reported as 77 false differences, and `git diff … | grep ^src/` proved a
no-code constraint that three paragraphs of intent could not. **This phase converts two of the three
prose rules into things that fire by themselves**, and adds a third guard — §3, found the day after
this design merged — that is stronger than either.

The first rule — enumerate the suites — is left as prose deliberately (§4).

## 1. Guard A: commit header length

**There is no local commit-message check of any kind.** No `.husky`, no `prepare` script, no
`commit-msg` hook, and `core.hooksPath` points at the default `.git/hooks`. `commitlint` runs in CI
only, and only over the commits a pull request adds.

So the feedback loop for a malformed header is: commit, push, open a PR, wait for a 13-second job,
then rewrite history and force-push. On #567 that cost a branch rebuild, because the offending commit
was not the tip and `git commit --amend` could not reach it.

**Scope: header length only.** Not a reimplementation of commitlint. The repository's rules are tuned
in `.commitlintrc.yml` — `type-enum` extended with `bench`, `subject-case` disabled,
`body-max-line-length` deliberately **off** because bodies quote error messages and URLs verbatim —
and a second implementation of those rules would drift from the first. **CI stays authoritative.**
This catches the one rule that has actually bitten, at the moment it applies.

**Delivery: a tracked `.githooks/commit-msg`, enabled per clone with
`git config core.hooksPath .githooks`.** Decided 2026-09-12 by the operator over two alternatives:

- **husky, auto-installed via `npm prepare`** — catches everyone without them thinking about it, but
  adds npm dependencies and a lifecycle script to a `package.json` that exists solely to build the
  Docusaurus site. Machinery in a place its purpose does not justify.
- **A `RepoConventions` test over recent headers** — needs no per-clone setup, but fires only after
  the commit exists, so the remedy is still a rewrite. Earlier than CI, later than a hook.

**The cost of the chosen option is real and must be stated in the phase record rather than glossed:
a hook does nothing until someone runs the config line.** It helps contributors who opt in and nobody
else — including a future session on a fresh clone. It is the option with no dependency footprint,
not the option with the widest reach.

## 2. Guard B: the BEIR provisioning message

`BeirHarness.IsProvisioned` and `IsDatasetCacheProvisioned` return `false` when `RAGNET_BEIR_CACHE`
is unset, and the tests skip. The skip is correct. **The message is not informative enough to
distinguish two very different situations:**

- the corpus genuinely is not on this machine, and
- the corpus **is** on this machine, at the conventional path, and the environment simply does not
  point at it.

The second is what happened three times. `~/.cache/ragnet-beir` exists, carries an `env.sh`, and
sourcing it takes `Rag.NET.Embeddings.Onnx.Tests` from **10 skips to 0** and the benchmark project
from 149/118 to **175 passed / 92 skipped**.

**The fix: when the variable is unset and the conventional directory exists, say so.** A skip
reading *"cache found at ~/.cache/ragnet-beir but RAGNET_BEIR_CACHE is unset — source its env.sh"* is
an instruction. *"Unprovisioned"* is a dead end that three sessions walked into.

**This changes no test behaviour** — the same tests skip under the same conditions. Only the message
changes, and only on the branch where the data is present but unreferenced.

## 3. Guard C: CI cannot say which test failed

**Added 2026-09-12, after this design merged**, because [#571](https://github.com/MarcelRoozekrans/Rag.NET/issues/571)
produced a cleaner instance of this phase's thesis than either guard above, and because it is a
regression this milestone introduced rather than an old habit.

**Phase 6.2.41's migration to Microsoft.Testing.Platform removed failure detail from CI output.**
Between `Run tests:` and `Failed! - Failed: 1` a job log now contains **nothing** — no test name, no
assertion, no stack trace, and no GitHub annotations. On #570 that turned a one-line flake into a
diagnosis requiring a full log read, a count comparison against `main`, and a reproduction outside the
repository — and **the failing test still could not be named.**

**Not ubuntu-specific and not CI-specific.** Reproduced with a throwaway two-test project outside this
repository, one `Assert.Equal` failing on purpose:

```
grep -c "ThisOneFailsOnPurpose\|ALPHA\|BETA"  in dotnet test console output  →  0
```

**The detail is written, just discarded.** It goes to `<project>_net10.0_x64.log` under
`bin/Release/net10.0/TestResults/`, which no workflow uploads, so it dies with the runner. Recovered
from the repro, it carries exactly what is missing:

```
failed Probe.ThisOneFailsOnPurpose (118ms)
  Assert.Equal() Failure: Strings differ
  Expected: "expected-marker-ALPHA"
  Actual:   "actual-marker-BETA"
    at Probe.ThisOneFailsOnPurpose() in ...
```

**One trap the implementation must not walk into: the file is UTF-16LE with a BOM.** A plain `cat` in
a workflow prints `f a i l e d   P r o b e . …` — spaced-out and unreadable, which looks like a
corrupt file rather than a wrong pipeline. `iconv -f UTF-16 -t UTF-8` recovers it cleanly. This is
recorded here because a fix that appears to work while emitting garbage is worse than no fix.

**The fix: when a project fails, dump its log.** `ci.yml` and `nightly.yml` already loop per project
and already capture failure into `$failed`; the addition is a few lines inside the existing failure
branch. **No change to which tests run, which projects are selected, or how failure is determined.**

### Why this belongs in this phase rather than after it

| | Guard A | Guard B | **Guard C** |
|---|---|---|---|
| Cost when it bites | one branch rebuild | 26 tests silently skipped | **every red build, indefinitely** |
| Who it reaches | contributors who opt in | whoever reads the skip | **everyone, no opt-in** |
| Fires unprompted | only after `core.hooksPath` is set | yes | **yes** |

Guard A is the weakest of the three by the phase's own standard — §1 says so plainly. Guard C has no
adoption caveat at all, and it is the only one of the three whose absence is actively costing time
today. **A phase named for rules that were written down and broken anyway should not defer the guard
that would have caught the breakage it is documenting.**

### What this is not

**Not a fix for #571's flake.** The emulator race is untouched; this makes the next occurrence
legible. **Not a reporting overhaul** — no TRX, no JUnit, no artifact upload, no dashboards. The
narrow claim is that a failing CI job should name the test that failed.

## 4. Why the third rule stays prose

The enumerate-the-suites rule stays prose. A guard for it would have to know which suites a given
change could affect, which is the judgement the rule exists to discipline — a mechanical version
would either run everything (which is what the rule already says, and what a plan can simply list as
commands) or guess, and a guessing guard is worse than none.

**The actionable version of that rule is already in use**: 6.2.41's plan listed its suites as literal
commands in a block at the top rather than describing them. That is the fix, and it needs no code.

## 5. Scope

In:

1. **`.githooks/commit-msg`** — rejects a header over 100 characters, naming the length and the cap.
2. **The one-time setup line**, documented where contributors will meet it.
3. **The BEIR skip message** (§2), for the cache and embedder gates that read `RAGNET_BEIR_CACHE`.
4. **A `RepoConventions` test** asserting `.githooks/commit-msg` exists and rejects a 101-character
   header — because a guard nobody tests is the thing this phase is about.
5. **The failing-project log dump** §3, in `ci.yml` and `nightly.yml`, decoded from UTF-16 so the
   output is readable, inside the failure branch those loops already have.

Out:

- **Reimplementing commitlint's rules locally.** Header length only; CI remains authoritative.
- **Changing which tests skip.** §2 is a message change.
- **Auto-installing the hook.** Rejected with its reasoning in §1; revisit if opt-in proves too weak
  to matter.
- **Fixing the #571 emulator flake.** §3 makes the next occurrence legible; it does not chase the
  race. Diagnosing a flake whose failures are anonymous is the thing being unblocked, not the thing
  being done.
- **Any wider test-reporting work** — no TRX, no JUnit, no artifact upload, no retry-on-failure.
  Each is a defensible idea and none is needed to make a red build name its test.

## 6. Verifiability

**Guard A is directly testable**: feed the hook a 101-character header and assert a non-zero exit;
feed it a 100-character one and assert zero. That test is item 4 and is the difference between
shipping a guard and shipping a file that looks like one.

**Guard B is testable at the seam that matters**: with the variable unset and a directory present,
the message names the directory; with neither, it does not. The existing suite already runs
unprovisioned in CI, so the negative case is exercised on every run.

**Guard C is verifiable by deliberate failure, and must be verified that way.** A dumped log is
exactly the kind of change that looks right in review and emits nothing in practice — wrong path,
wrong working directory, wrong branch of the loop, or readable text turned to UTF-16 mojibake. The
implementation must make a test fail on purpose, run the workflow step's own logic against it, and
read the test name back out. **Not a green CI run**: a green run exercises nothing here, which is
precisely how 6.2.41 shipped this regression — its sweep verified that passing still worked and never
once exercised failing.

**What cannot be tested** is adoption — whether anyone runs the `core.hooksPath` line. That is the
stated cost of §1's decision, not an oversight, and the phase record should say so plainly rather
than implying the rule is now mechanically enforced for everyone.
