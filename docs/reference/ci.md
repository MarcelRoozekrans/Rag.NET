---
id: ci
title: CI and Test Tiers
sidebar_position: 3
---

# CI and Test Tiers

Rag.NET has 64 test projects and they do not all want the same thing. Some need nothing but a
runtime; some start Docker containers; one downloads two gigabytes of language model; five need
credentials or large local assets that a plain checkout does not have. Running them all on every
push would be slow and flaky. Running only the easy ones would be a lie.

So each test project **declares what it needs**, and the workflows select on those declarations.
Nothing in a workflow file names a project.

## The three tiers

A test project is in exactly **one** tier, determined by what its `.csproj` declares.

| Tier | Declares | Where it runs | Gates a merge? |
|---|---|---|---|
| **Fast** | nothing | `ci.yml`, every push and pull request | **Yes** |
| **Docker** | `<RequiresDocker>true</RequiresDocker>` | `ci.yml`, every push and pull request | **Yes** |
| **LLM** | `RequiresDocker` **and** `<RequiresLlm>true</RequiresLlm>` | `nightly.yml`, plus opt-in | **No — never** |

The fast tier is the large majority. The Docker tier is the Testcontainers suites — the vector
stores, the Service Bus ingestion tests, and the integration suites that use `PgVectorFixture` or
`QdrantFixture`. Both gate, because both are deterministic and `ubuntu-latest` has a Docker daemon.

Docker suites are **Linux-only**. The Windows runners have no Linux Docker daemon, so Testcontainers
cannot work there; `ci.yml`'s `build-test` job is an OS matrix (`ubuntu-latest` and
`windows-latest`) since Phase 4.0, and the Docker tier runs only on the Linux leg.

**"Gates" in that table means the failure is real and fails the run** — no `continue-on-error`
anywhere in `ci.yml` — **and, for both tiers, that a merge is mechanically blocked** (measured
2026-08-03, correcting this page, which said no branch protection existed): the repository's
**`Main` ruleset** requires both matrix legs, `build-test (ubuntu-latest)` and
`build-test (windows-latest)`, as status checks on the default branch, and the Docker tier runs
inside the Ubuntu leg, so both tiers block. Since 2026-08-11 it also requires **`pack-validate`**
and **`commitlint`** — Phase 6.3's first checklist item, done before either release dispatch,
because until then the only guard on the whole packaging surface could go red without blocking
anything. One honest limit remains: repository admins can always bypass the ruleset.

The LLM tier is one project, `Rag.NET.E2ETests`. It pulls `nomic-embed-text` and `llama3.2:1b`, and
its assertions are text a model wrote — Phase 2.1 measured one such assertion failing roughly **1 run
in 11**. That is not a defect, it is a model choosing different words, and a required check that
fails on it teaches people to press re-run instead of read. So it reports and never blocks, even when
you asked for it.

## Opting a pull request into a nightly job

Two labels, one per job, because the jobs cost very different amounts and are wanted for different
reasons:

| Label | Runs | Blocks the merge? |
|---|---|---|
| **`run-llm`** | the Ollama end-to-end suite — pulls ~2 GB of models | **No**, never, by design |
| **`run-secrets`** | the env-gated suites — Document Intelligence, ONNX embedding and late chunking, and the SciFact and ArguAna retrieval-quality parity runs | **Not yet** — it fails loudly, but it is not in the `Main` ruleset's required checks |

On `run-secrets`: the job *gates* in the sense that a failure is a real failure and is reported as
one — no `continue-on-error` anywhere in it. It does not *block* anything today: the `Main`
ruleset requires the two `build-test` legs, `pack-validate` and `commitlint`, and this job is not
among them. If a fifth is ever added, this is the nightly job to require; the `llm` one never is.

Use `run-llm` when you have changed the answer engine or a retrieval path and want to see the
end-to-end result before merging. Use `run-secrets` when you have touched PDF OCR, Document
Intelligence, ONNX embedding, or anything on the retrieval path that could move the
[retrieval-quality parity number](./retrieval-quality.md) — those suites are deterministic, so a
failure is a real regression rather than a model choosing different words.

They are separate labels on purpose: a PR touching the PDF parser has no use for a two-gigabyte
model download, and a single shared label would have made the cheap job unreachable without paying
for the expensive one.

`workflow_dispatch` runs both, off any branch, ad hoc.

**The label triggers on `labeled`, not on `synchronize`.** Pushing new commits to a pull request
that already carries `run-llm` or `run-secrets` does **not** re-run the job: no `labeled` event
fires, so nothing starts, and the newest result on the PR is from the commit that was current when
the label went on. To re-run against new commits, remove the label and add it again. That is
deliberate — a nightly job that re-ran on every push would be neither nightly nor opt-in — but it is
easy to misread a stale green tick as covering the latest commit.

## The secrets overlay

Five projects contain tests that need credentials or large local assets:

| Project | Reads | Where the value comes from |
|---|---|---|
| `Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests` | `RAGNET_DOCINTEL_ENDPOINT`, `RAGNET_DOCINTEL_KEY` | repository secrets, never yet configured; provisionable by the fenced procedure below |
| `Rag.NET.Embeddings.Onnx.Tests` | `RAGNET_ONNX_EMBED_MODEL`, `RAGNET_ONNX_EMBED_VOCAB` | **downloaded by the job** |
| `Rag.NET.Chunking.IntegrationTests` | `RAGNET_ONNX_EMBED_MODEL`, `RAGNET_ONNX_EMBED_VOCAB` | **downloaded by the job** |
| `Rag.NET.Benchmarks.Quality.IntegrationTests` | those two, plus `RAGNET_BEIR_CACHE`, `RAGNET_BEIR_LONG_RUNS` and `RAGNET_ONNX_RERANK_MODEL`/`_VOCAB` | downloaded, plus a runner temp path; the last three are deliberately **never supplied by the job** — see below |
| `Rag.NET.Parsers.Pdf.Tests` | `RAGNET_TESSDATA` | repository secret in the nightly (where it reaches nothing — see below); supplied locally by the fenced OCR procedure below |

Each of those tests calls `Assert.Skip` when its variable is absent, so the projects are safe
anywhere and skip on a normal developer machine. They declare
`<RequiresSecrets>true</RequiresSecrets>`, and the `env-gated` job in `nightly.yml` selects on that
property and supplies the values.

**Only two of these are actually secret.** That distinction was missing for two phases and it cost
the whole point of the job. `RAGNET_ONNX_EMBED_MODEL` and `RAGNET_ONNX_EMBED_VOCAB` are *paths to
files*; every reader calls `File.Exists` on them. Held as repository secrets they named a path that
no step ever created on a fresh runner, so the ONNX suites skipped every night and the job went
green. `RAGNET_BEIR_CACHE` is a scratch directory, and it was not supplied at all. The job now
downloads `all-MiniLM-L6-v2` from Hugging Face at a pinned revision, checks it against a SHA-256,
caches it between runs, and points all three variables at runner paths — and **fails** if the files
are not there afterwards, because there is no fork-safety argument for skipping a test whose input
the job could have fetched.

**`RAGNET_TESSDATA` reaches nothing in CI, and that is now a decision with a runnable procedure
rather than an open defect.** Its only reader, the real-Tesseract OCR test, is inside
`#if ENABLE_OCR`, which no workflow build defines — deliberately: the published
`Rag.NET.Parsers.Pdf` package compiles the Tesseract engine out so consumers do not carry its
native payload (Azure Document Intelligence is the packaged OCR engine), and CI builds what it
ships. The gated test is compiled and run **locally** by the procedure below, which was executed
green on 2026-08-03 — the test's first run anywhere:

```bash
# Compile the OCR flavour (every default build compiles Tesseract out) and run the gated test.
dotnet build tests/Rag.NET.Parsers.Pdf.Tests -c Release -p:EnableOcr=true
mkdir -p tessdata && curl -fsSL -o tessdata/eng.traineddata \
  https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata
RAGNET_TESSDATA="$PWD/tessdata" dotnet test tests/Rag.NET.Parsers.Pdf.Tests --no-build -c Release \
  --filter "FullyQualifiedName~OcrFallback_RealTesseract"
```

The nightly still supplies the secret; it is harmless there and starts mattering only if someone
adds the OCR build flag and Tesseract's native binaries to that job — which the packaging
decision above argues against rather than toward.

**The Document Intelligence live suite is satisfiable by the procedure below — and has still
never run.** Both facts matter, so both are stated. `RAGNET_DOCINTEL_ENDPOINT` and
`RAGNET_DOCINTEL_KEY` are mapped from repository secrets that have never been configured, so
`AzureDocumentIntelligenceLiveTests` has skipped on every run to date; its offline coverage is
hand-written WireMock cassettes that nothing has confirmed against the real service, and the
recorded-responses work that fixes *that* is Phase 6.1. What is no longer true is that the gate is
satisfiable nowhere: any maintainer with an Azure subscription can run the suite — the **F0 free
tier (500 pages/month) exists**, so satisfying the gate does not even require spend, though a paid
tier bills per page and the suite submits real documents. The procedure, not yet executed by
anyone:

```bash
# Once: an Azure Document Intelligence resource (the resource kind is still FormRecognizer).
az cognitiveservices account create --name <name> --resource-group <rg> \
  --kind FormRecognizer --sku F0 --location westeurope --yes
endpoint=$(az cognitiveservices account show --name <name> --resource-group <rg> \
  --query properties.endpoint -o tsv)
key=$(az cognitiveservices account keys list --name <name> --resource-group <rg> \
  --query key1 -o tsv)

# The live suite: skips without the variables, runs and submits real pages with them.
RAGNET_DOCINTEL_ENDPOINT="$endpoint" RAGNET_DOCINTEL_KEY="$key" \
  dotnet test tests/Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests --no-build -c Release

# Nightly coverage rides on the same two values as repository secrets:
gh secret set RAGNET_DOCINTEL_ENDPOINT --body "$endpoint"
gh secret set RAGNET_DOCINTEL_KEY --body "$key"
```

**This is an overlay, not a fourth tier.** All five are fast-tier projects: they run in `ci.yml` on
every push (skipping the gated tests) *and* in `nightly.yml` with the values supplied. A project is
in one tier and may appear in more than one workflow.

## What the nightly actually measures, and what it does not

`Rag.NET.Benchmarks.Quality.IntegrationTests` describes **three** BEIR datasets — SciFact, FiQA
and ArguAna — under **ten** protocols. Two are measurements of Rag.NET against a published
figure or against itself: *parity* (one chunk per document, truncated at 256 tokens, the only
protocol comparable to a published figure) and *real* (Rag.NET's own chunking, max-pooled back
to documents, compared only to our own parity run). Three are the **ablation cells** Phase 3.14
added and Phase 3.15 measured — *+BM25 hybrid*, *+HyDE* and *+reranker*, each a variation on the
parity corpus. Five belong to the [library comparison](./library-comparison.md): the
run-file *comparison control* and the *Semantic Kernel*, *LangChain*, *LlamaIndex* and
*Haystack* entrant rows. Counting parity per separator leg the way this table always has, that
is **35 cases** — this page said "eleven" until 2026-08-03, a count from before the ablation
and comparison rows existed — and the nightly still runs **seven** of them.

| Case | Cost when last timed | In the nightly? |
|---|---|---|
| SciFact parity, both separators | ~5 min each, cold | **Yes** |
| ArguAna parity, both separators | ~4 min each cold, 50 s warm | **Yes** |
| Chunk-shape checks, all three datasets | ~1.5 s for all three | **Yes** — no model needed |
| FiQA parity | 1 h 11 m, one separator (FiQA has no titles) | No — opt-in |
| SciFact real | 10 m 43 s measured 2026-07-31, parity vectors warm; fully cold is derived, untimed | No — opt-in |
| ArguAna real | 11 m 7 s measured 2026-07-31, parity vectors warm (28 min cold pre-3.16) | No — opt-in |
| FiQA real | 1 h 4 m measured (59.8 min real leg, parity vectors warm) | No — opt-in |
| +BM25 hybrid cells (×3) | SciFact ~1 m 50 s, ArguAna ~2 m, FiQA ~58 m — warm; cold adds the parity embedding price | No — opt-in |
| +HyDE cells (×3) | ~1 m 30 s – ~3 m 49 s warm; needs the generated hypothetical cache, which no fresh runner can have | No — opt-in |
| +reranker cells (×3) | SciFact ~4 m, FiQA ~4 m warm, ArguAna ~28 m; needs the locally provisioned cross-encoder (below) | No — opt-in |
| Comparison controls (×3) | seconds-to-minutes warm; FiQA's never run | No — opt-in |
| Semantic Kernel entrants (×3) | seconds warm; FiQA's never run | No — opt-in |
| LangChain / LlamaIndex / Haystack entrants (×9) | minutes each producing the Python run file; FiQA's never run | No — opt-in, and they need the pinned Python harness's run files |

`BeirRunBudget` is the authority on every figure — it records each dataset × protocol pair's
measured (or honestly derived) cost and **throws on a pair nobody has timed**, so the code's own
inventory cannot drift the way this page's did.

The `env-gated` job has `timeout-minutes: 120` and spends part of that restoring, building the whole
solution and running the four other `RequiresSecrets` projects. FiQA's real leg alone is longer than
the job's entire budget, and its parity leg would consume most of what is left, so a job that ran
everything would not report a slow parity number — it would **time out and report nothing**, which
is the same silence supplying `RAGNET_BEIR_CACHE` was meant to end.

So the expensive cases are gated behind `RAGNET_BEIR_LONG_RUNS`, which `nightly.yml` never sets.
Each one skips with a message naming itself, its measured cost and the exact command that runs it —
the job's presence report also prints the variable as unset, so a log reader is told the long runs
were off rather than left to infer it from a test count. To run one:

```bash
RAGNET_BEIR_LONG_RUNS=1 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build \
  --filter "DisplayName~BeirRealChunkingTests&DisplayName~arguana"
```

**`RAGNET_BEIR_RUN_INDEX` — repeat runs, for the cost measurement only.** Phase 5.1 publishes no
cost figure from a single run: `CostReproducibility` compares repeats and refuses a figure whose
runs disagree beyond its bar. Each entrant therefore has to be measured more than once, and the
variable says which repeat this invocation is writing, so run 2's timings sidecar lands beside
run 1's instead of overwriting it. It defaults to `1`, and **throws** on anything that is not a
positive integer rather than falling back — a silent fallback would overwrite run 1 with what the
operator meant to be run 2, and the gate would then compare a run against itself and report
perfect agreement.

`nightly.yml` does not set it, deliberately: one run per night is a run the gate cannot judge, and
a nightly that produced ungated figures would be worse than one that produces none. This is a
developer procedure, run twice by hand on one machine in one session, which is what the design's
comparability rule requires anyway. To measure both .NET entrants twice:

```bash
for i in 1 2; do
  RAGNET_BEIR_LONG_RUNS=1 RAGNET_BEIR_RUN_INDEX=$i \
    dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build \
    --filter "FullyQualifiedName~BeirComparisonControlTests"
done
```

The Python entrants take the same index as `--run-index`:

```bash
for i in 1 2; do
  uv run python run_entrant.py scifact langchain --run-index $i
done
```

**`RAGNET_COST_MATRIX_RUNS` — gating a finished sweep and dumping the publishable tables.** Once
every cell has been measured `N` times, `CostMatrixDumpTests` runs `CostReproducibility` over all
of them and prints the two tables the roadmap's §6 authorised: latency cross-ecosystem, index
construction per ecosystem. The variable says how many repeats to read, so it is also what refuses
a dump the data cannot support — below `2` it **throws** rather than skipping, because there is no
one-run table to fall back to:

```bash
RAGNET_COST_MATRIX_RUNS=3 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests \
  --no-build --filter "DisplayName~DumpsTheGatedCostMatrix"
```

A cell whose sidecars are missing or whose spread is past the bar **fails** and is named, along
with every other failing cell, instead of dropping out of the table — a matrix that quietly prints
eleven rows where twelve were measured reads exactly like a complete one.

**The reranked ablation cells additionally need the cross-encoder, which the nightly deliberately
does not provision.** It used to: the job fetched, SHA-256-checked and cached the ~87 MB
`cross-encoder/ms-marco-MiniLM-L6-v2` export on every cold run — and both genuine runs on the
record showed it feeding nothing, because every reader sits behind `RAGNET_BEIR_LONG_RUNS`, which
that job never sets. Phase 4.1 removed the provisioning rather than keep paying for an input no
test consumes; the pins and the digest checks moved here, unchanged. If a checksum fails, do
**not** edit the checksum to match — check whether upstream republished the revision first:

```bash
# cross-encoder/ms-marco-MiniLM-L6-v2, pinned (recorded 2026-08-01: the revision is main's
# targetCommit from the HF API; the model SHA-256 is its Git LFS oid, re-verified locally; the
# vocab SHA-256 was computed locally — it is byte-identical to all-MiniLM-L6-v2's, expected,
# since both tokenize with the standard BERT uncased WordPiece vocabulary).
revision=c5ee24cb16019beea0893ab7796b1df96625c6b8
dir="$RAGNET_BEIR_CACHE/models/ms-marco-MiniLM-L6-v2"
mkdir -p "$dir"
curl -fsSL -o "$dir/model.onnx" "https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2/resolve/$revision/onnx/model.onnx"
curl -fsSL -o "$dir/vocab.txt"  "https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2/resolve/$revision/vocab.txt"
echo "5d3e70fd0c9ff14b9b5169a51e957b7a9c74897afd0a35ce4bd318150c1d4d4a  $dir/model.onnx" | sha256sum -c -
echo "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3  $dir/vocab.txt"  | sha256sum -c -

RAGNET_ONNX_RERANK_MODEL="$dir/model.onnx" RAGNET_ONNX_RERANK_VOCAB="$dir/vocab.txt" \
RAGNET_BEIR_LONG_RUNS=1 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build \
  --filter "DisplayName~UnderCrossEncoderRerank&DisplayName~scifact"
```

One more opt-in gate lives in the same project and costs seconds, not hours:
`RAGNET_IDENTITY_BATTERY_DIR` points `IdentityBatteryDumpTests` at the directory
`identity_check.py --write-battery` filled with the library comparison's embedder-identity battery
inputs, and the fact dumps the .NET-side vector for each one (the full procedure is in
[Library Comparison](./library-comparison.md#reproducing-it)):

```bash
RAGNET_IDENTITY_BATTERY_DIR="$RAGNET_BEIR_CACHE/identity-battery" \
  dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests \
  --filter "DisplayName~DumpsEachBatteryInputsVector"
```

The split keeps the *parity* number under nightly regression guard on two datasets, which is the
number the milestone exists to protect and the only one that can be checked against a published
figure at all. **What it gives up is stated rather than buried:** no chunk-to-document max-pooling
runs against a corpus in the nightly any more. The cheap chunk-shape checks still run there and
still catch a chunker that stopped chunking; pooling itself is covered by `DocumentRankingTests`'
fixture and by an opt-in run. The costs behind every row above live in `BeirRunBudget`, which throws
rather than guesses when a dataset is added without being timed.

Every figure is the **cold** cost, because `RAGNET_BEIR_CACHE` is `RUNNER_TEMP/beir` — a fresh
directory every night. The embedding cache makes a developer's second run much faster and saves the
nightly nothing at all. Note also that it only caches *embeddings*: retrieval and scoring are paid
in full on every run, which is why four warm parity cases still take about five minutes.

**The `env-gated` job gates.** Unlike the LLM tier these suites are deterministic — the same model
over the same corpus produces the same vectors — so a failure is a regression. The honest caveat is
now narrower than it was: when the *Document Intelligence* secrets are not configured, that one
suite skips and the job still passes. A step prints which variables are present so a log reader can
tell a real pass from a run in which nothing executed. Forks never receive secrets, and that is
deliberate: an unset secret is not a failure. A missing model file is.

## Adding a test project

The rules are enforced by `tests/Rag.NET.RepoConventions.Tests`, which reads the repository off disk
and fails the build when a declaration and reality disagree. Both directions, so a stale declaration
fails just as loudly as a missing one.

**First, add it to `Rag.NET.slnx`.** This is not bookkeeping. `ci.yml` builds the solution once and
then runs each project with `--no-build`; a project the solution does not list is never built, and
on a CI checkout — where `obj/` is empty — `dotnet test --no-build` against it exits 0 having
printed nothing and run nothing. That is exactly what happened to
`Rag.NET.WebSearch.Tavily.Tests`: four real tests, a correct tier, and not one of them ever ran.
`EveryTestProjectIsInTheSolution` now fails naming any project that is missing, and each tier loop
independently fails a project whose test assembly is not on disk — the guard covers every reason a
project might not have been built, not only this one.

**If your new suite starts a container** — a `Testcontainers.*` package reference, or a container
fixture from `tests/Rag.NET.Testing` — it must declare:

```xml
<PropertyGroup>
  <RequiresDocker>true</RequiresDocker>
</PropertyGroup>
```

Forget it and `EveryProjectThatStartsAContainerDeclaresRequiresDocker` fails naming your project.
Declare it without starting a container and the same test fails the other way.

**If your new suite reads a `RAGNET_*` environment variable** it must declare:

```xml
<PropertyGroup>
  <RequiresSecrets>true</RequiresSecrets>
</PropertyGroup>
```

`EveryProjectThatReadsASecretDeclaresRequiresSecrets` enforces both directions the same way. The
declaration only gets `nightly.yml` to *select* your project; something still has to supply the
value, or the test skips there exactly as it does locally. If the value is a real credential, add it
to the repository secrets. **If it is a file path or a directory — a model, a vocab, a cache — add a
step to the `env-gated` job that creates it, and do not make it a secret.** A secret cannot put a
file on a runner, and a variable pointing at a path that does not exist skips silently and green.

**If it needs a model as well as a container**, add `<RequiresLlm>true</RequiresLlm>` alongside
`RequiresDocker` — the Ollama fixture is a container, so `RequiresLlm` without `RequiresDocker` is a
contradiction the conventions tests reject. That guard runs the other way too: a project using
`OllamaFixture` **must** declare `RequiresLlm`. Without it the suite lands in the Docker tier, which
gates on every push, and the whole reason the LLM tier is nightly and advisory is undone by a single
deleted line.

**If it needs none of these**, do nothing. It lands in the fast tier, which is the point of the
default: forget a declaration and the project fails loudly for want of a daemon rather than quietly
vanishing from CI. Being *in the solution* is the one thing that is not a default — see above, and
it is the one omission that used to vanish silently rather than fail.

## Why declarations rather than a list in the workflow

A list of project names in a workflow file fails silently in the one direction that matters. Add a
Testcontainers suite, forget to update the list, and it never runs again — with nothing anywhere to
notice, and a green tick every time. Self-declaration inverts that failure. It is also why the
conventions tests assert that the workflows still *select on the properties*: replace a property
query with a list of names and the guard tests become decorative.

Those assertions name the selection pipelines verbatim, and they read the workflow with its comment
lines stripped first. The earlier version asserted only that the string `RequiresDocker` appeared
somewhere in `ci.yml` — where it appears four times in prose — so replacing the entire tier
selection with a hardcoded list passed it. A guard that a comment can satisfy is not a guard.

## Packing, the rehearsed push, and the one that is gated

`ci.yml` has a second gating job besides the test matrix: `pack-validate`, on `ubuntu-latest`.
Every run it derives the version from git history (see [Versioning](#versioning-gitversion-and-the-release-tooling)
below), packs the 70 shippable packages with it (`dotnet pack Rag.NET.slnx -c Release -o
artifacts/packages -p:Version="$PACKAGE_VERSION"` — 70 `.nupkg` plus 70 `.snupkg`), validates
them with `tests/Rag.NET.PackageValidation.Tests` — the only guard there is, because `dotnet
pack` enforces almost none of its own metadata — and then **pushes every package to a local
directory feed, twice, asserting per file that each one arrived**.

The rehearsal exists because the push to nuget.org cannot run before Phase 6.3, and this
repository keeps finding defects in exactly such never-run paths: the rewritten `nightly.yml`
failed on its first-ever execution, the OCR test is not skipped but not compiled, and three
env-gated guards were green by skipping. So everything except the credential and the endpoint
runs on every push — the command, its arguments, the glob that selects the packages, and what a
rerun does.

Three things the rehearsal measured (2026-08-03), each pinned by a workflow assertion:

- a directory feed delivers flat, one file per package, and the glob push delivers all of them;
- duplicates against a directory feed are **silently overwritten** — it cannot produce the 409
  that `--skip-duplicate` exists to tolerate, and the CLI warns the flag is unsupported for this
  push type — so the second push proves a rerun is harmless, not that the skip works;
- a `.snupkg` push to a directory feed is a **complete silent no-op**: exit 0, no output,
  nothing delivered. The workflow attempts it anyway and asserts non-arrival, so the day NuGet
  changes that behaviour the run fails and the rehearsal widens to cover symbol packages.

`--skip-duplicate` is the deliberate duplicate policy for the real push: nuget.org never forgets
a published version, so a push that dies partway through 70 packages must be re-runnable, and
without the flag the retry fails on the first package that already arrived. Idempotent is the
only retry-safe shape against an append-only feed.

### The gated nuget.org push

The `publish-nuget` job in `ci.yml` is fully wired and runs nowhere before Phase 6.3. The gate,
recorded to the standard `TestGateTests` holds every other gate in this repository to:

| | |
|---|---|
| **Name** | `publish-nuget`, a job in `ci.yml` |
| **Condition** | a manual `workflow_dispatch` on `main` with `publish_to_nuget=true`, plus a Trusted Publishing policy on nuget.org and the `NUGET_USER` repository variable — the job fails loudly when no key is minted rather than 401ing |
| **Satisfied by** | the procedure below, runnable by any maintainer with admin on the repository; Phase 6.3 executes it |

```bash
# Once: the nuget.org account name the Trusted Publishing policy belongs to. Not a secret —
# it is a username, and holding it as a variable keeps it visible and editable.
gh variable set NUGET_USER
# The release: dispatch CI on main with the publish input. The full test matrix and
# pack-validate run first on that same commit, and publish-nuget refuses to start until
# both are green.
gh workflow run ci.yml --ref main -f publish_to_nuget=true
```

**One step in this procedure is not a command, and it is the one that fails last.** Trusted
Publishing needs a policy created on nuget.org itself — under *Account → Trusted Publishing* —
naming this repository, the `ci.yml` workflow and the owner. Nothing in the repository can
create it, assert it or detect its absence: the workflow runs, `NuGet/login` requests a token,
and nuget.org declines. Create it before the first dispatch.

**Why Trusted Publishing rather than a stored key.** NuGet deprecated long-lived API keys in
favour of short-lived tokens minted per run from the workflow's OIDC identity. The practical
difference is that there is no credential in the repository to leak, rotate or forget: the token
this job receives lasts minutes and is bound to this repository and workflow. Raised by StefH on
issue #87, tracked as #89.

**The push command did not change**, deliberately. Only the origin of `$NUGET_API_KEY` did — a
secret before, a step output now — so the command Phase 4.1 rehearsed against a local feed on
every push is still the command that runs on release day. `WorkflowWiringTests` pins the command,
the login action, the step output it reads and the `id-token: write` permission together, because
a push command that stays stable while its credential changes underneath is exactly the drift
nothing else would notice.

> **Delete the `NUGET_API_KEY` secret once a Trusted Publishing push has succeeded.** It is no
> longer read by anything, and a retired credential that still works is how these migrations
> stall — the old mechanism stays usable, so nothing forces the new one to be correct.

**`TestGateTests` does not cover this gate, and that is stated rather than assumed away.** That
guard scans *test* gates — `RAGNET_*` environment variables, `#if` symbols, skip attributes —
and knows nothing of workflow `if:` conditions, so a workflow gate sits outside it. Extending
its scanner to workflows was considered and declined: there is exactly one workflow gate, and a
general workflow-gate scanner built for one instance is speculation of the kind this repository
keeps deleting. What holds this gate instead is `WorkflowWiringTests` in the gating fast tier,
which pins the job's condition, the endpoint, the push command text and this page's fenced
procedure — the same properties `TestGateTests` demands: named, condition stated, satisfiable by
a documented procedure, and guarded so it cannot be deleted or drift silently.

### What the rehearsal cannot prove — the 6.3 residual

Pushing to a local feed is not pushing to nuget.org. **Exercised for real exactly once, on
release day:** authentication, API-key scoping, package-ID availability (none of the 70 IDs is
reserved until then — an exposure the design accepts and records), the service's own validation,
the real 409-and-skip behaviour of `--skip-duplicate`, and `.snupkg` symbol delivery — which at
nuget.org rides automatically on each `.nupkg` push and cannot be rehearsed against a directory
feed at all. This gap is the argument the rejected alternative — publish prereleases now — was
making, and it does not vanish because that alternative was not chosen.

## Versioning: GitVersion and the release tooling

Until Phase 4.1 every package packed as **1.0.0** — the SDK default, chosen by nobody. The
version is now **derived from git history by GitVersion**, the house convention
(`MarcelRoozekrans/AdoNet.Async`): `GitVersion.yml` is the configuration, the tool is pinned in
`.config/dotnet-tools.json`, and the output is parsed with `jq`. Both packing jobs consume it —
a derive step runs `dotnet dotnet-gitversion /output json | jq -r '.SemVer'`, fails loudly when
the result is not a version (because `-p:Version=` with an empty value packs 1.0.0 again,
silently), and hands it to the pack command.

The repository has **no tags yet, deliberately** — Phase 6.3 decides the release version — so
every derived version is a **prerelease**: `0.1.0-preview.N` on `main`, with N incrementing per
commit, and `0.1.0-<branch>.N` on a branch. Measured on 2026-08-03: `main` derived
`0.1.0-preview.1495`, and in a throwaway clone a `v1.0.0` tag on HEAD derived a stable `1.0.0`
with **no configuration change** — the mechanism release day depends on, verified before release
day. Two guards keep the wiring from rotting into decoration: `WorkflowWiringTests` pins the
derive and pack command text in both packing jobs, and
`EveryPackageCarriesTheVersionGitVersionDerives` re-derives the version after every pack and
reads what the produced packages actually say — so a deleted derive step, a dropped `-p:Version`
flag and a stale `GitVersion.yml` all fail a gating job instead of quietly shipping 1.0.0.

### Conventional commits, enforced mechanically

release-please derives release versions from commit messages, so a malformed commit is not a
style nit — it is input the release tooling cannot read. The `commitlint` job lints **only the
commits a pull request adds**, against `.commitlintrc.yml`: stock
`@commitlint/config-conventional` with three deviations, each measured against the full history
on 2026-08-03 rather than guessed. `bench` is a permitted type (19 historical commits use it,
and benchmark work recurs here); `subject-case` is off (83 historical commits start the subject
with a proper noun — `LangChain`, `SciFact`, `Milestone` — which the rule cannot tell from
shouting); `body-max-line-length` is off (bodies quote error messages and command lines
verbatim).

**Existing history is deliberately not linted.** Stock config-conventional fails 184 of the
1,506 commits; even the tuned rules fail 70 — 44 headers over 100 characters (none after
2026-07-26), 24 typeless subjects from the pre-convention era (none after 2026-07-29), and 2
one-off types. Turning a gating check permanently red for commits nobody can amend teaches
people to ignore it, so the start point is the commit that introduced `.commitlintrc.yml`, and
the job lints the pull request's base-to-head range only.

### The gated release

The `release-please.yml` workflow is fully wired and, unlike the push, **cannot be rehearsed**:
its only observable effects — a release pull request, a `vX.Y.Z` tag, a GitHub release — are
the release itself. It is the one genuinely unexercised path Phase 4.1 ships, recorded to the
same standard as the push gate rather than left unstated:

| | |
|---|---|
| **Name** | `release-please`, the workflow in `.github/workflows/release-please.yml` |
| **Condition** | a manual `workflow_dispatch` on `main` — no push trigger, so nothing proposes a release before 6.3 asks for one |
| **Satisfied by** | the procedure below, runnable by any maintainer; Phase 6.3 executes it |

```bash
# The release PR: release-please reads the conventional commits since the last release and
# opens a PR proposing the version they imply. The user merges it, like every PR here.
gh workflow run release-please.yml --ref main
# After that PR merges, dispatch again: release-please sees the merged release PR and creates
# the GitHub release and the vX.Y.Z tag — the tag GitVersion derives the stable version from.
gh workflow run release-please.yml --ref main
# First release ever: release-please proposes 1.0.0 by default. If 6.3 decides otherwise,
# override before the first dispatch with an empty commit carrying a Release-As footer:
git commit --allow-empty -m "chore: set the release version" -m "Release-As: 0.9.0"
```

Then the release itself is the publish procedure above, dispatched on the tagged commit — where
GitVersion returns the tag's stable version and `publish-nuget` packs and pushes exactly that.

**The release pull request arrives with no checks on it, and that is a property of GitHub rather
than a misconfiguration.** Events triggered by the built-in `GITHUB_TOKEN` do not start workflow
runs — the rule that stops workflows triggering themselves forever. release-please opens its PR
with that token, so `ci.yml`'s `pull_request` trigger never fires and **none of the four required
checks reports**. Verified rather than inferred: release PR
[AdoNet.Async#140](https://github.com/MarcelRoozekrans/AdoNet.Async/pull/140) has `total_count: 0`
check runs and was merged anyway, through the same admin bypass this repository has.

That matters here more than it does there. The `Main` ruleset requires four checks precisely so
that nothing reaches `main` unvalidated — and the release commit, the one commit that becomes a
tag and 70 published packages, would be the single commit merged with **no CI at all**, by
bypassing the rule that exists to prevent it. Pick one before the first release:

- **Run the checks by hand on the release branch.** `ci.yml` has a `workflow_dispatch` trigger,
  and a dispatched run reports against the branch's head commit, so the required checks can be
  satisfied without a bypass:

  ```bash
  gh workflow run ci.yml --ref release-please--branches--main
  ```

- **Give release-please a token that is not `GITHUB_TOKEN`** — a fine-grained PAT or a GitHub App
  installation token, passed as the action's `token:` input. Its PR triggers `ci.yml` normally and
  the checks run unprompted. This is the option that needs no discipline on the day, at the cost
  of a credential to hold — which is the trade Trusted Publishing was just adopted to avoid
  elsewhere, so it is a real choice rather than an obvious one.

Merging the release PR with the admin bypass is the third option and is the one to take
deliberately or not at all, because it is indistinguishable afterwards from the bypass being
routine.

**The residual, stated:** the action's first real execution is release day. What holds it until
then is `WorkflowWiringTests`, which pins the dispatch-only trigger, the `main`-ref condition,
the action reference and this fenced procedure — the same properties every other gate in this
repository is held to: named, condition stated, satisfiable by a documented procedure.

### Renovate

`renovate.json` extends `config:recommended` and `:semanticCommits` (so Renovate's own commits
pass the commitlint gate) and carries the `dependencies` label. Phase 4.8 added one
`packageRules` entry: patch and minor bumps are grouped into a single PR on a weekly schedule
(`before 6am on monday`); majors get no rule of their own and fall through to
`config:recommended`'s default — ungrouped, unscheduled — which is already "one PR per major,
proposed as soon as it is available". That is deliberate, not an omission: majors are where
breakage lives — `Qdrant.Client` floating to 1.18.1 overnight and deprecating `SearchAsync` is
the worked example (Phase 4.8's entry in `docs/planning/ROADMAP.md` has the full account) — so
each major earns its own PR and its own changelog read rather than riding inside a batched weekly
bump. It was validated with `renovate-config-validator` on 2026-08-03 (the original config) and
re-validated 2026-08-04 after the `packageRules` addition — both runs reported `Config validated
successfully`.

**Correction, 2026-08-08 (Phase 4.5's documentation pass): the app is enabled and opening PRs.**
This section previously read "inert until the Renovate GitHub App is enabled" — true when Phase
4.8 wrote it, false by the time this page was next read. Evidence: five `renovate/*` branches live
on the repository as of this correction (`box.v2-10.x`, `major-ml-dotnet-monorepo`,
`pinecone.client-4.x`, `wiremock.net-2.x`, `zeroalloc.mediator.generator-5.x`), and `gh pr list`
shows Renovate PRs opening since 2026-08-05, several already merged — including a major-version
bump (`zeroalloc.valueobjects` to v2, PR #54, merged into `main`). Enabling the app is still the
repository owner's action, taken in a browser, with no CLI or API equivalent to fence — that half
of this section stands unchanged; only the "unexercised" claim was stale.

**Two claims, and Phase 4.8 recorded them separately rather than letting one imply the other —
both now demonstrated.** *Dependency pinning is delivered and provable*: the phase's nuspec diff
came back empty over 156 external dependency lines, so no published floor moved. *Upgrade
automation is configured and exercised*: the app has opened and merged real PRs against this
repository, both patch/minor batches and standalone majors, matching the `packageRules` shape
Phase 4.8 configured.

## Running the tiers locally

```bash
# fast tier — no Docker, no secrets
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --no-build

# Docker tier — needs a running Docker daemon
dotnet test tests/Rag.NET.VectorStores.Qdrant.Tests/Rag.NET.VectorStores.Qdrant.Tests.csproj --no-build
```

The explicit `dotnet build` before any `--no-build` run is load-bearing rather than an optimisation:
`Directory.Build.props` documents an SDK regression under which `dotnet test` from a completely empty
`obj/` still requires a build first, and a CI checkout is empty every single run.

Warnings are errors across the whole solution (`Directory.Build.props`), so CI needs no extra
strictness flag — a warning fails the build wherever it is built.
