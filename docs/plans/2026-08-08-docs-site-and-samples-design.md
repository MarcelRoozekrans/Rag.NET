# The Documentation Site, and Samples — Design (Phase 4.5)

**Date:** 2026-08-08
**Milestone:** 4 — Release Readiness
**Status:** approved (design)

## 0. What measurement found

Phase 4.5 is *"End-to-end runnable samples covering the main library scenarios"*, and it also owns
`docs.yml` — a workflow the 2026-08-02 replan assigned here and which was recorded as the last
thing keeping an older entry open.

`docs.yml` does not exist. Measuring why produced the phase.

**The documentation site does not build.** Not "is out of date" — `npm run build` fails. Two
independent causes, and nothing in the repository could have told anyone:

- **A transitive dependency broke it.** `@docusaurus/core` 3.7.0 declares `webpack: ^5.95.0`. That
  caret resolves to **5.109.2**, which tightened `ProgressPlugin`'s options schema; Docusaurus
  3.7.0 still passes `name`, `color`, `reporters` and `reporter`, all now rejected. **There is no
  lockfile**, so this arrives on any fresh install, at a time nobody chose.
- **25 broken links across 7 pages**, including the landing page and getting-started. Docusaurus
  fails the build on broken links by default, which is correct.

Neither was noticed because **no CI job has ever built the site.** `docs.yml` is exactly the guard
that would have caught both, which is why it is this phase's first task rather than its last.

Three smaller findings from the same pass:

- **No lockfile at all** — so `npm ci`, the standard CI install, cannot run, and no two installs
  are guaranteed alike.
- **`node_modules/`, `.docusaurus/` and `build/` are not in `.gitignore`.** A single `npm install`
  leaves 1,415 packages untracked in the working tree.
- **The deploy target names an organisation the repository is not in.** `docusaurus.config.ts` sets
  `url: https://rag-net.github.io`, `organizationName: rag-net`; the repository is
  `MarcelRoozekrans/Rag.NET`. The `RAG-Net` organisation does exist — but this repository is not in
  it, and **GitHub Pages is not enabled** on it either (the Pages API returns 404).

## 1. The repair, in the order the causes were found

**Upgrade `@docusaurus/*` 3.7.0 → 3.10.2.** Measured, not assumed: after the upgrade both the
server and client bundles compile, and the `ProgressPlugin` error is gone. Pinning webpack down
instead would work today and rot the same way tomorrow.

**Commit a lockfile.** This is what makes the upgrade meaningful. Without it the next fresh install
picks new transitive versions again, and the phase's own fix has a shelf life.

**Fix the 25 broken links.** They are real: `getting-started` links to six pages by paths that do
not resolve, and the landing page to four.

**`onBrokenLinks` stays at its default.** Docusaurus offers `'warn'`, which would make the build
pass with the links still broken. That is the option this repository must not take — a green build
over broken documentation is the exact defect shape the last several phases were spent removing.

**`.gitignore` gains `node_modules/`, `.docusaurus/` and `build/`.** `package-lock.json` is
committed, deliberately.

## 2. `docs.yml` builds; deployment is a separate decision

The workflow **builds the site on every pull request**. That is the whole guard: a broken link or a
dependency drift becomes a red check on the change that introduced it, instead of a discovery
months later.

**Deployment is deliberately not wired in this phase**, because it needs a decision this design
cannot make: the site is configured for `rag-net.github.io/Rag.NET/`, the repository lives under
`MarcelRoozekrans`, and Pages is enabled nowhere. Guessing would either publish to a URL nobody
expects or fail a workflow for a reason unrelated to the docs.

So: **build now, deploy when the target is chosen.** The config's stale `organizationName` is left
as it is rather than half-corrected — changing it without knowing the answer would replace an
obviously wrong value with a plausibly wrong one, which is worse.

## 3. Samples

Only `samples/Rag.NET.Sample` exists, for a library of seventy packages.

Samples land **after** the site builds, for a reason beyond ordering: the phase's own charter is
that this is *"when the docs get read end to end"*, and a sample is the honest way to read them —
you follow your own getting-started page and find out where it lies. A sample written against a
site nobody can build is a sample written against nothing.

Scope is deliberately left to the implementation plan, informed by what the broken links expose
about which pages claim what.

## 4. `Rag.NET.Security.AspNetCore`

The **last package at `VerifiedBy: none`**. Phase 4.6 moved `Rag.NET.Mcp.Tool` off it and found
three defects behind that one label — no pipeline registered, no transport registered, and logging
over its own protocol stream.

This is the same shape: an ASP.NET Core integration package nothing exercises. Milestone 4's
Definition of Done requires zero packages at `none` before release, and this is the only one left.

**Expect it to be more than a label change**, on the 4.6 precedent.

## 5. Out of scope

- **Deploying the site.** §2 — needs the Pages/organisation decision.
- **Rewriting documentation content beyond the broken links.** The XML-documentation phase owns the
  API-reference standard and is still blocked on its own scoping call.
- **The 24 npm audit vulnerabilities** reported at install (12 moderate, 12 high). Real, but they
  are build-time tooling for a static site, not shipped code; recorded rather than fixed here, so
  that fixing them is a decision with its own evidence rather than a reflex during a docs repair.
