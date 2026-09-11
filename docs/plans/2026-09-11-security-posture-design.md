# What the Project Claims About Its Own Security — design for Phase 6.2.38

**Origin:** item 3 of `STATE.md`'s *"What is actually open, in the order worth taking it"* list,
recorded 2026-09-07 and unscheduled until 2026-09-11. **The delay is itself the point**: the item
was recorded and then never placed in a phase, which is this repository's record-then-schedule rule
failing in its quieter direction. It was found again only because a session's claim that no local
work remained was challenged.

## 0. What is missing, and what is not

`docs/guide/security.md` exists and is good at what it does: 320 lines documenting three
**features** — RBAC on chunks, PII detection and redaction, and the audit log — with registration
snippets and composition rules.

What is absent is any statement of **posture**. A reader deciding whether to put this library in
front of their data gets three how-tos and no answer to "what does this project claim, and what does
it leave to me". Ten packages carry a security-adjacent remit — `Rag.NET.Security`,
`Rag.NET.Security.AspNetCore`, `Rag.NET.Security.Audit.Sqlite`, `Rag.NET.Mcp`,
`Rag.NET.Mcp.AspNetCore`, `Rag.NET.Mcp.Tool`, and the four `Rag.NET.Api*` packages — and no document
draws the boundary around them.

**And there is no `SECURITY.md`.** 71 packages have been live on nuget.org since 2026-08-11 with no
vulnerability-disclosure path. GitHub surfaces the absence on the repository page and in the
Security tab; a researcher with a finding currently has no private channel and would open a public
issue.

**This does not gate v1.0** and was not treated as though it did when triaged on 2026-09-07. It is
scheduled now because a v1.0 tag is a bad moment to still be missing it, not because anything is on
fire.

## 1. Two artefacts, because they answer different questions

**`SECURITY.md` at the repository root.** Supported versions, how to report privately, expected
response. Short by design — a disclosure policy that reads like a legal document does not get read.

**Reporting goes through GitHub private security advisories**, not an email address. It avoids
publishing a personal mailbox on a public repo, it gives the reporter a tracked channel, and it is
the mechanism GitHub's own "Report a vulnerability" button uses once enabled. *If the maintainer
prefers a mail address, that is a one-line substitution and their call to make.*

**A `## Security posture` section at the top of `docs/guide/security.md`** — not a second document.
A reader asking "is this safe" reaches for the security page; posture belongs above features in the
same place, so there is one destination rather than two that each look like the other's summary.

## 2. What the posture must state

### 2.1 RBAC fails open, and that is the sharpest thing here

`docs/guide/security.md` already says it, inside the RBAC section:

> Chunks that do not carry the key are world-readable and pass through for every caller.

That is a correct design choice — retrofitting RBAC onto an existing corpus would otherwise hide
every previously-ingested chunk the moment the feature is registered, which is a far worse failure
than the one it prevents. **But it is a fail-open default, stated in passing, two-thirds of the way
into a feature how-to.** A consumer who registers RBAC and assumes deny-by-default has a data
exposure and no reason to suspect it.

The posture states it plainly, with the reasoning, near the top. This is documentation of an
existing decision — **not a proposal to change the default**, which is out of scope (§4).

### 2.2 The boundary — what the library does not do

It filters retrieved chunks, redacts PII at ingest, and records an audit trail. It does **not**
authenticate end users, encrypt data at rest, manage keys, or secure the vector store it is pointed
at. Each of those belongs to the host application or the backing store, and saying so is the
difference between a boundary and an assumption.

### 2.3 The MCP write surface

`Rag.NET.Mcp` exposes tools that write. #198 closed the authorization gap with API-key
authorization (`McpApiKeyAuthorization`, `McpApiKeyEndpointFilter`), and it is **opt-in** — which
means an unconfigured MCP host is unauthenticated. The posture names that rather than leaving it to
be discovered from the registration extension's XML docs.

### 2.4 The dependency position

Five open Dependabot alerts on `main`, triaged 2026-09-07 and re-verified against the API
2026-09-11:

| Package | Severity | Where | Patch |
|---|---|---|---|
| `image-size` ×2 | high | Docusaurus build (`package-lock.json`) | none |
| `nltk` | high | Python comparison harness (`benchmarks/library-comparison-python/uv.lock`) | none |
| `qs` ×2 | medium | `webpack-dev-server`, reaching only `npm start` | 6.16.0 |

**None is in a shipped NuGet package's dependency closure.** A .NET consumer installing any of the
71 packages pulls none of it. Stating this is most of the value: a prospective user who sees "3
high" on the repository page needs the closure argument, and the absence of one reads as neglect.

## 3. The one fixable alert, folded in

`qs` is patched at 6.16.0 and `package.json` already carries an `overrides` block pinning
`serialize-javascript` and `uuid` past their own advisories (#177). Adding `"qs": "^6.16.0"` is the
same one-line move, and it has been available since 2026-09-07.

It is folded into this phase rather than left as a separate item because the posture document has to
describe the dependency position either way, and describing "one of these is fixable and unfixed"
while not spending the line is worse than either alternative.

## 4. Scope

In:

1. **`SECURITY.md`** at the repository root (§1).
2. **`## Security posture`** at the top of `docs/guide/security.md` (§1, §2).
3. **`"qs": "^6.16.0"`** in `package.json`'s existing `overrides` block (§3).

Out, and this boundary is the phase's main risk:

- **Any actual security change.** No new authorization, no defaults flipped, no RBAC semantics
  touched. §2.1 documents the fail-open default; it does not propose changing it.
- **If writing the posture surfaces something that ought to change, it gets filed, not fixed.** The
  same rule 6.2.36 followed when it found #544 and shipped a diagnostic instead of a fix. A phase
  whose remit is "write down what is true" must not quietly become one that alters what is true —
  that would put security changes into a PR reviewed as a documentation change.
- **The two unpatched alerts.** No patch exists; pinning is not available. They are described, not
  acted on.

## 5. Verifiability

Fully local. `Rag.NET.RepoConventions.Tests` carries documentation guards (`DocumentationQualityTests`,
`DocumentedConstraintGuardTests`, `FeatureClaimTests`) which already run against `docs/guide/`, so
the new prose is checked by the same gates as the rest. The `qs` override is verified by
`npm ls qs` resolving to 6.16.0 or higher and by the docs site still building.

**The claim this phase cannot verify by test** is that the posture is *complete* — that no security-
relevant default went unstated. That is a reading exercise, and the honest mitigation is to enumerate
the ten security-adjacent packages and account for each rather than writing from memory of what the
library does.
