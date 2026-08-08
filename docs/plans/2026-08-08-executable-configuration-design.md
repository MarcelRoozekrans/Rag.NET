# Making an Executable Configurable and Testable — Design (Phase 4.6)

**Date:** 2026-08-08
**Milestone:** 4 — Release Readiness
**Status:** approved (design)

## 0. What measurement found, and how it moved the phase

Phase 4.6 was scoped as *"`dotnet tool` for ingest/query/evaluate against a configured pipeline"* —
a new CLI. Before building it, the repository's one existing `dotnet tool` was measured.

**`Rag.NET.Mcp.Tool` cannot work as published.** Verified rather than inferred:

- `RagMcpTools(IRagPipeline pipeline)` takes the pipeline from DI.
- `AddRagNetMcpServer()` registers the MCP server and its tools and **never registers an
  `IRagPipeline`**.
- **`IConfiguration` appears nowhere** in `Rag.NET.Mcp` or `Rag.NET.Mcp.Tool` — zero occurrences.

So the tool starts, and fails the moment any MCP tool is invoked. It is also documented two
mutually impossible ways:

- Its **package description** says *"Configure via appsettings.json or environment variables."*
  There is no configuration code at all.
- Its **`Program.cs`** header says *"Edit this file after install and add your pipeline
  registrations."* `Program.cs` of an installed `dotnet tool` is compiled; that instruction cannot
  be followed by anyone.

It carries `<VerifiedBy>none</VerifiedBy>` — honestly recorded, and the reason none of the above was
caught. `IsPackable` is `true`, so it publishes at **6.3**.

**Building a second executable beside it, in the same untestable shape, would double the problem.**
So this phase makes an executable in this repository configurable and testable *once*, applies that
to the tool that exists, and then builds the CLI as the second consumer of it.

## 1. Why it shipped as a scaffold, and why that is now solvable

The scaffold was not laziness. A pipeline needs an embedding generator, a vector store and a chat
client; there are **six vector-store packages**, and chat/embedding providers come from
`Microsoft.Extensions.AI.*`. Referencing everything is what made this tool **19 MB** before Phase
4.7's decomposition. Referencing nothing was the other extreme, and it made the tool unusable.

Three decisions collapse the matrix.

### 1.1 One OpenAI-compatible client covers almost every provider

OpenAI, Azure OpenAI, OpenRouter, Ollama and LM Studio all speak the same wire API: an endpoint, a
key, a model id. **This repository already depends on that fact** — `TestChatClientFactory` points
an `OpenAIClient` at `https://openrouter.ai/api/v1` and treats it as an ordinary `IChatClient`.

So chat *and* embeddings need **one** package (`Microsoft.Extensions.AI.OpenAI`, already pinned at
10.8.3), not a provider matrix. A provider that is not OpenAI-compatible is served by hosting the
library directly — see §1.3.

### 1.2 A bounded, declared vector-store set

`InMemory` (already in core, zero setup), **Qdrant** and **PgVector** — the two with real fixtures
and integration coverage in this repository. Three named kinds, chosen and written down.

**`InMemory` is the default, and that must be loud.** Its data vanishes on restart. This repository
has already paid for exactly this silence once: `UseCostBudgeting()` defaulted to an in-memory cost
ledger whose spend reset on restart, with real money behind it. The configuration documentation
says so plainly, and an `InMemory` store logs a warning at startup rather than being quietly
convenient.

### 1.3 "Host the library yourself" stays a real answer, stated positively

Anything outside §1.1 and §1.2 — Weaviate, Pinecone, Chroma, Azure AI Search, ONNX embeddings, a
bespoke `IChatClient` — is served by referencing `Rag.NET.Mcp` from your own host and registering
whatever you like. That path already exists and works.

The current header comment offers it as option 2 after an impossible option 1. It becomes the
documented answer for the uncommon case, rather than an apology for the tool not working.

## 2. The seam: wiring moves into the library

The wiring moves out of `Program.cs` and into `Rag.NET.Mcp`, as an extension taking
`IConfiguration`. `Program.cs` becomes a handful of lines that parse transport arguments and run
the host.

**This is the whole point of the phase.** An executable whose behaviour lives in `Program.cs` can
only be tested by launching a process; one whose behaviour lives in a library method taking
`IConfiguration` can be tested by handing it a configuration and asserting what came out. That is
how `Rag.NET.Mcp.Tool` gets from `VerifiedBy: none` to `unit`, and it is the part the CLI reuses
rather than reinventing.

**Corrected 2026-08-08, while writing the plan.** An earlier draft of this section gave every store
a single `ConnectionString`. It does not fit: `UsePgVector(connectionString, vectorDimensions, …)`
takes one, but `UseQdrant(host, port, collectionName, vectorDimensions, …)` takes four separate
values and no connection string at all. The settings are per-kind, not uniform.

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

**`VectorDimensions` sits under `Embeddings`, not under the store**, because it is a property of the
embedding model and every store merely has to agree with it — `nomic-embed-text` is 768,
OpenAI's `text-embedding-3-small` is 1536, and the store parameters default to 1536 regardless of
what the model actually produces. A mismatch is a runtime failure at the first upsert, far from its
cause, which makes it §3's business.

Environment variables come free with the standard configuration builder, which is what the package
description already promises.

## 3. Failing at startup, not on first use

**A misconfigured tool must fail at startup with a readable message.** Validating lazily would move
the failure rather than fix it: today's defect is precisely that everything looks fine until an MCP
client calls a tool and gets an unresolvable-service error with no indication of what to configure.

Validation runs while building the host, names the missing or invalid setting, and names the
configuration key that would fix it. Not "Unable to resolve service for type 'IRagPipeline'".

It covers, at minimum: an unknown `VectorStore.Kind`; settings missing for the chosen kind
(a `Qdrant` kind with no `Host`); and a missing chat or embeddings `Endpoint`/`Model`.

**`VectorDimensions` is the one that cannot be validated at startup, and saying so is part of the
design.** Whether the configured number matches what the endpoint actually returns is only knowable
by embedding something, which startup must not do. What validation *can* do is refuse a value that
is absent or non-positive, and make the number explicit in configuration rather than letting the
store's `= 1536` default apply silently to a 768-dimension model. The remaining mismatch surfaces
on first ingest — which is acceptable only because the setting is now visible; today it is not
expressible at all.

## 4. What this phase corrects, beyond the code

- **The package description** becomes true rather than aspirational.
- **The impossible "edit this file after install" instruction** goes.
- **`VerifiedBy`** goes `none` → `unit`, which is a claim a test has to earn.
- **The 1.87 MB package shape** (34 entries, Cl100kBase vocabulary and the MCP stack dominating)
  becomes a stated decision rather than a default, which Phase 4.7's close asked for before
  publication.

## 5. Then the CLI

`ragnet` — ingest, query, evaluate — built on §2's configuration and wiring. It is the second
consumer of the seam, not a second scaffold, and it inherits the startup validation of §3.

Deliberately after the repair: a CLI written first would either reinvent this or, more likely,
repeat the scaffold.

## 6. Testing

- **The wiring seam, driven by configuration** — each vector-store kind resolves; an
  OpenAI-compatible endpoint produces a usable `IChatClient` and embedding generator.
- **Startup validation** — every missing or invalid setting produces a message naming the setting
  and the key. The messages are asserted, because a diagnostic nobody has read is not a diagnostic.
- **The `InMemory` warning fires**, since it is the guard against repeating the cost-ledger silence.
- **`Rag.NET.Mcp.Tool` gets its first test**, which is what moves `VerifiedBy` off `none`.

## 7. Out of scope

- **Provider packages beyond §1.1/§1.2.** Adding Weaviate, Pinecone, Chroma, Azure AI Search or
  ONNX to the tool's closure is the 19 MB mistake by instalments; §1.3 is the answer.
- **A configuration schema or generator.** JSON binding is enough at this size.
- **Changing `Rag.NET.Mcp`'s tool surface** (`rag_retrieve`, `rag_ask`, `rag_ingest`) — this phase
  makes the host work, not the tools different.
