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
prose rules into things that fire by themselves.**

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

## 3. Why these two and not the third

The enumerate-the-suites rule stays prose. A guard for it would have to know which suites a given
change could affect, which is the judgement the rule exists to discipline — a mechanical version
would either run everything (which is what the rule already says, and what a plan can simply list as
commands) or guess, and a guessing guard is worse than none.

**The actionable version of that rule is already in use**: 6.2.41's plan listed its suites as literal
commands in a block at the top rather than describing them. That is the fix, and it needs no code.

## 4. Scope

In:

1. **`.githooks/commit-msg`** — rejects a header over 100 characters, naming the length and the cap.
2. **The one-time setup line**, documented where contributors will meet it.
3. **The BEIR skip message** (§2), for the cache and embedder gates that read `RAGNET_BEIR_CACHE`.
4. **A `RepoConventions` test** asserting `.githooks/commit-msg` exists and rejects a 101-character
   header — because a guard nobody tests is the thing this phase is about.

Out:

- **Reimplementing commitlint's rules locally.** Header length only; CI remains authoritative.
- **Changing which tests skip.** §2 is a message change.
- **Auto-installing the hook.** Rejected with its reasoning in §1; revisit if opt-in proves too weak
  to matter.

## 5. Verifiability

**Guard A is directly testable**: feed the hook a 101-character header and assert a non-zero exit;
feed it a 100-character one and assert zero. That test is item 4 and is the difference between
shipping a guard and shipping a file that looks like one.

**Guard B is testable at the seam that matters**: with the variable unset and a directory present,
the message names the directory; with neither, it does not. The existing suite already runs
unprovisioned in CI, so the negative case is exercised on every run.

**What cannot be tested** is adoption — whether anyone runs the `core.hooksPath` line. That is the
stated cost of §1's decision, not an oversight, and the phase record should say so plainly rather
than implying the rule is now mechanically enforced for everyone.
