# Full retrieval options on the ask path: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `AskAsync` and `AskStreamingAsync` accept a complete `RetrievalOptions` through `RagOptions.Retrieval`, and reject it when it is combined with the eight short-form fields.

**Architecture:** `RagOptions` records which of the eight retrieval fields were assigned. `RagPipeline.BuildRetrievalOptions` returns `Retrieval` unchanged when it is set alone, keeps today's copy when it is not set, and throws `ArgumentException` when both are set.

**Tech Stack:** .NET, xUnit v3, NSubstitute, System.Text.Json.

**Spec:** `docs/plans/2026-11-09-ask-retrieval-options-design.md`

## Global Constraints

- Every call that does not set `Retrieval` behaves exactly as before.
- `ArgumentException` uses `paramName: "options"` and its message names every conflicting field.
- It is thrown before any retrieval or model call.
- Conventional commits, with headers of at most 100 characters (commitlint). No nested parentheses in commit bodies.
- Release type: `feat:`, a minor.
- C# fences in `docs/` are compiled by `pack-validate`, so a docs example has to compile.

## Review Focus

1. `TopK = 5`, the default value, set next to `Retrieval`. Expected: it throws. Task 1 tests every field at its default value.
2. `AskStreamingAsync` with a conflict. Expected: it throws on the first `MoveNextAsync`, and the retriever is never called. Task 1.
3. `MaxContextTokens`, which today's mapping test does not pin. Expected: it is still forwarded when `Retrieval` is null. Task 1 adds it to the mapping test.
4. JSON input that sets only `retrieval`. Expected: no conflict. Task 1.
5. `Retrieval` set to an invalid value such as `TopK = 0`. Expected: the retriever's own validation runs, with no special handling in the pipeline. Task 1 asserts the instance is passed through. The validation itself is covered elsewhere already.

---

### Task 1: `RagOptions.Retrieval` and conflict detection

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/Options/RagOptions.cs`, which holds the eight retrieval fields, the new property, the flags and the remarks.
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs:130-140` (`BuildRetrievalOptions`)
- Test: `tests/Rag.NET.Tests/Models/Options/RagOptionsTests.cs`
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineFacadeTests.cs`

**Interfaces:**
- Produces: `public RetrievalOptions? Retrieval { get; set; }` on `RagOptions`.
- Produces: `internal IReadOnlyList<string> AssignedRetrievalFields { get; }` on `RagOptions`. It returns the `nameof` of each assigned field, in declaration order. `Rag.NET.Abstractions.csproj` grants `InternalsVisibleTo` through `AssemblyAttribute` items. Check that `Rag.NET` and `Rag.NET.Tests` are among them, and add whichever is missing.
- `RetrievalOptions` is a `sealed record` in `src/Rag.NET.Abstractions/Models/Options/RetrievalOptions.cs`. Pass the instance itself, not a `with` copy.
- The eight fields are `TopK`, `MaxContextTokens`, `MinScore`, `UseHybridSearch`, `UseLostInTheMiddleReordering`, `UseRedundancyFilter`, `RedundancyThreshold` and `MetadataFilter`. Their default values do not change.

- [ ] **Step 1: Write the failing tests**

  In `RagOptionsTests`:
  - `AssignedRetrievalFields_EmptyByDefault`: `new RagOptions().AssignedRetrievalFields` is empty.
  - `AssignedRetrievalFields_RecordsAssignmentAtDefaultValue`, a `[Theory]` over the eight names. Assign each field its own default value, such as `TopK = 5` or `RedundancyThreshold = 0.95f`, then check that `AssignedRetrievalFields` equals `[name]`.
  - `AssignedRetrievalFields_IgnoresNonRetrievalFields`: assigning `SystemPrompt`, `Temperature` and `Retrieval` leaves it empty.
  - `Json_RetrievalOnly_AssignsNoField`: `JsonSerializer.Deserialize<RagOptions>("""{"retrieval":{"useMmr":true}}""", JsonSerializerOptions.Web)` gives an object whose `Retrieval.UseMmr` is true and whose `AssignedRetrievalFields` is empty.
  - `Json_TopK_AssignsTopK`: deserializing `{"retrieval":{},"topK":5}` gives `["TopK"]`.

  In `RagPipelineFacadeTests`:
  - Extend `AskStreamingAsync_MapsRagOptionsToRetrievalOptions` with `MaxContextTokens = 1234`, and assert `r.MaxContextTokens == 1234`.
  - `AskAsync_WithRetrieval_PassesInstanceUnchanged`: with `var retrieval = new RetrievalOptions { UseMmr = true, UseHyde = false, TopK = 9 }`, the retriever receives `Arg.Is<RetrievalOptions?>(r => ReferenceEquals(r, retrieval))`.
  - The same check for `AskStreamingAsync`: `AskStreamingAsync_WithRetrieval_PassesInstanceUnchanged`.
  - `AskAsync_RetrievalWithShortFormField_Throws`, a `[Theory]` over the eight names. Set that field to its default value plus `Retrieval = new()`. Assert that `ArgumentException` is thrown, that `ParamName == "options"`, that the message contains the field name, and that `_retriever.DidNotReceiveWithAnyArgs().RetrieveAsync(...)`.
  - The same theory for `AskStreamingAsync`, enumerating the stream: `AskStreamingAsync_RetrievalWithShortFormField_Throws`.
  - `AskAsync_RetrievalWithTwoShortFormFields_MessageNamesBoth`: with `TopK` and `MinScore` set, the message contains both names.

  A theory over field names needs one helper, `static RagOptions WithFieldAtDefault(string name)`, with a switch over the eight names. It lives in the test class.

- [ ] **Step 2: Run the tests and confirm they fail**

  Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RagOptionsTests|FullyQualifiedName~RagPipelineFacadeTests"`
  Expected: compile errors for `Retrieval` and `AssignedRetrievalFields`.

- [ ] **Step 3: Implement**

  - In `RagOptions`, turn each of the eight auto-properties into a property with a backing field, keeping the initialisers. Each setter sets a bit in a private `[Flags] enum RetrievalField`.
  - Add `Retrieval`, with a doc comment. When it is set, it is used as is, and setting any of the eight fields as well throws at `AskAsync` or `AskStreamingAsync`. Mention MMR and switching HyDE, multi-query and reranking off.
  - Rewrite the class `<remarks>` so it no longer says MMR and HyDE can't be reached. It should point at `Retrieval` instead.
  - In `BuildRetrievalOptions`, keep the copy when `Retrieval` is null. When `Retrieval` is set and nothing was assigned, return it. Otherwise throw `new ArgumentException($"…{string.Join(", ", assigned)}…", "options")`. The message should say to move those fields into `RagOptions.Retrieval`.
  - `AskAsync` already calls `BuildRetrievalOptions` before retrieval. `AskStreamingAsync` calls it inside the iterator, so the throw lands on the first `MoveNextAsync`. Neither call site needs to change.

- [ ] **Step 4: Run the tests and confirm they pass**

  Run the command from Step 2. Expected: every test passes, and so do the existing tests in both classes.

- [ ] **Step 5: Commit**

  `git commit -m "feat(core): let AskAsync take full RetrievalOptions through RagOptions.Retrieval (#932)"`

### Task 2: Docs

**Files:**
- Modify: `docs/guide/retrieval.md:1011-1045`, the section "Using retrieval options with `AskAsync`".

**Interfaces:**
- Consumes: `RagOptions.Retrieval` from Task 1.

- [ ] **Step 1: Correct the section**

  - Replace "`RagOptions` mirrors all `RetrievalOptions` properties". It mirrors eight of them.
  - Replace the class listing, which leaves out `MaxContextTokens` and several chat fields, with a sentence naming the eight short-form fields.
  - Replace the note "does not expose `UseMultiQuery`…" with a subsection on `Retrieval`, including a compiling example: `new RagOptions { Retrieval = new RetrievalOptions { TopK = 10, UseMmr = true, UseHyde = false }, SystemPrompt = "…" }`.
  - State the rule: `Retrieval` replaces the eight fields, and setting both throws `ArgumentException`, even when the value is the default.

- [ ] **Step 2: Verify that the docs build and the fence compiles**

  Run: `dotnet test tests/Rag.NET.RepoConventions.Tests`, which holds the docs guards. Then run pack-validate the way `ci.yml` does: `dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version=0.0.0-local`, followed by `dotnet test tests/Rag.NET.PackageValidation.Tests -c Release`.
  Expected: both pass.

- [ ] **Step 3: Commit**

  `git commit -m "docs(retrieval): document RagOptions.Retrieval for per-call MMR and HyDE (#932)"`

### Task 3: Whole-branch verification

- [ ] Run every test project listed by `dotnet sln list`. The known local-only failure, `Rag.NET.E2ETests` `WhatIsMyAddress_WithReporterSettings_*`, is excluded.
- [ ] Run pack-validate with the two commands from Task 2, Step 2.
- [ ] Push, and open the PR against `main`.
