# What the Project Claims About Its Own Security — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** state the project's security posture, give 71 published packages a vulnerability-disclosure path, and spend the one-line dependency fix that has been available since 2026-09-07 — without changing any security behaviour.

**Architecture:** Two documents and one dependency pin. `SECURITY.md` at the root carries the disclosure policy. A `## Security posture` section at the top of `docs/guide/security.md` carries the claims, the boundary, and the dependency position. `package.json`'s existing `overrides` block gains `qs`. A new repo-conventions test pins the two documentation facts most likely to rot silently.

**Tech Stack:** Markdown, npm `overrides`, xunit v3 (for the guard).

**Spec:** `docs/plans/2026-09-11-security-posture-design.md` — read it first, **and read §0 below second: the design's §5 states something about verification that is false**, and this plan corrects it rather than inheriting it.

## Global Constraints

- **Phase:** 6.2.38. No issue number — this came from `STATE.md`'s open list, not from a GitHub issue.
- **Conventional commits, header at most 100 characters.** CI lints every commit a PR adds.
- **NO SECURITY BEHAVIOUR CHANGES.** No authorization added, no default flipped, no RBAC semantics touched. This is the phase's main risk and its hardest boundary. **If writing the posture surfaces something that ought to change, file it — do not fix it.** A security change landing in a PR reviewed as a documentation change is how a default gets flipped without anyone reviewing it as a default flip.
- **Every claim in the posture must be checked against the code, not written from memory.** The design's §5 named this as the honest mitigation for the one thing tests cannot verify, and it already paid off once — see §0.2.
- **Do not document the prompt-injection family in full here.** §0.2 found it missing from `security.md`; the posture *names and links* it. Writing a fifth feature section is a separate piece of work and gets filed.
- **Test command:** `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`. Baseline at the time of writing: **98 passed / 2 pre-existing skips** — confirm in Task 0.

---

## §0. Two corrections to the design, found before writing anything

### 0.1 The design's §5 is wrong about verification

It says:

> `Rag.NET.RepoConventions.Tests` carries documentation guards (`DocumentationQualityTests`, `DocumentedConstraintGuardTests`, `FeatureClaimTests`) which already run against `docs/guide/`, so the new prose is checked by the same gates as the rest.

**Nothing validates `docs/guide/` markdown.** `DocumentationQualityTests` parses XML `<summary>` blocks under `src/Rag.NET.Abstractions` and asserts they are not restatements of the member name. `DocumentedConstraintGuardTests` reads `*Options.cs` doc comments and checks something enforces the numeric constraints they claim. The only occurrences of the string `docs/guide` anywhere in that test project are **two comments** in `TestGateTests.cs`.

So the new prose lands with **no automated gate at all**, and the design said the opposite. That matters because it changes what "done" means here: verification is reading, and the plan must not pretend otherwise.

**Task 4 adds a small guard** rather than accepting that. Not to check the prose — no test can — but to pin the two facts most likely to rot silently: that `SECURITY.md` exists and names a reporting channel, and that `security.md`'s posture section still names every security-adjacent package. The repo has clear precedent for guarding documentation claims (`DocumentedConstraintGuardTests` exists because doc comments promised constraints nothing enforced).

### 0.2 A whole security feature family is missing from the security page

Enumerating the ten security-adjacent packages instead of writing from memory — the design's own §5 mitigation — turned this up immediately.

`Rag.NET.Security`'s package description begins: *"Prompt injection defence-in-depth for Rag.NET: chunk sanitisation, query sanitisation, retrieval…"*. The package contains `RegexQuerySanitiser`, `LlmQuerySanitiser`, `RegexRetrievalGuard`, `TrustLevelRetrievalGuard`, `PromptHardeningAnswerEngineDecorator`, `InjectionPatterns` and `TrustLevelGuardOptions`.

**`docs/guide/security.md` documents none of it.** Its sections are RBAC, PII Detection and Redaction, Audit Log, and Composing Features. `IChunkSanitiser` appears only in its PII role. The words "prompt injection", "query sanitiser", "trust level" and "retrieval guard" do not appear on the page.

The detail lives in `docs/reference/features.md` §"Prompt Injection Fortification" (line 781), which opens by calling indirect prompt injection **"the primary RAG security risk"**.

So: the project's reference documentation names a primary security risk, ships a package family defending against it, and **the page called "Security" never mentions it**. A reader evaluating posture reads that page.

This is precisely the gap a posture section exists to close, and it is why the posture enumerates families rather than repeating the three the page already covers. **Writing the missing feature section is out of scope** (Global Constraints) — the posture names it, links to `features.md`, and Task 5 files the gap.

---

### Task 0: Baseline

- [x] **Step 1: Record the baseline**

```bash
dotnet test tests/Rag.NET.RepoConventions.Tests -c Release
```

Write the count here. Expected 98 passed / 2 pre-existing skips; confirm rather than trust.

- [x] **Step 2: Confirm the dependency position has not moved since the design**

```bash
gh api repos/MarcelRoozekrans/Rag.NET/dependabot/alerts --jq '.[] | select(.state=="open") | "\(.security_advisory.severity)\t\(.dependency.package.name)\t\(.dependency.manifest_path)"'
```

Expected: 5 open — `image-size` ×2 and `qs` ×2 in `package-lock.json`, `nltk` in `benchmarks/library-comparison-python/uv.lock`. **If the set has changed, the posture's table changes with it** — this is the one part of the document that goes stale on someone else's schedule, and writing yesterday's numbers into a security document is worse than omitting them.

---

### Task 1: `SECURITY.md`

**Files:** Create `SECURITY.md` (repository root).

- [x] **Step 1: Establish the supported-version claim before writing it**

```bash
gh api repos/MarcelRoozekrans/Rag.NET/releases --jq '.[0].tag_name' 2>/dev/null
dotnet dotnet-gitversion /output json /showvariable SemVer   # from PowerShell
```

The published line is `0.1.0` (71 packages, 2026-08-11) and v1.0 is unreleased. **Do not write "1.x is supported"** — it does not exist. The honest claim is that the latest published `0.1.x` is supported and that pre-1.0 means no backport guarantee.

- [x] **Step 2: Write it**

Keep it short — a disclosure policy that reads like a contract does not get read. Cover exactly:

- **Supported versions** — per Step 1.
- **How to report** — GitHub private security advisories (the repository's Security tab → "Report a vulnerability"), *not* a public issue, and **not an email address**: the design chose this so no personal mailbox is published on a public repo. A one-line substitution if the maintainer decides otherwise.
- **What to expect** — acknowledgement target, and that fixes ship in a normal release rather than a private channel.
- **Scope** — that reports about the Docusaurus build, the Python benchmark harness, or a consumer's own configuration of the library are out of scope for a security advisory, with a pointer to the posture section for why.

**Do not invent a response-time SLA the maintainer has not agreed to.** "Best effort, typically within a week" is honest for a single-maintainer project; "within 24 hours" is a promise this repository has no evidence of keeping.

- [x] **Step 3: Verify GitHub sees it**

```bash
gh api repos/MarcelRoozekrans/Rag.NET/community/profile --jq '.files.security'
```

Returns non-null once the file is on the default branch — so this reads `null` until the PR merges. **Record that expectation here rather than treating the null as a failure**; check it again after merge.

- [x] **Step 4: Commit**

```bash
git add SECURITY.md
git commit -m "docs(security): add a disclosure policy for 71 published packages"
```

---

### Task 2: The posture section

**Files:** Modify `docs/guide/security.md` — insert after the front-matter/title block, **before** `## RBAC on Chunks`.

- [x] **Step 1: Re-read the three feature sections before writing about them**

The posture summarises what the page already documents. Read `## RBAC on Chunks`, `## PII Detection and Redaction` and `## Audit Log` first so the summary matches the page rather than a recollection of it.

- [x] **Step 2: Write the section**

`## Security posture`, covering, in this order:

**(a) What this library is, in one paragraph.** A RAG library with opt-in security features — not a security product. Every feature named below is opt-in and off by default.

**(b) The four feature families**, one line each with a link to where each is documented:

| Family | Where documented |
|---|---|
| RBAC on chunks | this page, below |
| PII detection and redaction | this page, below |
| Audit log | this page, below |
| **Prompt-injection defences** — chunk/query sanitisation, retrieval guards, prompt hardening | `docs/reference/features.md` § "Prompt Injection Fortification" |

**State plainly that the fourth is not documented on this page**, and that `features.md` calls indirect prompt injection the primary RAG security risk. Do not quietly link it as though the page covered it — see §0.2.

**(c) RBAC fails open.** The sharpest item, and it goes near the top of the section, not buried:

> Chunks without an `allowed_roles` metadata key are world-readable and pass through for every caller.

With the reasoning: retrofitting RBAC onto an existing corpus would otherwise hide every previously-ingested chunk the moment the feature is registered. **Documenting the decision, not proposing a change** (Global Constraints). Quote the existing sentence from the RBAC section rather than paraphrasing it, so the two cannot drift.

**(d) The boundary — what the library does not do.** It filters retrieved chunks, redacts at ingest, records an audit trail. It does **not** authenticate end users, encrypt at rest, manage keys, or secure the backing store. Name the ten security-adjacent packages and which side of the boundary each sits on:

`Rag.NET.Security`, `Rag.NET.Security.AspNetCore`, `Rag.NET.Security.Audit.Sqlite`, `Rag.NET.Mcp`, `Rag.NET.Mcp.AspNetCore`, `Rag.NET.Mcp.Tool`, `Rag.NET.Api`, `Rag.NET.Api.Client`, `Rag.NET.Api.Grpc`, `Rag.NET.Api.Grpc.Client`.

**Read each `.csproj` `<Description>` rather than assuming what the package does** — that is how §0.2 was found.

**(e) The MCP write surface.** `Rag.NET.Mcp` exposes tools that write; API-key authorization exists (`McpApiKeyAuthorization`, `McpApiKeyEndpointFilter`) and is **opt-in**, so an unconfigured host is unauthenticated. Check the registration extension before writing the sentence — if `Rag.NET.Mcp.AspNetCore`'s description ("a guarded HTTP transport that refuses to serve…") means it refuses to start unauthenticated, then "unauthenticated by default" is wrong and the posture must say what is actually true.

**(f) The dependency position.** The table from Task 0 Step 2, with the closure argument: **none of these is in a shipped NuGet package's dependency closure**. State which are unpatched and which was fixed here (Task 3).

- [x] **Step 3: Check every link resolves**

```bash
grep -oE "\]\([^)]+\)" docs/guide/security.md | sort -u
```

Read the list; confirm each relative path exists and each anchor matches a real heading. A posture document with a broken link to the prompt-injection docs is worse than one without the link, because it looks answered.

- [x] **Step 4: Commit**

```bash
git add docs/guide/security.md
git commit -m "docs(security): state the posture, not just the features"
```

---

### Task 3: The `qs` override

**Files:** Modify `package.json`.

- [x] **Step 1: Confirm the patched version and the entry point**

```bash
npm ls qs 2>&1 | head -20
```

Expected: `qs` arriving under `webpack-dev-server` (hence `npm start` only). The advisory's patched version is **6.16.0**. **If `npm ls` shows `qs` on a path that reaches `npm run build`, the posture's "reaches only `npm start`" claim is wrong** and Task 2(f) must be corrected before merge.

- [x] **Step 2: Add the override**

`package.json` already has:

```json
  "overrides": {
    "serialize-javascript": "^7.1.0",
    "uuid": "^14.0.0"
  }
```

Add `"qs": "^6.16.0"`, matching the existing shape (#177 set that precedent).

- [x] **Step 3: Regenerate the lockfile and verify**

```bash
npm install
npm ls qs
```

Expected: every `qs` resolves to ≥ 6.16.0. **Commit `package-lock.json` too** — an override without the regenerated lock changes nothing for anyone who installs from the lock.

- [x] **Step 4: Confirm the docs site still builds**

```bash
npm run build
```

This is the actual regression risk of the task: a transitive pin that breaks the site build. **If it fails, revert the override and record why** — the alert is medium, dev-only, and not worth a broken docs build.

- [x] **Step 5: Commit**

```bash
git add package.json package-lock.json
git commit -m "chore(deps): pin qs past its advisory, as serialize-javascript and uuid already are"
```

---

### Task 4: A guard for the two facts that can rot silently

**Files:** Create `tests/Rag.NET.RepoConventions.Tests/SecurityDocumentationTests.cs`.

Per §0.1 there is no gate on any of this. A test cannot check whether prose is *good*, but it can check that the disclosure policy still exists and that the posture still accounts for every security-adjacent package — the second being the claim most likely to go quietly wrong, because it breaks when someone **adds a package**, not when someone edits the document.

- [x] **Step 1: Write the tests**

Use `TestProject.FindRepositoryRoot()`, the helper the other guards in this project use (see `BuildGuardTests.cs:42`). Two facts:

```csharp
    [Fact]
    public void TheRepositoryCarriesADisclosurePolicy()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), "SECURITY.md");

        Assert.True(File.Exists(path), $"SECURITY.md is missing from {path}. 71 packages are published; a researcher with a finding needs a private channel.");
        Assert.Contains("advisor", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePostureAccountsForEverySecurityAdjacentPackage()
    {
        var root = TestProject.FindRepositoryRoot();
        var posture = File.ReadAllText(Path.Combine(root, "docs", "guide", "security.md"));

        var packages = Directory
            .GetDirectories(Path.Combine(root, "src"))
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith("Rag.NET.Security", StringComparison.Ordinal)
                        || name.StartsWith("Rag.NET.Mcp", StringComparison.Ordinal)
                        || name.StartsWith("Rag.NET.Api", StringComparison.Ordinal))
            .ToList();

        var missing = packages.Where(p => !posture.Contains(p!, StringComparison.Ordinal)).ToList();

        Assert.True(
            missing.Count == 0,
            "docs/guide/security.md's posture section names the packages whose security remit it "
            + "draws a boundary around. These exist under src/ and are not named, so the boundary "
            + "is stated over an incomplete list: " + string.Join(", ", missing));
    }
```

**The second test's value is that it fails when a package is added**, which is when the posture silently stops being complete and nobody is looking at the document. Derive the list from the filesystem, never hardcode it — a hardcoded list is a second thing to forget.

- [x] **Step 2: Run and confirm they fail first**

Run: `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release --filter "FullyQualifiedName~SecurityDocumentationTests"`

**Run this BEFORE Tasks 1 and 2 land if executing out of order** — a guard that has never failed is a guard that may not work. If both pass immediately, check you have not already written the documents.

- [x] **Step 3: Run the whole conventions suite**

Expected: Task 0's baseline plus 2.

- [x] **Step 4: Commit**

```bash
git add tests/Rag.NET.RepoConventions.Tests/SecurityDocumentationTests.cs
git commit -m "test(conventions): pin the disclosure policy and the posture's package list"
```

---

### Task 5: File what was found, do not fix it

- [x] **Step 1: File the prompt-injection documentation gap**

Per §0.2 and the Global Constraints. An issue saying: `docs/guide/security.md` documents three of four security feature families; the prompt-injection family — which `docs/reference/features.md` calls the primary RAG security risk — is absent from the page named "Security", and its detail lives only in the reference. Propose a feature section on the guide page. Label `documentation`.

**Record the issue number here**, and reference it from the posture section's family table so the gap is visible from the document it affects.

- [x] **Step 2: If anything else surfaced, file that too**

Especially anything in Task 2(e) — if the MCP transport's actual behaviour differs from "opt-in, unauthenticated by default", that is a finding about the code, not the document, and it is filed rather than fixed here.

---

### Task 6: Roadmap, review and PR

- [x] **Step 1: `docs/planning/ROADMAP.md`**, the Phase 6.2.38 block — record what the phase found, in its neighbours' style: §0.1's absent gate, §0.2's missing feature family, and the issue number from Task 5. **Do not change the `[status: ...]` marker** — `complete-phase` does that after the merge.

- [x] **Step 2: Correct the design document's §5** with a struck-through note rather than a deletion, the way 6.2.36 corrected its design's §4. The claim that documentation guards cover `docs/guide/` was wrong and the correction is instructive: it is a claim about tooling that nobody checked, in a document arguing for checking claims.

- [x] **Step 3: Run the suites.** Enumerate them:

```bash
dotnet test tests/Rag.NET.RepoConventions.Tests -c Release
npm run build
```

Only the conventions suite and the docs site are affected — no `src/` code changes in this phase. **If a `src/` file appears in `git diff main...HEAD --stat`, the no-behaviour-change constraint was violated.** Check before the PR, not after.

- [x] **Step 4: Run `pre-push-review`.** Record the verdict and report path here.

- [x] **Step 5: Open the PR.** Note that no security behaviour changed, link the Task 5 issue, and flag the `SECURITY.md` reporting-channel decision for the maintainer. Record the number here.

---

## Self-review

**Spec coverage** — design §4's three in-scope items: (1) `SECURITY.md` → Task 1. (2) the posture section → Task 2. (3) the `qs` override → Task 3. Out-of-scope items are enforced by Global Constraints and checked mechanically by Task 6 Step 3's `git diff` check. Tasks 4 and 5 are additions, both justified in §0 rather than by taste.

**Placeholder scan** — no TBDs. Task 1 Step 2 deliberately declines to invent an SLA and says why; Task 2(e) deliberately declines to assert the MCP default until the code is read. Both are instructions to check, not gaps.

**Type consistency** — only one code artefact, Task 4's test class. `TestProject.FindRepositoryRoot()` matches the helper used at `BuildGuardTests.cs:42`; confirm the exact accessibility and namespace when writing, since `CassetteSecretTests.cs` calls an unqualified `FindRepositoryRoot()` and the two may not be the same member.

**Known weakness, stated** — Task 4's guard checks *existence and coverage*, not correctness. Nothing can test whether the posture is true; §0.2 shows the real mitigation is enumeration against the code, which is a reading discipline the plan can require but not enforce.
