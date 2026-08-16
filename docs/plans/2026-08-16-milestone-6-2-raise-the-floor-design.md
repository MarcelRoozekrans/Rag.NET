# Phase 6.2 — Raise the Floor on Unit-Only Packages: the design decision

**Date:** 2026-08-16
**Phase:** 6.2, Milestone 6 (Hardening & v1.0 — Battle-Tested)
**Status:** design proposed, not yet approved

The ROADMAP entry for this phase deliberately states a question and its evidence rather than an
answer: *"The design decision is the phase's own first task."* This document is that task.

---

## 0. A correction to Phase 6.0's inventory, found before any design work

**Phase 6.0's allowlist claims coverage that does not exist**, and the claim is load-bearing: ten of
the thirty packages 6.2 owns are annotated *"the E2E suite"* or *"the E2E suite runs it against a
host; the ledger has not been told"* — wording that says the work is a **ledger correction**, not a
test. If that were true, a third of this phase would cost nothing.

It is not true. Measured 2026-08-16:

`tests/Rag.NET.E2ETests/Rag.NET.E2ETests.csproj` references exactly six projects — `Rag.NET`,
`Rag.NET.AnswerEngines`, `Rag.NET.Graph`, `Rag.NET.GraphRag`, `Rag.NET.VectorStores.PgVector` and
`Rag.NET.Testing`. **None of the ten is among them**, and no E2E source file mentions any of them.
Their only consumers are their own `*.Tests` projects.

| Package | 6.0 says | Actually |
|---|---|---|
| `Rag.NET.Api` | the E2E suite | own unit tests only — no `WebApplicationFactory`, no `TestServer` |
| `Rag.NET.Api.Client` | the E2E suite | own unit tests only |
| `Rag.NET.Api.Grpc` | the E2E suite | own unit tests only |
| `Rag.NET.Api.Grpc.Client` | the E2E suite | own unit tests only |
| `Rag.NET.Cli` | the E2E suite drives it | own unit tests only — no `Process.Start` |
| `Rag.NET.Diagnostics.AspNetCore` | the E2E suite | own unit tests only |
| `Rag.NET.Hosting` | the E2E suite | own unit tests only — no `HostBuilder` |
| `Rag.NET.Mcp` | the E2E suite | own unit tests only |
| `Rag.NET.Mcp.Tool` | the E2E suite | own unit tests only |
| `Rag.NET.Security.AspNetCore` | the E2E suite | **the one exception** — its own tests do use `TestServer` + `HostBuilder` |

So nine of the ten need real work, and the tenth is under-credited rather than over-credited.

**Why this matters beyond the arithmetic.** Phase 6.0's exit condition was that the ledger *"stops
being a feeling"*. Its own classification was a feeling: the E2E claim was written from memory of
what the suite covers and never checked against the csproj — the same failure mode as the two false
`features.md` claims Milestone 3 found by mechanical comparison, and the same shape as
`Rag.NET.GraphRag` sitting `✅ Done` and green with eight defects in it. The inventory phase built
to catch unverified claims shipped one.

This is a completion, not a reproach — the guard 6.0 built is exactly what surfaced it, because the
allowlist forced every package to name its owner in writing where it could be read and checked.
An empty ledger field would have hidden this.

**Consequence for this phase:** 6.2 is ~30 packages of real work, not ~20 with 10 free. The first
task below is the correction.

---

## 1. What the evidence says, restated

The ROADMAP lists what actually found defects in this project. Every one:

| Defect | How it was found |
|---|---|
| Late chunking inert from 1.1 to 3.7 | running the real thing for the first time |
| Default chunker: one chunk per word | embedding-cost arithmetic did not add up |
| `OnnxReranker` destroying 26% of every document as `[UNK]` | a stated prediction contradicted in a specific direction |
| Three `BeirDatasetCache` races + a Windows rename hazard | a cold cache, then a second operating system |
| Two false `features.md` claims | mechanical comparison of documentation to code |
| §0 above: nine packages' claimed E2E coverage | mechanical comparison of a claim to a csproj |

**Not one was found by adding another unit test to a package that already had some.** Four of the
six were found by *running something real*; two by *mechanically checking a claim against reality*.

The phase's candidate list (property testing, fuzz testing, differential testing) is not supported
by this evidence. Those techniques find defects in **algorithms with wide input spaces**. This
repository's defects are overwhelmingly in **integration seams** — the thing was never run, ran
against a fake that agreed with it, or was described in prose nobody checked. Property testing a
parser that is never handed a real file will not find that it cannot open one.

**Decision: the bar is one real run per package, not a new testing technique.**

---

## 2. The proposed per-kind bar

A package leaves bare `unit` by satisfying the row for its kind, or by carrying a
`<VerifiedByReason>` that says why it cannot. Each bar is stated so that it can be **false**.

### (a) Real-file parsers and chunkers — 8 packages
`Parsers.Archive`, `Parsers.Email`, `Parsers.Epub`, `Parsers.Html`, `Parsers.Office`, `Parsers.Pdf`,
`Chunking.CSharp`, `Chunking.Templates`

**Bar:** a committed real file of each format the package claims to support, parsed in a test, with
assertions on *shape* — not merely "did not throw". At minimum: extracted text is non-empty, its
length is within a stated band, and a known string from the document is present.

**Why shape:** the one-chunk-per-word defect passed every "did not throw" test in the repository.
A parser returning empty string for a real PDF is the failure this bar must catch, and only a
content assertion catches it.

**Files must be real and redistributable.** Public-domain or permissively-licensed sources only,
each with its provenance recorded beside it — this repository has been wrong about a licence three
times, so provenance is written down, not assumed.

### (b) Real-store packages — 3 packages
`Caching`, `Memory`, `Storage.Sqlite`

**Bar:** one real round trip through the real storage engine — a real SQLite file on disk, not
in-memory — asserting that what came out equals what went in, **and that it survives reopening the
store**. Persistence is the property a fake cannot have and the one users depend on.

### (c) Real-run plumbing — 7 packages
`DataProviders`, `Diagnostics`, `Diagnostics.AspNetCore`, `Mediator`, `Resilience`, `Telemetry`,
`Evaluation`, `Evaluation.Ragas`

**Bar:** the package's observable effect, observed once, through a real pipeline rather than a
fake — a real trace collected from a real `AddRagNet` pipeline; a real failure injected and the
real retry counted; a real metric exported and read back.

**`Resilience` gets the sharpest version:** inject a real failure and assert the retry *count* and
the *delay*, because a resilience policy that silently does nothing is indistinguishable from one
that works until it is needed.

### (d) Hosted-surface packages — 9 packages (§0's correction)
`Api`, `Api.Client`, `Api.Grpc`, `Api.Grpc.Client`, `Cli`, `Hosting`, `Mcp`, `Mcp.Tool`,
`Security.AspNetCore`

**Bar:** the surface started for real and called over its real transport — `WebApplicationFactory`
or `TestServer` for the HTTP and gRPC surfaces, the real MCP stdio transport for `Mcp.Tool`, and
the CLI invoked as a process for `Cli`. One round trip that returns a real answer, asserted.

`Security.AspNetCore` already meets this and needs only the ledger told.

**Why a real transport and not a direct method call:** every defect this repository found in a
hosted surface — MCP failing open, the API auth middleware not being required, the registration
order of decorators — lived in *composition*, which a direct call bypasses entirely.

### (e) Reason-only — 2 packages
`Abstractions` (types and interfaces only) and `Benchmarks.Quality` (the harness itself).

**Bar:** a `<VerifiedByReason>` and no test. `Abstractions` ships no behaviour to run;
`Benchmarks.Quality`'s correctness *is* the four-dataset parity agreement already pinned in
`BeirReproduction`, and its reason will name those figures. A reason is a completion here, not a
concession — but it is written down and machine-readable, which is the whole point.

---

## 3. What this phase explicitly does not do

- **It does not add unit tests to packages that already have them.** The evidence says that has
  never once worked in this repository.
- **It does not adopt property, fuzz or differential testing.** Not rejected on merit — unsupported
  by this codebase's defect record, and a technique adopted without evidence is the inert-guard
  shape this repository keeps deleting. Recorded for "Beyond v1.0" instead.
- **It does not make any package good.** Measured is the bar, per the milestone's scope line.

## 4. Order of work

1. **Correct 6.0's allowlist** (§0) — the nine wrong entries, so the work list stops lying. Cheap,
   and everything after it depends on the list being true.
2. **Real-file parsers** (a) — highest defect yield per the evidence, and entirely offline.
3. **Hosted surfaces** (d) — largest group, and where composition defects have actually lived.
4. **Real-store** (b) and **real-run** (c).
5. **Reasons** (e) — last, so that "cannot be exercised" is a conclusion rather than an opening move.

## 5. Exit condition

`PackagesAllowedToStayUnit` contains no 6.2-owned entry. Every one of the thirty is `container`,
`benchmark`, or carries a `<VerifiedByReason>`; `NoPackageStaysAtBareUnit` and its staleness twin
both pass without a 6.2 entry in the list.

## 6. Open question for the operator

**§2(a) requires committed real files** — a PDF with tables, a DOCX, an XLSX, a PPTX, an EPUB, an
EML and an MSG, a ZIP. These add roughly 2–5 MB to the repository permanently.

The alternative is generating them at test time with the same libraries that parse them, which is
**explicitly rejected here**: a file generated by the library under test is a fake wearing a real
file's extension, and would reproduce the exact failure mode this milestone exists to end. If the
size is unacceptable, the honest answer is `<VerifiedByReason>`, not a synthetic file.

**Recommendation: commit the real files.** 2–5 MB against a package set whose parsers have never
once been handed a real document is a good trade.
