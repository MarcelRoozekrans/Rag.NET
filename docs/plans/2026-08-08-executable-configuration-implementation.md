# Making an Executable Configurable and Testable — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `Rag.NET.Mcp.Tool` actually work — configured from `appsettings.json`, validated at startup, and tested — then build the `ragnet` CLI on the same seam.

**Architecture:** Pipeline wiring moves out of `Program.cs` into a new `Rag.NET.Hosting` package as an `IConfiguration`-taking extension. That is the seam: it makes an executable's behaviour testable without launching a process, and the CLI reuses it rather than reinventing it.

**Tech Stack:** .NET 10, `Microsoft.Extensions.AI.OpenAI` (pinned 10.8.3), `Microsoft.Extensions.Configuration`, xUnit v3.

**Design:** `docs/plans/2026-08-08-executable-configuration-design.md` — read §1.1–§1.3 before choosing any provider.

---

## Context

**`Rag.NET.Mcp.Tool` cannot work as published.** `RagMcpTools(IRagPipeline pipeline)` takes the pipeline from DI; `AddRagNetMcpServer()` never registers one; `IConfiguration` appears **nowhere** in either project. It starts, then fails when any MCP tool is invoked.

It is documented two impossible ways: the **package description** promises `appsettings.json` configuration that does not exist, and **`Program.cs`** says *"Edit this file after install"* — it is compiled. `VerifiedBy` is `none`, which is why nothing caught any of it, and `IsPackable` is `true`, so it publishes at **6.3**.

## Bounded provider set — do not widen it

- **Chat + embeddings:** `Microsoft.Extensions.AI.OpenAI` only. One OpenAI-compatible client covers OpenAI, Azure OpenAI, OpenRouter, Ollama and LM Studio — `TestChatClientFactory` already relies on this.
- **Vector stores:** `InMemory` (core), **Qdrant**, **PgVector** — only these three.

**Do NOT add Weaviate, Pinecone, Chroma, Azure AI Search or ONNX embeddings to the tool's closure.** That is the 19 MB mistake by instalments. Anything outside the set is served by hosting `Rag.NET.Mcp` directly, which is a documented answer, not an apology.

## Ground rules

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`.** MA0051 (≤60-line methods), MA0048, MA0061, **MA0006** (`string.Equals`, not `==`), ERP022, EPC12/13, ZA0601. **MA0006 only surfaces under `-c Release`** — build Release before believing you are done.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits **with bodies**, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. **Subject under 100 characters.**
- **Never `git add -A`** — explicit paths. **Never pipe build/test output through `head`/`tail`/`grep`.**
- **An incremental build is not a measurement** — `--no-incremental` for any quoted count.
- **`git status` before committing** — a file watcher edits `.csproj`/`.slnx` concurrently and has removed a project from the solution before.

**Baselines:** `Rag.NET.Tests` **1180**, `RepoConventions` **48 + 1 skip**.

---

## Task 1: The seam — configuration-driven wiring in its own package

**Files:**
- Create: `src/Rag.NET.Hosting/` — **a new package**: options types plus the wiring extension
- Create: `tests/Rag.NET.Hosting.Tests/`
- Modify: `Rag.NET.slnx` — add both, and **confirm they are there before committing**

**The wiring does NOT go in `Rag.NET.Mcp`.** That was an error in an earlier draft of this plan.
`Rag.NET.Mcp` is what a user references to host MCP tools in their own application — the design's
§1.3 path for providers outside the bounded set. Putting Qdrant, PgVector and
`Microsoft.Extensions.AI.OpenAI` into it would force those on every such user, which is the 19 MB
mistake in miniature and the exact thing Phase 4.7 existed to undo.

A separate `Rag.NET.Hosting` package holds the configuration types and the wiring. `Rag.NET.Mcp.Tool`
references it; Task 7's CLI references it; `Rag.NET.Mcp` does not.

**A new package must satisfy the packaging conventions** — `RepoConventions` enforces a `README.md`,
a `<Description>`, and a `<VerifiedBy>`. It ships at **`unit`**, with tests in the same commit; a new
package arriving at `none` is what this phase exists to stop repeating.

**This task is the phase.** Everything else depends on behaviour living in a library method that takes `IConfiguration`, rather than in `Program.cs` where only a launched process can reach it.

Add an extension — name it to match the repo's conventions, e.g. `AddRagNetPipelineFromConfiguration(this IServiceCollection services, IConfiguration configuration)` — that binds the `RagNet` section and registers `IRagPipeline` with a chat client, embedding generator and vector store.

**The configuration shape is per-kind, not uniform.** `UsePgVector(connectionString, vectorDimensions, …)` takes a connection string; `UseQdrant(host, port, collectionName, vectorDimensions, …)` takes four values and no connection string. **Check both signatures in the source before binding anything.**

```json
{
  "RagNet": {
    "ChatClient":  { "Endpoint": "…", "ApiKey": "…", "Model": "…" },
    "Embeddings":  { "Endpoint": "…", "ApiKey": "…", "Model": "…", "VectorDimensions": 1536 },
    "VectorStore": {
      "Kind": "InMemory | Qdrant | PgVector",
      "Qdrant":   { "Host": "…", "Port": 6334, "CollectionName": "…" },
      "PgVector": { "ConnectionString": "…" }
    }
  }
}
```

`VectorDimensions` lives under `Embeddings` because it is a property of the model, and every store must agree with it — see the design's §2.

**Tests:** each of the three kinds resolves an `IVectorStore` of the expected type; a configured endpoint produces a usable `IChatClient` and embedding generator. **No network calls** — assert what was registered, not what a provider returns.

**Do not touch `Program.cs` yet.** Task 5 does that, once there is something for it to call.

---

## Task 2: Startup validation that names the setting and the key

**Files:** same as Task 1
**Test:** `tests/Rag.NET.Hosting.Tests/`

Today's defect is that everything looks fine until an MCP client calls a tool and gets `Unable to resolve service for type 'IRagPipeline'`. **Validating lazily would move the failure, not fix it.**

Validation runs while the host is built and covers at least:

- an unknown `VectorStore.Kind`
- settings missing for the chosen kind — a `Qdrant` kind with no `Host`
- a missing chat or embeddings `Endpoint` or `Model`
- a `VectorDimensions` that is absent or non-positive

**Every message must name both the setting and the configuration key that fixes it.** "Missing endpoint" is not enough; `RagNet:ChatClient:Endpoint` is.

**Assert the message text, not just the exception type.** A diagnostic nobody has read is not a diagnostic — this is the whole reason the task exists.

**What cannot be validated, and must be documented rather than faked:** whether `VectorDimensions` matches what the endpoint actually returns. Knowing that requires embedding something, which startup must not do. Refuse absent or non-positive values, and say in the XML docs that a genuine mismatch surfaces at first ingest.

---

## Task 3: Make the `InMemory` default loud

**Files:** same
**Test:** `tests/Rag.NET.Hosting.Tests/`

`InMemory` data vanishes on restart. **This repository has already paid for exactly this silence**: `UseCostBudgeting()` defaulted to an in-memory cost ledger whose spend reset on restart, with real money behind it.

Log a warning at startup when the resolved kind is `InMemory`, naming the consequence — not merely "using in-memory store".

**Test that the warning fires**, using the repo's existing `FakeLogger`/logging-test approach (see how other tests assert logs; do not invent a mechanism). This test is the guard against repeating the ledger mistake.

---

## Task 4: First tests for `Rag.NET.Mcp.Tool`, and the `VerifiedBy` ledger

**Files:**
- Modify: `src/Rag.NET.Mcp.Tool/Rag.NET.Mcp.Tool.csproj` — `<VerifiedBy>none</VerifiedBy>` → `unit`
- Modify: `tests/Rag.NET.RepoConventions.Tests/PackageVerificationTests.cs` — the `PackagesAllowedToDeclareNone` ledger
- Test: a project that can reach the tool's own code

**The ledger is the trap.** `PackageVerificationTests` has two paired guards: `NoPackageIsVerifiedByNothing` requires every `none` to be listed in `PackagesAllowedToDeclareNone` with a reason, and **`EveryPackageAllowedToDeclareNoneStillDeclaresNone` fails if a listed package stops declaring `none`.** So changing `VerifiedBy` **without removing the ledger entry turns that staleness guard red.** Change both together.

Whatever the tool still owns after Task 5 — argument parsing, transport selection — needs a real test. If `Program.cs` retains no testable logic at all, say so and explain what `unit` then means for this package, rather than asserting something vacuous to move a label.

**Expect `RepoConventions` to change**: one fewer package at `none` changes the skip. State the arithmetic.

---

## Task 5: `Program.cs`, and the two impossible instructions

**Files:**
- Modify: `src/Rag.NET.Mcp.Tool/Program.cs`
- Modify: `src/Rag.NET.Mcp.Tool/Rag.NET.Mcp.Tool.csproj` (the `Description`)
- Modify: `src/Rag.NET.Mcp.Tool/README.md`

`Program.cs` calls Task 1's extension and shrinks to argument parsing plus running the host.

**Delete the header comment's "Edit this file after install and add your pipeline registrations."** It cannot be done — the file is compiled into the installed tool. Replace it with what is now true: configure via `appsettings.json` or environment variables, and host `Rag.NET.Mcp` yourself for providers outside the bounded set.

**The package `Description` currently promises "Configure via appsettings.json or environment variables."** After Task 1 that is true for the first time. Check it still describes the tool accurately and adjust if not.

Ship a **sample `appsettings.json`** showing every supported kind, and confirm it is actually packed — check the `.nupkg`, not the csproj. *Phase 4.7 learned that intent and artefact differ.*

---

## Task 5a: Make the transport registration real (added 2026-08-08)

**Files:**
- Modify: `src/Rag.NET.Mcp/DependencyInjection/McpServerBuilder.cs`
- Modify: `src/Rag.NET.Mcp/DependencyInjection/ServiceCollectionExtensions.cs`
- Possibly: `src/Rag.NET.Mcp.Tool/Program.cs`
- Test: `tests/Rag.NET.Mcp.Tests/`

**Not in the original plan. Found by running the tool during Task 5** — the first time anything
ever had, which `VerifiedBy: none` had guaranteed.

`McpServerBuilder.WithStdioTransport()` and `WithHttpTransport(port)` **are no-ops.** They set
fields on an `McpTransportOptions` singleton that **nothing reads** — verified: zero references
outside its own declaration. Nothing anywhere calls the MCP SDK's real transport registration.

Observed by running the built executable:

- **HTTP** throws at `app.MapMcp()` — *"You must call WithHttpTransport()"*.
- **stdio — the default, and what every MCP client uses — silently starts a bare Kestrel web
  server on port 5000 instead of speaking MCP over stdio.**

So even with Tasks 1-3's pipeline correctly wired, the tool still does not do the one thing it
exists to do. **This is a larger defect than the one this phase was created for**, and the phase's
deliverable — a tool that works when configured — is not met without it.

### The constraint that probably caused it

`Rag.NET.Mcp` references **`ModelContextProtocol`**; `Rag.NET.Mcp.Tool` references
**`ModelContextProtocol.AspNetCore`**. `WithStdioServerTransport()` is available to the library;
the real `WithHttpTransport()` extension is **not** — it lives in the ASP.NET Core package the
library deliberately does not reference. Flags-nobody-reads was very likely the workaround.

So the two halves differ:

- **stdio** can be delegated from the library directly.
- **HTTP** cannot, without `Rag.NET.Mcp` taking an ASP.NET Core dependency — which would be the
  same mistake as putting provider references in it (design §1.3).

`AddRagNetMcpServer()` currently **discards** the `IMcpServerBuilder` that `services.AddMcpServer()`
returns. Exposing it is the obvious way to let the tool call the ASP.NET-only extension itself.
**Choose a shape, and say why** — this is a public API decision on `Rag.NET.Mcp`, not a detail.

### Non-negotiable

**A transport method that silently does nothing must become impossible.** If a transport cannot be
configured from where it is called, that must be a compile error or a loud throw — never a
fluent call that returns `this` and changes nothing.

### Testing

Assert the SDK actually received the transport registration, not that our own flag was set —
**asserting the flag is what created this bug.** If the SDK's registration is not observable,
say so and describe what you asserted instead.

**Expect `Rag.NET.Mcp.Tests` to change.** State the arithmetic.

---

## Task 6: Decide the package shape

**Files:** `docs/planning/ROADMAP.md` (the debt entry at ~line 410)

Phase 4.7 measured the packed tool at **1.87 MB, 34 entries**, dominated by the Cl100kBase vocabulary and the MCP stack, and asked for *"a decision rather than a default before the tool is published."*

Task 1 adds provider references, so **re-pack and re-measure** — the old figure is now stale:

```bash
dotnet pack src/Rag.NET.Mcp.Tool -c Release -p:Version=0.0.1-check -o <scratchpad>
```

Record the new size and entry count, and state whether that shape is intended. **Never commit a `.nupkg`.**

If the bounded provider set has pushed it somewhere uncomfortable, **say so with the number** rather than quietly accepting it — that is the decision this task exists to make.

---

## Task 7: The `ragnet` CLI

**Files:** a new `src/Rag.NET.Cli/` project, plus tests

Commands: **ingest**, **query**, **evaluate**, against a pipeline built by **Task 1's extension**. It is the second consumer of the seam, not a second scaffold — it must not do its own wiring.

- Same configuration, same startup validation, same `InMemory` warning.
- **Command handlers must be testable without launching a process** — the same rule that made this phase necessary. `Program.cs` stays thin.
- It ships with `VerifiedBy: unit` and tests **from its first commit**, not added later. A new package arriving at `none` is what this phase exists to stop repeating.

Add it to `Rag.NET.slnx`. **Confirm it is there before committing** — the file watcher has removed a project mid-rebase before, and a project missing from the solution builds nothing and reports success.

---

## Task 8: Documentation and ROADMAP

**Files:** `docs/` (find the existing MCP/tooling pages — do not create new ones), `docs/planning/ROADMAP.md`

Document the configuration shape, every supported kind, the `InMemory` warning, and the host-it-yourself path for providers outside the bounded set.

In `ROADMAP.md`, record honestly:

- **The tool could not work as published** — no `IRagPipeline` registered, no `IConfiguration` anywhere, and two mutually impossible sets of instructions.
- **`VerifiedBy: none` is why nothing caught it** — the ledger was doing its job by recording it; nothing was reading the record.
- The re-measured package shape from Task 6.
- Close the entries this phase owned, and **any it did not** — say which and why.

**Do not tick a Definition-of-Done box this phase did not make true.**

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release --no-incremental
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.Hosting.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
```

State every count with arithmetic against the baselines. **The deliverable is a tool that works when configured, says so clearly when it is not, and has a test that would notice if either stopped being true.**
