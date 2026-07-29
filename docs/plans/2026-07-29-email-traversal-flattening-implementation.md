# Email Traversal Flattening Implementation Plan (Phase 3.9)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the stack-recursive embedded-message traversal in both email parsers with one shared depth-first driver over an explicit stack, so nesting depth costs heap rather than stack.

**Architecture:** An internal generic driver holds a `Stack<Frame<TMessage>>`, where a frame is a message plus its position in that message's child list. Per-library differences live in two thin adapters (`IMessageAdapter<TMessage>`); the decision to descend lives behind an injected `IDescentPolicy`, wired to `EmbeddedMessageContext` in production and to always-yes in tests.

**Tech Stack:** .NET 10, MimeKit, MsgReader, xUnit v3.

**Design:** `docs/plans/2026-07-29-email-traversal-flattening-design.md`. Read it first — especially §0, which records why this debt is being worked twice, and §3, which is the only reason the descent policy is a seam at all.

---

## Conventions

- Warnings are errors: MA0051 (**≤60-line methods — this plan's driver loop will press on it**), MA0015, MA0048 (one public type per file, name must match), MA0006 (`string.Equals` not `==`), MA0008, MA0009, MA0132, MA0140, ZA0601/ZA0501, EPS05/EPS06 (ValueTask hidden copies), EPC12/EPC13, HLQ004/HLQ012/HLQ013, NU1510.
- **No new `#pragma` or `SuppressMessage`.** `MsgDocumentParser` has one existing HLQ012 pragma around `foreach (var item in message.Attachments)` — MsgReader's `Attachments` is a `List<object>`. That one stays; if the adapter moves that loop, the pragma moves with it and gains no siblings.
- All logging goes through the `LoggerMessage` source-gen in `EmailParserLog.cs`. Never `logger.LogX` directly.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A` or `git add .`** — explicit paths. `.lucent/chunks.json` and `.lucent/embeddings.bin` are expected dirty; leave them.

Verify after each task: `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)**.

Baselines: `Rag.NET.Parsers.Email.Tests` **64**, `Rag.NET.Tests` **1308**, `Rag.NET.RepoConventions.Tests` **9**.

**Timestamp trap.** If you restore a file from git or a backup to compare behaviour, the mtime may make MSBuild skip recompiling, so `--no-build` then tests a stale binary. Touch the file, or build without `--no-build`, and confirm from the log that the project actually recompiled. This has produced a false result in this repository before.

---

## The one sequencing rule

**Task 2 must be written and green against the current recursive code before Task 3 begins.** It captures the section ordering that exists today. Written afterwards, it would merely document whatever the new code happens to do, which is not a test — it is a transcript. If you find yourself writing it after the refactor, stop and reorder.

---

## Task 1: the driver, proved at 10,000 levels

**Files:**
- Create: `src/Rag.NET.Parsers.Email/EmbeddedTraversal.cs`
- Create: `src/Rag.NET.Parsers.Email/IMessageAdapter.cs`
- Create: `src/Rag.NET.Parsers.Email/IDescentPolicy.cs`
- Create: `tests/Rag.NET.Parsers.Email.Tests/EmbeddedTraversalTests.cs`

Nothing in this task touches either parser. The driver is built against a fake adapter first, so it is designed to a test rather than retrofitted to the code it will replace.

**Step 1: write the depth test.**

```csharp
private sealed record FakeMessage(int Depth);

// Adapter yielding a single embedded child per level until MaxDepth, then nothing.
// Children are produced lazily — do not materialise 10,000 messages up front.

[Fact]
public async Task ATenThousandLevelChainDoesNotConsumeStack()
{
    // The claim this whole phase exists to make. The overflow floor is ~500 levels and the
    // ceiling is 64, so no test reaching through EmailParserOptions can construct a case that
    // would ever have overflowed — see the design, §3. Driving the traversal directly is the
    // only way to reach a depth that proves anything.
    //
    // IF THIS TEST TERMINATES THE TEST RUNNER RATHER THAN FAILING, THAT IS THE SIGNAL.
    // STATUS_STACK_OVERFLOW is uncatchable, so recursion reintroduced into the traversal kills
    // the process instead of reporting a failure. It is not flakiness. Read the traversal.
    var sections = await Drive(depth: 10_000);

    Assert.Equal(10_001, sections.Count);
}
```

**Step 2: run it.** Expected: **does not compile** — `EmbeddedTraversal` does not exist. That is this task's "red".

**Step 3: build the three types.**

`IMessageAdapter<TMessage>` — everything library-specific:

```csharp
internal interface IMessageAdapter<TMessage>
{
    string? GetSubject(TMessage message);

    IAsyncEnumerable<DocumentSection> ReadBodyAsync(
        TMessage message, DocumentMetadata metadata, CancellationToken cancellationToken);

    /// <summary>Children in document order. Disposed by the driver when its frame is popped.</summary>
    IEnumerator<MessageChild<TMessage>> ReadChildren(TMessage message);
}
```

`MessageChild<TMessage>` — one child, either a nested message or a file attachment. `EmbeddedMessage` non-null means descend:

```csharp
internal sealed class MessageChild<TMessage>
{
    public TMessage? EmbeddedMessage { get; init; }
    public required string Name { get; init; }
    public string? MimeType { get; init; }
    public Func<CancellationToken, ValueTask<Stream>>? OpenAsync { get; init; }
}
```

`OpenAsync` is deferred rather than a ready `Stream` because MimeKit needs an async `DecodeToAsync` to produce one and MsgReader hands over a `byte[]`. Opening eagerly would decode every attachment in a message before the first is dispatched.

`IDescentPolicy` — the seam:

```csharp
internal interface IDescentPolicy
{
    /// <summary>The context the child is parsed under, or null to skip the branch.</summary>
    EmbeddedMessageContext? TryDescend(EmbeddedMessageContext parent, string name);
}
```

The driver, over `Stack<Frame<TMessage>>`:

```csharp
private sealed class Frame<TMessage>
{
    public required TMessage Message { get; init; }
    public required EmbeddedMessageContext Context { get; init; }
    public required IEnumerator<MessageChild<TMessage>> Children { get; init; }
    public bool HeaderEmitted { get; set; }
}
```

Loop shape — `Peek`, emit header once, advance children, `Pop` only when exhausted:

```
while (stack.Count > 0)
{
    cancellationToken.ThrowIfCancellationRequested();
    var frame = stack.Peek();

    if (!frame.HeaderEmitted)
    {
        frame.HeaderEmitted = true;
        // subject section, then adapter.ReadBodyAsync — await foreach inline, not pushed
    }

    if (!frame.Children.MoveNext()) { stack.Pop().Children.Dispose(); continue; }

    var child = frame.Children.Current;
    if (child.EmbeddedMessage is { } embedded)
    {
        var childContext = policy.TryDescend(frame.Context, child.Name);
        if (childContext is not null)
            stack.Push(NewFrame(embedded, childContext));
        continue;
    }

    // file attachment: open, then await foreach EmailAttachmentDispatcher.DispatchAsync inline
}
```

**`Peek`-then-`Pop`-when-exhausted is the ordering guarantee.** A parent's child *i* expands completely before *i+1* is touched, which is what the recursion does. Do not restructure this into "pop, process, push back" — it is the same thing said less clearly, and it is easy to get subtly wrong.

Body sub-parses and dispatcher hops are `await foreach`-ed **inline**, not pushed onto the stack. They are bounded; pushing them would need the stack to hold two different frame kinds for no gain.

**Disposal:** wrap the whole loop in `try`/`finally` and dispose every frame left on the stack. `yield return` inside `try`/`finally` is legal in C# iterators and `finally` runs on `DisposeAsync`, so a caller that breaks out of the `await foreach` still releases every enumerator. The design calls this the most likely place for a leak; write it first, not last.

**MA0051** caps methods at 60 lines. Expect to extract the header emission and the file-attachment dispatch into helpers.

**Step 4: run the test.** Expected: **PASS**, in well under a second.

**Step 5: prove the test can fail.** This is the acceptance criterion for the whole phase, so verify it rather than assuming.

Temporarily replace the driver's descent with a recursive call — a private `async IAsyncEnumerable<DocumentSection> RecurseAsync(...)` that `await foreach`es itself for each embedded child — and run the test. **Expect the test runner process to terminate**, not a red test. Record the exit code you observe. Then revert.

A test that passes but cannot fail is the defect this milestone has found more often than any other; this one costs a minute to check.

**Step 6: also pin ordering and limits at the driver level.** Add tests for: children emitted depth-first in document order; a policy returning `null` skips exactly that branch and continues with the next sibling; frames are disposed when the consumer abandons the enumeration early (a fake enumerator recording `Dispose`).

**Commit:** `feat(email): depth-first traversal driver over an explicit stack`

---

## Task 2: capture today's section ordering — before anything changes

**Files:**
- Create: `tests/Rag.NET.Parsers.Email.Tests/EmbeddedMessageOrderingTests.cs`

**This task runs against the current recursive parsers and must be green before Task 3 starts.**

Build a multi-branch fixture with `EmlFixtureBuilder.CreateNested`, which takes both `attachments` and an `embedded` message. Note it adds file attachments **before** the embedded `MessagePart`, so the emitted order follows that.

The shape to build — deliberately wide *and* deep, because a flattening bug shows up as depth-first quietly becoming breadth-first, which only a multi-branch fixture with siblings after a descent can catch:

```
root
  body
  attachment A            (text/plain, via FakeTextParser)
  embedded child 1
      body
      attachment B
      embedded grandchild
          body
  attachment C
  embedded child 2
      body
```

**Assert the exact section sequence**, not a count:

```csharp
Assert.Equal(
    ["Root Subject", "root body", "A", "Child 1", "child 1 body", "B", ...],
    sections.Select(s => s.Text).ToArray());
```

A count assertion passes while the order is wrong, which is the entire failure mode this test exists to catch. Assert `SectionIndex` is `0..n-1` contiguous in emission order too — it is stamped exactly once in `ParseAsync` and must stay that way.

Do the same for `MsgDocumentParser` with `MsgFixtureBuilder`.

**Run against current code. Expected: PASS.** If it fails, you have misread the current ordering — fix the expectation to match today's behaviour, not the behaviour you think is right. This test's whole value is that it was written before the change.

**Commit:** `test(email): pin embedded-message section ordering before flattening`

---

## Task 3: EML adapter, and wire EmailDocumentParser to the driver

**Files:**
- Create: `src/Rag.NET.Parsers.Email/MimeMessageAdapter.cs`
- Create: `src/Rag.NET.Parsers.Email/EmbeddedMessageDescentPolicy.cs`
- Modify: `src/Rag.NET.Parsers.Email/EmailDocumentParser.cs`

The adapter wraps MimeKit: `GetSubject` → `message.Subject`; `ReadBodyAsync` → the existing `ParseBodyAsync` logic (prefer `TextBody`, fall back to `HtmlBody` through `HtmlDocumentParser`); `ReadChildren` walks `message.Attachments`, mapping a `MessagePart` to `EmbeddedMessage` and a `MimePart` to a file child whose `OpenAsync` runs `DecodeToAsync` into a `MemoryStream`.

Carry across, unchanged, the two rules the current code applies: a `MimePart` with a blank `FileName` or null `Content` is skipped, and the child's name for an embedded message is `nested.Subject`, falling back to `embedded.ContentDisposition?.FileName`, falling back to `"(no subject)"`.

The production policy:

```csharp
internal sealed class EmbeddedMessageDescentPolicy(string extension, string contentType, ILogger? logger)
    : IDescentPolicy
{
    public EmbeddedMessageContext? TryDescend(EmbeddedMessageContext parent, string name)
    {
        if (!parent.TryEnterEmbedded(name, logger))
            return null;

        return parent.Descend(EmbeddedMessageMetadata.Create(parent.Metadata, name, extension, contentType));
    }
}
```

Depth accounting, budget accounting and the warning behaviour are all `TryEnterEmbedded`'s, exactly as now — this moves the *call site*, not the rules.

`ParseAsync` keeps stamping `SectionIndex` exactly once and now enumerates the driver:

```csharp
await foreach (var section in EmbeddedTraversal.RunAsync(message, adapter, context, policy, parsers, logger, cancellationToken))
    yield return section with { SectionIndex = sectionIndex++ };
```

Delete `ParseMessageAsync`, `ParseAttachmentsAsync` and `ParseEmbeddedAsync` from the parser. `ParseBodyAsync` moves into the adapter.

**Run the full email suite.** Expected: all pass, including Task 2's ordering tests and the 12 existing `EmbeddedMessageRecursionTests`.

**If an existing recursion test fails, stop and report rather than editing it.** Those tests encode depth limits, budget-across-siblings, tag non-leakage and the clamp at parse time. This phase changes none of that, so a failure means the refactor moved behaviour the design said it would not.

**Commit:** `refactor(email): flatten the EML embedded-message traversal`

---

## Task 4: MSG adapter, and wire MsgDocumentParser to the driver

**Files:**
- Create: `src/Rag.NET.Parsers.Email/StorageMessageAdapter.cs`
- Modify: `src/Rag.NET.Parsers.Email/MsgDocumentParser.cs`

The same shape over MsgReader. Three things differ and all three must survive:

1. `Storage.Message.Attachments` is a `List<object>` mixing `Storage.Message` and `Storage.Attachment`. The existing HLQ012 pragma moves here with the loop.
2. `attachment.MimeType` comes from `PidTagAttachMimeTag`, which senders often omit — the existing `MimeTypeMap.FromFileName` fallback stays.
3. **A nested `Storage.Message` is deliberately not disposed.** It belongs to the outer message's `Attachments` collection, and disposing it while enumerating would destroy data the caller may still read. The current code carries a long comment explaining this, probed against MsgReader 6.1.0 — carry the comment across with the code. The driver disposes *enumerators*, never messages; make sure the adapter's enumerator disposal does not reach through to a `Storage.Message`.

`EmbeddedMessageDescentPolicy` is reused with `".msg"` and `MsgContentType`.

**Run the full email suite.** Same stop condition as Task 3.

**Commit:** `refactor(email): flatten the MSG embedded-message traversal`

---

## Task 5: correct the documentation the flattening falsifies

**Files:**
- Modify: `src/Rag.NET.Parsers.Email/EmailParserOptions.cs` — the `<remarks>` on `MaxSupportedEmbeddedDepth`
- Modify: `docs/reference/features.md` — the Email File Parser section
- Modify: `docs/planning/ROADMAP.md`

**No code changes. The ceiling stays at 64.**

The XML on `MaxSupportedEmbeddedDepth` currently explains a stack-recursive traversal and was rewritten only one phase ago. It is now wrong in the other direction: **the in-place path is no longer recursive at all.** Rewrite it to say what the ceiling now bounds — a third-party parser registered for a message content type, reached through the dispatcher path, which still costs a bounded handful of frames per hop — plus fan-out sanity. Keep the ~500 measurement as history, clearly marked as the floor for the *old* traversal.

`features.md` says, in the Email File Parser section: *"The ceiling is not adjustable: the traversal is stack-recursive, and 64 sits an order of magnitude below the depth at which it overflows."* Both clauses are now false. Correct it. Users need to know the ceiling exists and why it is not adjustable; they do not need the internal argument.

`ROADMAP.md`: move the **Stack-recursive email traversal** entry from the open follow-up-debts list to `### Closed`, this time **as implemented**. Record that it was closed once before on a false premise and reopened — a reader who finds only "closed" cannot tell this entry has a history worth knowing. Flip Phase 3.9 to `[status: complete]` with a `**Completed:**` line in the style of the other phases.

Do **not** flip `MILESTONE.md` — that happens after the whole-phase review, along with any ROADMAP status the reviewer's findings change.

**Commit:** `docs: close the email traversal debt, this time implemented`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)**.
2. `Rag.NET.Parsers.Email.Tests` at its new count (64 + what you added); `Rag.NET.Tests` **1308**; `Rag.NET.RepoConventions.Tests` **9**.
3. Confirm `EmailDocumentParser.cs` and `MsgDocumentParser.cs` contain no method that calls itself, directly or through a helper — the point of the phase.
4. Confirm no new `#pragma` or `SuppressMessage`: `git diff <base>..HEAD | grep -c "pragma\|SuppressMessage"` should count only the HLQ012 pragma moving.
5. Re-run the Task 1 depth test one final time on the merged state.

**Report:** every commit hash, verbatim build and test output, the exit code you observed when you made the driver recursive in Task 1 Step 5, the exact section sequence Task 2 captured, and everything this plan got wrong. The last one matters — the previous two phases each had a plan that asserted something the code did not do, and both were caught only because the implementer ran the plan's own snippet and watched it disagree.
