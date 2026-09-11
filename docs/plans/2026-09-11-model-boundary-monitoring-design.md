# The Boundary Rag.NET Does Not Watch — design for Phase 6.2.39

**Origin:** 6.2.38's posture section states what the library defends and what it leaves to the host.
Writing it made one omission visible: **Rag.NET inspects nothing on the way back from the model.**
This phase documents the composition that closes that, and takes on no code and no dependency to do
it.

## 0. The gap, stated precisely

Rag.NET's security features act at four points, all of them *before* the model is called:

| Point | Mechanism |
|---|---|
| Ingest | `IChunkSanitiser` — PII redaction, chunk sanitisation |
| Pre-retrieval | `IQuerySanitiser` |
| Retrieval | `IRetrievalGuard` — RBAC, trust level, regex |
| Prompt assembly | `PromptHardeningAnswerEngineDecorator` |

Nothing acts *after*. The one thing that looks like it does — `IConfidenceScorer` — answers a
different question: it scores whether a sentence is **supported by the retrieved context**, on a
0..1 scale, and fails open at `1.0` when it cannot score. That is a groundedness signal for RAG
quality. It is not an inspection of the response for a credential, a piece of PII, or an
exfiltration attempt that the model saw in a chunk and repeated.

**So a chunk containing a secret that survives ingest-time redaction can be summarised back to a
user, and no part of this library looks at that.** The posture now says the library does not encrypt
at rest or authenticate users; this is the same class of honest boundary and is currently unstated.

## 1. What this phase is, and is not

**Is:** one section in `docs/guide/security.md` describing the `IChatClient` decoration pattern, what
a model-boundary monitor adds, what it duplicates, and a worked example.

**Is not:** a package reference, an integration package, an abstraction, or a dependency. **Rag.NET
takes on nothing.** The composition already works because both sides sit on
`Microsoft.Extensions.AI.IChatClient` — Rag.NET consumes one from DI, a monitor wraps one. Nothing
needs to change on either side for a consumer to use both, which is the whole reason this is
documentation rather than code.

That distinction is the phase's main risk. A reader of the roadmap could reasonably expect "integrate
AI.Sentinel" to mean a `Rag.NET.Security.Sentinel` package. It deliberately does not, for a reason
the repository has already written down: `SecurityPackageWeightTests` exists because one file put
SQLite and a native binary on every `UseRbac` consumer. AI.Sentinel brings **thirteen `ZeroAlloc.*`
packages** plus `Microsoft.Extensions.AI`. That must never enter `Rag.NET.Security`, and a separate
integration package is post-1.0 work with its own package ID to own.

## 2. Naming the package, and disclosing the authorship

The worked example names **AI.Sentinel** specifically, and carries a one-line note that it is written
by the same author as Rag.NET.

**Decided 2026-09-11 by the operator, over three alternatives**: naming it without disclosure,
describing the pattern generically without naming any product, and not documenting it at all.

The disclosure costs one sentence and pre-empts the reasonable reaction of a reader who notices both
packages share an author and wonders whether the section is advertising. **That matters more here
than it would elsewhere**: this lands in the security guide, immediately after a posture section
whose entire value is that a reader believes it. An undisclosed self-recommendation discovered later
would be re-read backwards onto everything above it.

The generic alternative was rejected for the ordinary reason generic advice is: a reader told "wrap
your `IChatClient` in a monitor" and left to find one mostly does not.

## 3. What the section must say, including the unflattering parts

**The boundary picture**, as a table — Rag.NET's four pre-model points against a monitor's two
model-boundary directions.

**The composition**, as code: decorate the `IChatClient` before Rag.NET's DI consumes it. Verified
working (§4).

**The overlap, stated rather than glossed.** Both do prompt-injection detection, by different means —
Rag.NET's regex/LLM sanitisers on the query and its retrieval guards on the chunks, a monitor's
detectors at the boundary. Running both is defence in depth **or duplicated cost**, depending on
configuration. A section that implies the two are purely additive would be selling rather than
documenting, which §2's disclosure exists to avoid.

**The version caveat, measured and dated.** AI.Sentinel 2.0.1 targets `net8.0`/`net9.0` while Rag.NET
targets `net10.0`, and it builds against `ZeroAlloc.Mediator` 4.1.4 and `ValueObjects` 1.7.1 where
Rag.NET pins 5.0.1 and 2.0.5. NuGet resolves to the higher version, so a consumer of both runs
AI.Sentinel against majors it was not compiled against.

## 4. This was measured, not assumed

A throwaway spike on 2026-09-11 built a `net10.0` project forcing Rag.NET's exact pins against
AI.Sentinel 2.0.1 and ran it:

- the container builds,
- `IChatClient` resolves to `SentinelChatClient`,
- **55 distinct detectors resolve from DI and construct**,
- a scan executes and returns without `MissingMethodException` or `TypeLoadException`.

**What the spike does not prove**, and the section must not overclaim: it exercised construction and
the scan path, not every detector's internals. A detector reaching a changed Mediator or ValueObjects
API only on an alert path was never reached. The honest claim is *"verified to load and run on
2026-09-11 against these versions"*, with the versions named so the claim expires visibly rather than
silently.

**One observation from the spike that is not this repository's to fix**: a blatant injection scanned
clean in a bare configuration with all five injection-shaped detectors registered. Almost certainly a
missing `EmbeddingGenerator` — AI.Sentinel's own quick start sets one. It is mentioned because the
section should tell readers to verify detection against their own configuration rather than assume
registration equals protection. Registration that silently protects nothing is this milestone's
recurring defect shape, and the advice costs a sentence.

## 5. Scope

In:

1. One section in `docs/guide/security.md`, after the posture, before the feature sections.
2. A worked `IChatClient` composition example naming AI.Sentinel, with the authorship disclosure.
3. The overlap statement, the measured version caveat, and the verify-your-own-config advice.

Out:

- **Any dependency, package reference or integration code.** Nothing enters `src/`.
- **A `Rag.NET.Security.Sentinel` integration package.** Post-1.0, if ever; §1 records why.
- **Fixing or investigating AI.Sentinel's own behaviour.** §4's clean-scan observation is reported to
  its author, not chased here.

## 6. Verifiability

The composition example must compile and run — the spike already demonstrates the shape, and the
example in the document should be the shape that was actually executed rather than a plausible
rendering of it.

`SecurityDocumentationTests` (6.2.38) already guards this page's package list and its RBAC quote; a
new section does not disturb either. The docs site build validates the section's internal links.

**The claim that cannot be tested** is the version caveat, which is true of a third-party package on
its own release schedule. Dating it and naming the versions is the mitigation: a reader can see when
it was checked and against what, which a bare "works fine" cannot offer.
