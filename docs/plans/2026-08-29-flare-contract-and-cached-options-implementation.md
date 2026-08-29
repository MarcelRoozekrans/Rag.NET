# FLARE Contract and Cached Options Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop FLARE's per-sentence loop from being hijacked by a caller's terminal system prompt, restore the `MaxOutputTokens` guard the harness disarms, and apply the judge's extraction contract to FLARE's completed answer instead of to each fragment.

**Architecture:** Three independent fixes. One is a product change in `Rag.NET.AnswerEngines` (FLARE composes the caller's system prompt with its own fragment protocol rather than being replaced by it). Two are harness changes: `GraphExtractionCache` gains an optional third key field, and `CachedGraphRagClient` stops discarding the caller's `ChatOptions`. The third fix moves the contract to a single post-loop formatting call in the FLARE arms.

**Tech Stack:** .NET 10, C#, xunit.v3, NSubstitute, Microsoft.Extensions.AI 10.9.0.

**Spec:** `docs/plans/2026-08-29-flare-contract-and-cached-options-design.md`

## Global Constraints

- **Commit format:** conventional, free scopes (`docs/planning/CONVENTIONS.md`). Header **≤ 100 characters** — commitlint runs on every commit a PR adds.
- **Branch:** `fix/flare-contract-and-cached-options`. `main` is protected and requires a PR. **Do not merge.**
- **Build must stay at 0 warnings:** `Directory.Build.props` sets `TreatWarningsAsErrors=true`.
- **Never use `dotnet test --filter`** on `Rag.NET.Benchmarks.Quality.IntegrationTests`. `TestingPlatformDotnetTestSupport` with xunit.v3 discards the VSTest filter and runs all 25 classes. Invoke the runner directly with `-class`.
- **Every test must be mutation-checked:** verify the test fails against a deliberately broken implementation, and verify the mutation compiles before trusting the failure. This repo has shipped a provably vacuous regression test before.
- **Zero regeneration is a hard requirement.** All 86,510 existing cache entries (47,322 answers, 35,176 extractions, 4,012 reports) must keep their keys. Task 2 exists to prove this rather than assert it.

---

### Task 1: FLARE composes the caller's system prompt

**Files:**
- Modify: `src/Rag.NET.AnswerEngines/FlareAnswerEngine.cs` (`DefaultSystemPrompt` ~line 28, `BuildMessages` ~line 282-305)
- Test: `tests/Rag.NET.Tests/AnswerGeneration/FlareAnswerEngineTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `FlareAnswerEngine` whose system message always ends with its fragment-protocol clause. Task 4 relies on FLARE no longer looping when handed a terminal instruction, but does **not** call anything new — the change is internal to `BuildMessages`.

This is the product defect. `BuildMessages` currently emits `new(ChatRole.System, opts.SystemPrompt ?? DefaultSystemPrompt)`, so a caller's system prompt *replaces* FLARE's framing entirely. A caller instruction like *"End your reply with exactly this sentence…"* then contradicts the user message's *"Continue the answer with EXACTLY ONE additional sentence… reply with only `<DONE>`"*, and the model emits the closing sentence forever without ever emitting `<DONE>`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Rag.NET.Tests/AnswerGeneration/FlareAnswerEngineTests.cs`. Match the file's existing fake-client and construction patterns — read the file first and reuse whatever it already uses to build a `FlareAnswerEngine`; do not introduce a second style.

```csharp
/// <summary>
/// A caller's <see cref="RagOptions.SystemPrompt"/> must not displace FLARE's fragment protocol.
/// </summary>
/// <remarks>
/// Regression test for the 2026-08-29 runaway. FLARE generates one sentence per call and feeds the
/// growing answer back in; a caller instruction such as "End your reply with exactly this sentence"
/// is a <b>terminal</b> instruction, and applied per fragment it makes the model emit the closing
/// sentence forever and never emit the DONE token, so the loop also loses its early exit. One
/// observed response held the same sentence 256 times, 86,091 bytes, against a 3,747-byte maximum
/// across the 47,151 entries written before that day.
/// </remarks>
[Fact]
public async Task ACallerSystemPrompt_DoesNotDisplaceTheFragmentProtocol()
{
    var captured = new List<ChatMessage>();
    var client = new CapturingChatClient(captured, reply: "<DONE>");
    var engine = NewEngine(client);

    _ = await engine.AskAsync(
        "q",
        [NewSearchResult("ctx")],
        new RagOptions { SystemPrompt = "End your reply with exactly: The answer is \"...\"" });

    var system = Assert.Single(captured.Where(m => m.Role == ChatRole.System));
    Assert.Contains("End your reply with exactly", system.Text, StringComparison.Ordinal);
    Assert.Contains("exactly one sentence", system.Text, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("<DONE>", system.Text, StringComparison.Ordinal);
}
```

If `CapturingChatClient`, `NewEngine` or `NewSearchResult` do not already exist in this file under some name, write them as private helpers in the test class. `CapturingChatClient` implements `IChatClient`, appends every message it receives to the supplied list, and returns the supplied `reply` from `GetResponseAsync`.

- [ ] **Step 2: Run it and verify it fails**

```bash
dotnet build tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Debug
./tests/Rag.NET.Tests/bin/Debug/net10.0/Rag.NET.Tests.exe \
  -method '*ACallerSystemPrompt_DoesNotDisplaceTheFragmentProtocol*'
```

Expected: FAIL. The system message is exactly the caller's prompt, so the `"exactly one sentence"` and `"<DONE>"` assertions do not find their text.

- [ ] **Step 3: Add the fragment-protocol clause**

In `src/Rag.NET.AnswerEngines/FlareAnswerEngine.cs`, beside `DefaultSystemPrompt`:

```csharp
/// <summary>
/// FLARE's own framing, always appended after any caller system prompt.
/// </summary>
/// <remarks>
/// <b>This is mechanism, not style, which is why a caller cannot displace it.</b> FLARE emits one
/// sentence per call and feeds the growing answer back in, so an instruction written for a complete
/// reply — "end with this sentence", "reply in JSON" — is actively harmful applied per fragment: the
/// model satisfies it every time, the satisfied text becomes "answer so far", and it never emits
/// <see cref="DoneToken"/>. Appended last so it is the most recent instruction the model reads.
/// </remarks>
private const string FragmentProtocol =
    "You are writing ONE sentence at a time as part of a longer answer that is assembled from " +
    "your replies. Reply with exactly one sentence, or with only " + DoneToken + " if the answer " +
    "is complete. These replies are fragments, not a complete reply: do not add closing or " +
    "summary sentences, and do not apply any end-of-reply formatting instruction to them.";
```

- [ ] **Step 4: Compose it in `BuildMessages`**

Replace the system message line in `BuildMessages`:

```csharp
        return
        [
            new(ChatRole.System, BuildSystemPrompt(opts)),
            new(ChatRole.User, userText),
        ];
    }

    /// <summary>The caller's system prompt, if any, followed by FLARE's fragment protocol.</summary>
    private static string BuildSystemPrompt(RagOptions opts) =>
        $"{opts.SystemPrompt ?? DefaultSystemPrompt}\n\n{FragmentProtocol}";
```

- [ ] **Step 5: Run the test and the rest of the FLARE suite**

```bash
dotnet build tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Debug
./tests/Rag.NET.Tests/bin/Debug/net10.0/Rag.NET.Tests.exe -class '*FlareAnswerEngineTests*'
```

Expected: PASS, including every pre-existing FLARE test. If an existing test asserted the system message equals the caller's prompt exactly, it is now wrong and must be updated to assert *containment* — record that in the commit body.

- [ ] **Step 6: Mutation check**

Revert `BuildSystemPrompt`'s body to `opts.SystemPrompt ?? DefaultSystemPrompt`, confirm it compiles, re-run the test, confirm it FAILS, then restore.

- [ ] **Step 7: Commit**

```bash
git add src/Rag.NET.AnswerEngines/FlareAnswerEngine.cs \
        tests/Rag.NET.Tests/AnswerGeneration/FlareAnswerEngineTests.cs
git commit -F <message file>
```

Header: `fix(flare): keep the fragment protocol when a caller sets a system prompt`

Body must state that this is reachable by any user whose `SystemPrompt` carries a terminal or formatting instruction, and cite the 256-fold repetition as the observed failure.

---

### Task 2: `GraphExtractionCache` accepts an optional third key field

**Files:**
- Modify: `src/Rag.NET.Benchmarks.Quality/GraphExtractionCache.cs` (`GetOrAddAsync` ~line 233, `ComputeKey` ~line 318)
- Test: `tests/Rag.NET.Benchmarks.Quality.Tests/GraphExtractionCacheTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `Task<string> GetOrAddAsync(string prompt, Func<CancellationToken, Task<string>> generateAsync, string? optionsKey = null, CancellationToken cancellationToken = default)`. Task 3 calls this with a rendered options string.

`ComputeKey` currently hashes two length-prefixed, NUL-terminated fields — `identity` then `prompt`. **Appending a zero-length third field still changes every hash**, because the buffer grows by an `int32` length plus a NUL. The field must be *omitted* when `optionsKey` is null or empty.

- [ ] **Step 1: Capture the golden key from the current implementation**

Before changing anything, record what the current code produces, so the preservation test pins a real value rather than a re-implementation of the algorithm.

Add this temporary test, run it, and copy the printed hash:

```csharp
[Fact]
public void PrintGoldenKey()
{
    var cache = new GraphExtractionCache(
        RootFor(nameof(PrintGoldenKey)), "openai/gpt-4o-mini@t0.0", GraphExtractionCacheMode.Fill);
    Assert.Fail(cache.KeyForTesting("golden-prompt"));
}
```

If no `KeyForTesting` seam exists, add `internal string KeyForTesting(string prompt) => ComputeKey(prompt);` and ensure `Rag.NET.Benchmarks.Quality` already grants InternalsVisibleTo to the test assembly — check the csproj; if it does not, make the golden test call `GetOrAddAsync` and read the resulting file name from disk instead, since `PathFor(key)` shards on the key.

Delete `PrintGoldenKey` once the value is recorded.

- [ ] **Step 2: Write the failing tests**

```csharp
/// <summary>
/// A prompt with no caller options must hash to exactly the key it hashed to before options
/// existed.
/// </summary>
/// <remarks>
/// <b>This is what makes "zero regeneration" a checked claim.</b> All 86,510 entries on disk —
/// 47,322 answers, 35,176 extractions, 4,012 reports — were written by calls that passed no
/// options. Appending a zero-length third field to the key buffer would add an int32 length and a
/// NUL, change every hash, and orphan the lot while appearing to work.
/// </remarks>
[Fact]
public void AKeyWithNoOptions_IsUnchangedFromBeforeOptionsExisted()
{
    var cache = new GraphExtractionCache(
        RootFor(nameof(AKeyWithNoOptions_IsUnchangedFromBeforeOptionsExisted)),
        "openai/gpt-4o-mini@t0.0",
        GraphExtractionCacheMode.Fill);

    Assert.Equal("<paste the golden hash from Step 1>", cache.KeyForTesting("golden-prompt"));
}

/// <summary>Two option strings over one prompt are two entries.</summary>
[Fact]
public void DifferentOptions_DoNotShareAnEntry()
{
    var cache = new GraphExtractionCache(
        RootFor(nameof(DifferentOptions_DoNotShareAnEntry)),
        "openai/gpt-4o-mini@t0.0",
        GraphExtractionCacheMode.Fill);

    Assert.NotEqual(
        cache.KeyForTesting("p", "maxOutputTokens=150"),
        cache.KeyForTesting("p", "maxOutputTokens=300"));
    Assert.NotEqual(cache.KeyForTesting("p"), cache.KeyForTesting("p", "maxOutputTokens=150"));
    Assert.Equal(cache.KeyForTesting("p"), cache.KeyForTesting("p", optionsKey: ""));
}
```

Extend the `KeyForTesting` seam to `internal string KeyForTesting(string prompt, string? optionsKey = null) => ComputeKey(prompt, optionsKey);`.

- [ ] **Step 3: Run and verify they fail**

```bash
dotnet build tests/Rag.NET.Benchmarks.Quality.Tests/Rag.NET.Benchmarks.Quality.Tests.csproj -c Debug
./tests/Rag.NET.Benchmarks.Quality.Tests/bin/Debug/net10.0/Rag.NET.Benchmarks.Quality.Tests.exe \
  -class '*GraphExtractionCacheTests*'
```

Expected: FAIL to compile — `ComputeKey` takes one argument. That is the correct first failure.

- [ ] **Step 4: Implement the optional field**

Replace `ComputeKey`:

```csharp
    /// <param name="optionsKey">
    /// Canonical rendering of the caller's request options, or <see langword="null"/>/empty when the
    /// caller constrained nothing.
    /// <b>Omitted from the buffer entirely when absent, never appended as an empty field</b> — an
    /// empty field still costs an int32 length and a NUL, which would change every key ever written
    /// and orphan the whole cache while appearing to work.
    /// </param>
    private string ComputeKey(string prompt, string? optionsKey = null)
    {
        var identity = Encoding.UTF8.GetBytes(_modelIdentity);
        var promptBytes = Encoding.UTF8.GetBytes(prompt);
        var hasOptions = !string.IsNullOrEmpty(optionsKey);
        var optionsBytes = hasOptions ? Encoding.UTF8.GetBytes(optionsKey!) : [];

        var fields = hasOptions ? 3 : 2;
        var buffer = new byte[
            (fields * (sizeof(int) + 1)) + identity.Length + promptBytes.Length + optionsBytes.Length];

        var offset = AppendField(buffer, 0, identity);
        offset = AppendField(buffer, offset, promptBytes);
        if (hasOptions)
        {
            _ = AppendField(buffer, offset, optionsBytes);
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }
```

Extend `ComputeKey`'s existing `<remarks>` with a sentence recording that the field count varies and that this is unambiguous because fields are length-prefixed *and* NUL-terminated, so a two-field buffer can never be byte-identical to a three-field one.

Then thread the parameter through `GetOrAddAsync`:

```csharp
    public async Task<string> GetOrAddAsync(
        string prompt,
        Func<CancellationToken, Task<string>> generateAsync,
        string? optionsKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(generateAsync);

        var key = ComputeKey(prompt, optionsKey);
```

`optionsKey` is placed **before** `cancellationToken` and defaulted, so every existing call site compiles unchanged. Check for call sites that pass the token positionally — `grep -rn "GetOrAddAsync(" --include=*.cs src/ benchmarks/ tests/ | grep -v /obj/` — and convert any to a named `cancellationToken:` argument.

- [ ] **Step 5: Run the tests**

Same command as Step 3. Expected: PASS, including every pre-existing cache test.

- [ ] **Step 6: Mutation check**

Change `var fields = hasOptions ? 3 : 2;` to `var fields = 3;` and always append the options field. Confirm it compiles. Re-run: `AKeyWithNoOptions_IsUnchangedFromBeforeOptionsExisted` must FAIL with a differing hash — that is the 86,510-entry orphaning caught by a test. Restore.

- [ ] **Step 7: Commit**

```bash
git add src/Rag.NET.Benchmarks.Quality/GraphExtractionCache.cs \
        tests/Rag.NET.Benchmarks.Quality.Tests/GraphExtractionCacheTests.cs
git commit -F <message file>
```

Header: `feat(cache): key entries on caller options without orphaning the existing cache`

---

### Task 3: `CachedGraphRagClient` honours the caller's `ChatOptions`

**Files:**
- Modify: `benchmarks/Rag.NET.Benchmarks.Quality.GraphExtractions/CachedGraphRagClient.cs` (`GetResponseAsync` ~line 181, `CallOnceAsync` ~line 293)
- Test: `tests/Rag.NET.Benchmarks.Quality.Tests/` — new file `CachedGraphRagClientOptionsTests.cs` if no existing client test file is found by `find tests -name "*CachedGraphRagClient*"`.

**Interfaces:**
- Consumes: `GraphExtractionCache.GetOrAddAsync(prompt, generateAsync, optionsKey, cancellationToken)` from Task 2.
- Produces: a client that forwards merged options to the inner `IChatClient`. Task 4 depends on `MaxOutputTokens` actually reaching the model.

`GetResponseAsync` accepts `options` and never reads it; `CallOnceAsync` sends `_options`, the client's own `new ChatOptions { Temperature = temperature }`. The constructor's documentation already states the invariant this task must preserve: the temperature *"is not part of the prompt and therefore not part of the key — the model identity carries it, which is why the two must come from the same place."*

- [ ] **Step 1: Write the failing tests**

```csharp
/// <summary>The caller's options reach the model.</summary>
/// <remarks>
/// Regression test for the 2026-08-29 runaway. <c>FlareAnswerEngine</c> sets
/// <c>MaxOutputTokens = 150</c> with a comment saying it exists to bound rambling models; this
/// client discarded it, so a degenerate generation ran to 86,091 bytes and then timed out.
/// </remarks>
[Fact]
public async Task CallerOptions_ReachTheInnerClient()
{
    var inner = new OptionsRecordingChatClient(reply: "ok");
    var cache = new GraphExtractionCache(
        RootFor(nameof(CallerOptions_ReachTheInnerClient)),
        "openai/gpt-4o-mini@t0.0",
        GraphExtractionCacheMode.Fill);
    using var client = new CachedGraphRagClient(cache, inner, temperature: 0f);

    _ = await client.GetResponseAsync(
        [new ChatMessage(ChatRole.User, "q")],
        new ChatOptions { MaxOutputTokens = 150 });

    Assert.Equal(150, inner.Received?.MaxOutputTokens);
    Assert.Equal(0f, inner.Received?.Temperature);
}

/// <summary>Constrained and unconstrained calls over one prompt are two cache entries.</summary>
[Fact]
public async Task ACallWithOptions_DoesNotHitTheEntryWrittenWithout()
{
    var inner = new OptionsRecordingChatClient(reply: "unconstrained");
    var cache = new GraphExtractionCache(
        RootFor(nameof(ACallWithOptions_DoesNotHitTheEntryWrittenWithout)),
        "openai/gpt-4o-mini@t0.0",
        GraphExtractionCacheMode.Fill);
    using var client = new CachedGraphRagClient(cache, inner, temperature: 0f);

    _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
    inner.Reply = "constrained";
    var second = await client.GetResponseAsync(
        [new ChatMessage(ChatRole.User, "q")],
        new ChatOptions { MaxOutputTokens = 150 });

    Assert.Equal("constrained", second.Text);
}
```

`OptionsRecordingChatClient` implements `IChatClient`, stores the `ChatOptions` it was handed in a `public ChatOptions? Received` property, and returns `Reply`.

- [ ] **Step 2: Run and verify they fail**

Expected: `CallerOptions_ReachTheInnerClient` fails with `Received?.MaxOutputTokens` null. `ACallWithOptions_DoesNotHitTheEntryWrittenWithout` fails returning `"unconstrained"` — the cache-hit proving options are invisible to the key.

- [ ] **Step 3: Merge the options and render the key**

```csharp
    /// <summary>
    /// The caller's options over this client's baseline: the baseline's temperature stays
    /// authoritative because the model identity carries it into every cache key.
    /// </summary>
    private ChatOptions Merge(ChatOptions? callerOptions)
    {
        if (callerOptions is null)
        {
            return _options;
        }

        var merged = callerOptions.Clone();
        merged.Temperature = _options.Temperature;
        return merged;
    }

    /// <summary>
    /// What the caller constrained beyond the baseline, canonically rendered, or an empty string
    /// when it constrained nothing.
    /// </summary>
    /// <remarks>
    /// Empty is the faithful encoding of every entry written before this existed, which is what lets
    /// all 86,510 of them keep their keys. Only fields that can change the response text are
    /// rendered, in a fixed order, so the same request always renders the same string.
    /// </remarks>
    private static string RenderOptionsKey(ChatOptions? callerOptions)
    {
        if (callerOptions is null)
        {
            return string.Empty;
        }

        var parts = new List<string>(3);
        if (callerOptions.MaxOutputTokens is { } maxTokens)
        {
            parts.Add(FormattableString.Invariant($"maxOutputTokens={maxTokens}"));
        }

        if (callerOptions.TopP is { } topP)
        {
            parts.Add(FormattableString.Invariant($"topP={topP}"));
        }

        if (callerOptions.Seed is { } seed)
        {
            parts.Add(FormattableString.Invariant($"seed={seed}"));
        }

        return string.Join(";", parts);
    }
```

`Temperature` is deliberately absent from the rendering: the baseline's value is authoritative and already in the key through the model identity. If `ChatOptions.Clone()` does not exist in Microsoft.Extensions.AI 10.9.0, construct a new `ChatOptions` and copy `MaxOutputTokens`, `TopP`, `Seed`, `StopSequences` and `ResponseFormat` explicitly — verify against the package's API before writing this.

- [ ] **Step 4: Thread them through**

In `GetResponseAsync`, replace the `_cache.GetOrAddAsync` call:

```csharp
        var merged = Merge(options);
        var text = await _cache.GetOrAddAsync(
            GraphExtractionPrompt.Render(sent),
            ct => CallModelAsync(sent, merged, ct),
            RenderOptionsKey(options),
            cancellationToken);
```

Add the parameter to `CallModelAsync` and `CallOnceAsync`, and in `CallOnceAsync` send it:

```csharp
        var response = await _inner!.GetResponseAsync(messages, options, cancellationToken);
```

where `options` is the merged value threaded down, replacing `_options`.

- [ ] **Step 5: Run the tests**

Expected: PASS, plus every pre-existing test in `Rag.NET.Benchmarks.Quality.Tests`.

- [ ] **Step 6: Mutation check**

Change `RenderOptionsKey` to always `return string.Empty;`. Confirm it compiles. `ACallWithOptions_DoesNotHitTheEntryWrittenWithout` must FAIL. Restore.

- [ ] **Step 7: Commit**

Header: `fix(harness): send the caller's chat options instead of discarding them`

---

### Task 4: the FLARE arms apply the contract to the completed answer

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagAnswerTests.cs` (`AnswerThroughEngineAsync` ~line 1439, `EngineAnswerOptions` ~line 1495, `PredictedCallShape` ~line 1609)

**Interfaces:**
- Consumes: Task 1's composed system prompt; Task 3's honoured options.
- Produces: no new public surface. `AnswerThroughEngineAsync` returns the same `string`.

Fixes 1 and 2 stop the runaway; they do not put the contract on FLARE's *answer*. FLARE joins sentences with no final model call (`FlareAnswerEngine.cs:114`), so the contract is applied by the harness in one post-loop call, through the same `counter` so Gate 2 still sees every call.

- [ ] **Step 1: Branch the options and add the formatting call**

In `AnswerThroughEngineAsync`, replace the `engine.AskAsync` line and what follows:

```csharp
        // FLARE's fragments are not complete replies, so the extraction contract cannot ride on
        // them — applied per fragment it makes the model close the answer on every call and never
        // emit <DONE> (2026-08-29: one response carried the closing sentence 256 times). Every
        // other arm emits one complete reply and takes the contract directly.
        var isFlare = string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal)
            || string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal);

        var response = await engine.AskAsync(
            query.Text, sources, isFlare ? FlareLoopOptions : EngineAnswerOptions, ct);

        var answer = isFlare
            ? await ApplyExtractionContractAsync(counter, query.Text, response.Answer, ct)
            : response.Answer;

        AssertCallShapeMatchesPrediction(arm, query.Id, sources.Count, counter.Calls);
        return answer;
    }

    /// <summary>What FLARE's sentence loop runs under: no contract, because fragments are not replies.</summary>
    private static readonly RagOptions FlareLoopOptions = new();

    /// <summary>
    /// Puts FLARE's assembled answer under the same extraction contract every other arm answers
    /// under, in one call after the loop.
    /// </summary>
    /// <remarks>
    /// Counted by Gate 2 like any other call — it goes through the same counting client — so
    /// <see cref="PredictedCallShape"/> carries it in the FLARE bounds rather than the gate being
    /// loosened to hide it.
    /// </remarks>
    private static async Task<string> ApplyExtractionContractAsync(
        IChatClient client, string question, string draft, CancellationToken ct)
    {
        var prompt =
            MultiHopRagAnswerJudge.AnswerInstruction + "\n\n" +
            "Question: " + question + "\n\n" +
            "Draft answer:\n" + draft;

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], null, ct);
        return response.Text;
    }
```

Update `EngineAnswerOptions`'s `<remarks>`: it now applies to every arm **except** the two FLARE arms, and must say why.

- [ ] **Step 2: Update the call-shape gate**

`FlareOptions` defaults are `MaxSentences = 15` and `MaxRetrievals = 3`, so `sentenceCalls = 30`. Every FLARE arm now makes exactly one more call than before, and at least two in total (one sentence call plus the formatting call). In `PredictedCallShape`:

```csharp
        var defaults = new FlareOptions();
        var sentenceCalls = defaults.MaxSentences * 2;

        // +1 for the post-loop extraction-contract call the arm makes through the same counting
        // client; min 2 because that call is unconditional.
        if (string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal))
        {
            return (2, sentenceCalls + 1, FormattableString.Invariant(
                $"FlareAnswerEngine at MaxRetrievals=0 (at most {defaults.MaxSentences} sentences x 2 calls, no regeneration, plus one contract call)"));
        }

        if (string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal))
        {
            return (2, sentenceCalls + defaults.MaxRetrievals + 1, FormattableString.Invariant(
                $"FlareAnswerEngine as shipped (at most {defaults.MaxSentences} sentences x 2 calls, plus one regeneration per lookahead capped at {defaults.MaxRetrievals}, plus one contract call)"));
        }
```

- [ ] **Step 3: Build and run the fast guard tests**

```bash
dotnet build tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj -c Release
./tests/Rag.NET.Benchmarks.Quality.IntegrationTests/bin/Release/net10.0/Rag.NET.Benchmarks.Quality.IntegrationTests.exe \
  -class '*BeirGraphRagAnswerTests*'
```

Expected: 0 warnings; every guard test passes. `Accuracy_AgainstTheGoldAnswers_ThreeArms` skips without the environment variables — that is correct here, it runs in Task 5.

- [ ] **Step 4: Mutation check on the gate**

Revert the two FLARE bounds to their old `(1, sentenceCalls)` / `(1, sentenceCalls + defaults.MaxRetrievals)` values, confirm it compiles, and confirm `AssertCallShapeMatchesPrediction_AcceptsThePredictedShapes_AndRejectsOthers` still passes — if it does, that guard test does not cover the FLARE bounds and must be extended to assert the new maximum before this task is complete. Restore.

- [ ] **Step 5: Commit**

Header: `fix(arms): apply the extraction contract to FLARE's answer, not its fragments`

---

### Task 5: delete the poisoned entry and re-run the pilot

**Files:** none modified. This task produces evidence.

**Interfaces:**
- Consumes: Tasks 1, 3 and 4.
- Produces: the extraction-contract measurement the two failed 2026-08-29 runs never reached.

- [ ] **Step 1: Delete the degenerate cache entry**

```bash
rm ~/.cache/ragnet-beir/graph-answers/37/37a756f63d8154c1fe75252766d08f51a404fbd77171bda19b93ddb41fb6aeb4.gex
```

It holds the contract sentence 256 times across 86,091 bytes. Left in place it replays as a legitimate answer and the judge scores it.

- [ ] **Step 2: Confirm no orphaned runners**

```bash
tasklist | grep -i "Rag.NET" || echo "clean"
```

Kill by assembly name, never `dotnet` or `testhost` — two "stopped" runs once survived that mistake and were found at 5.6 CPU-hours each.

- [ ] **Step 3: Verify the filter narrows**

```bash
./tests/Rag.NET.Benchmarks.Quality.IntegrationTests/bin/Release/net10.0/Rag.NET.Benchmarks.Quality.IntegrationTests.exe \
  -list methods -class '*BeirGraphRagAnswerTests*'
```

Read the class names, not the count. Every listed method must be on `BeirGraphRagAnswerTests`.

- [ ] **Step 4: Run the pilot**

```bash
source ~/.cache/ragnet-beir/env.sh
RAGNET_BEIR_LONG_RUNS=1 \
RAGNET_GRAPHRAG_ANSWERS_GENERATE=1 \
RAGNET_GRAPHRAG_ANSWERS_ARMS=dense,chatengine,mapreduce,refine,flarefixed,flare \
RAGNET_GRAPHRAG_ANSWERS_MAX_QUERIES=10 \
./tests/Rag.NET.Benchmarks.Quality.IntegrationTests/bin/Release/net10.0/Rag.NET.Benchmarks.Quality.IntegrationTests.exe \
  -class '*BeirGraphRagAnswerTests*' > pilot.log 2>&1
echo "EXIT=$?"
```

Expect roughly 20–70 minutes. The store build is an I/O-bound cache replay and varies from 130 s to 2,330 s with page-cache state — it is not a timing signal.

- [ ] **Step 5: Read the result against what it had to settle**

1. **The extraction contract, all six arms.** The 2026-08-28 pilot measured `dense` 9 of 9 and every engine 0 of 9. This is the number the whole task exists for.
2. **No `TaskCanceledException` in `GenerateSentenceAsync`.** Two runs on 2026-08-29 failed there with four each.
3. **The three gates hold** — context identity, call shape, lookahead firing.
4. **Call shapes:** `chatengine` 1, `refine` 6, `mapreduce` 7, FLARE arms one higher than before.

**Publish no accuracy headline.** Nine judged queries is underpowered: RAPTOR's 50-query pilot put its headline at +0.0000 where the full sweep found −0.0146 at p=0.0247.

If the run fails again, **stop and report** rather than retrying — two identical failures already established that a third run is not evidence.

- [ ] **Step 6: Commit the evidence**

No source change. If the run produced notes worth keeping, write them to `docs/plans/2026-08-29-flare-contract-pilot-notes.md` and commit with header `docs(plans): record the re-run pilot's extraction-contract result`.

---

### Task 6: record the finding in the planning files

**Files:**
- Modify: `docs/planning/ROADMAP.md` (Phase 6.2.1 block, ~line 4216-4564)
- Modify: `docs/planning/STATE.md` (Working State, Recommended Next Step, Last completed)

**Interfaces:**
- Consumes: Task 5's measured result.
- Produces: nothing code depends on.

- [ ] **Step 1: Append to Phase 6.2.1's ROADMAP block**

Record, in the block's established register: that the 2026-08-29 pilot re-runs failed twice on a FLARE runaway; that #418's fix caused it and this is the third time in the phase a fix caused the next defect; the evidence (256 repetitions, 86,091 bytes against a 3,747-byte historical maximum); the three fixes; and whatever Task 5 measured. State plainly whether the DoD's answer-engine clause is met — building the arms does not meet it, and neither does a pilot.

- [ ] **Step 2: Update `STATE.md`**

Update **Working State** to the last commit that lands on `main` — the field records what landed, as a SHA with a symbol to verify by content, not a branch name. It was already stale by two PRs (#417, #418) when this session opened. Also update **Last completed** and **Recommended Next Step**.

- [ ] **Step 3: Verify both writes**

Re-read both files and confirm the new content is on disk. A description in conversation does not change a file.

- [ ] **Step 4: Commit**

```bash
git add docs/planning/ROADMAP.md docs/planning/STATE.md
git commit -F <message file>
git status
```

Header: `chore(state): record the FLARE contract defect and the re-run pilot`

Confirm a clean tree.

---

## Self-Review

**Spec coverage.** Fix 1 → Task 1. Fix 2 → Tasks 2 and 3 (cache layer, then client; split because a reviewer could accept the key change and reject the merge semantics). Fix 3 → Task 4. Cache hygiene → Task 5 Step 1. DoD's "FLARE arms meet the contract" → Task 5 Step 5. DoD's "existing keys unchanged" → Task 2 Step 2. DoD's build-clean → Task 4 Step 3. No spec section is unimplemented.

**Placeholders.** None: every code step carries the code, every run step carries the command and the expected outcome. Three steps deliberately require verification against the repo rather than stating a fact — `ChatOptions.Clone()`'s existence in Microsoft.Extensions.AI 10.9.0 (Task 3 Step 3), whether an InternalsVisibleTo seam exists for `KeyForTesting` (Task 2 Step 1), and whether the existing gate test covers the FLARE bounds (Task 4 Step 4). Each names exactly what to check and what to do with either answer.

**Type consistency.** `GetOrAddAsync(prompt, generateAsync, optionsKey, cancellationToken)` is defined in Task 2 and called with that shape in Task 3. `KeyForTesting(prompt, optionsKey)` is introduced in Task 2 Step 1 and extended in Step 2. `RenderOptionsKey` and `Merge` are defined and used within Task 3. `FlareLoopOptions`, `ApplyExtractionContractAsync` and `isFlare` are defined and used within Task 4. `FragmentProtocol` and `BuildSystemPrompt` are defined and used within Task 1. `AnswerArm.Flare` / `AnswerArm.FlareFixed` match the constants in `AnswerArm.cs`.

**Ordering.** Task 3 depends on Task 2's signature; Task 4 depends on Task 1 and Task 3; Task 5 depends on all of them. Tasks 1 and 2 are independent of each other.
