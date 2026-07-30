---
id: ci
title: CI and Test Tiers
sidebar_position: 3
---

# CI and Test Tiers

Rag.NET has 64 test projects and they do not all want the same thing. Some need nothing but a
runtime; some start Docker containers; one downloads two gigabytes of language model; three need
credentials that only exist on the repository. Running them all on every push would be slow and
flaky. Running only the easy ones would be a lie.

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
cannot work there; that is why `ci.yml` is not a matrix.

**"Gates" in that table means the failure is real and fails the run** — no `continue-on-error`
anywhere in `ci.yml`. It does not yet mean a merge is mechanically blocked: this repository has no
branch protection rules, so no check is required. Both tiers are the ones to require when it is set
up.

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
| **`run-secrets`** | the env-gated suites — Tesseract, Document Intelligence, ONNX embedding and late chunking, and the SciFact retrieval-quality parity run | **Not yet** — it fails loudly, but no branch protection exists to block on |

On `run-secrets`: the job *gates* in the sense that a failure is a real failure and is reported as
one — no `continue-on-error` anywhere in it. It does not *block* anything today, because this
repository has no branch protection rules configured, so no check is required for a merge. When that
is set up, this is the nightly job to require; the `llm` one never is.

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

Three projects contain tests that need credentials or large local assets:

| Project | Reads |
|---|---|
| `Rag.NET.Parsers.Pdf.Tests` | `RAGNET_TESSDATA` |
| `Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests` | `RAGNET_DOCINTEL_ENDPOINT`, `RAGNET_DOCINTEL_KEY` |
| `Rag.NET.Chunking.IntegrationTests` | `RAGNET_ONNX_EMBED_MODEL`, `RAGNET_ONNX_EMBED_VOCAB` |

Each of those tests calls `Assert.Skip` when its variable is absent, so the projects are safe
anywhere and skip on a normal developer machine. They declare
`<RequiresSecrets>true</RequiresSecrets>`, and the `env-gated` job in `nightly.yml` selects on that
property and supplies the values.

**This is an overlay, not a fourth tier.** All three are fast-tier projects: they run in `ci.yml` on
every push (skipping the gated tests) *and* in `nightly.yml` with the values supplied. A project is
in one tier and may appear in more than one workflow.

**The `env-gated` job gates.** Unlike the LLM tier these suites are deterministic — Tesseract given
the same PDF and the same traineddata produces the same text — so a failure is a regression. One
honest caveat: when the secrets are not configured, every test skips and the job passes. A step in
the job prints which variables are present so a log reader can tell a real pass from a run in which
nothing executed. Forks never receive secrets, and that is deliberate: an unset secret is not a
failure.

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

`EveryProjectThatReadsASecretDeclaresRequiresSecrets` enforces both directions the same way. You
will also need the secret added to the repository settings, or the test will keep skipping.

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
