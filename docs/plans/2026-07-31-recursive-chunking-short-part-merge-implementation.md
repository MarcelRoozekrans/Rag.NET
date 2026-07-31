# Recursive Chunking Short-Part Merge Implementation Plan (Phase 3.16)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `RecursiveChunkingStrategy` pack split parts back towards `MaxChunkSize`, stop it splitting text that already fits, and make every emitted chunk an exact substring of its source.

**Architecture:** Mirror LangChain's `RecursiveCharacterTextSplitter._merge_splits` — a fit check before splitting, then greedy packing of same-level siblings rejoined with that level's separator. Parts are no longer trimmed before joining, so a packed chunk reproduces the source exactly and positions stop being a lossy search.

**Tech Stack:** .NET 10, xUnit v3.

**Design:** `docs/plans/2026-07-31-recursive-chunking-short-part-merge-design.md`. Read §0 and §2 before writing any code — §0 lists three faults, not one, and §2 explains why two apparently harmless current behaviours (trimming parts, skipping empties) must both invert.

---

## The thing this plan is really about

**The existing tests assert the defect, and the docs draw it.** `ChunkAsync_SplitsByParagraphsFirst` asserts that a 35-character input produces two chunks with `MaxChunkSize = 200`. It passes today. It must fail after Task 1, and you must change it rather than preserve it.

If you finish a task and every pre-existing test still passes, **you have not fixed anything** — go back and check what you actually changed. This is the sixth phase in this milestone to hit the same shape: code, tests and docs agreeing with each other and all wrong.

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0015, MA0048 (one public type per file, name matches file), MA0006, MA0008, MA0009, MA0132, MA0140, ZA0601 (no `GroupBy`/`OrderBy`/`ToList` in a loop), ZA0501, EPS05/EPS06, EPC12/EPC13, HLQ001/HLQ003/HLQ004/HLQ006/HLQ012/HLQ013, NU1510, RCS1194, CA2022, MA0060. **No new `#pragma` or `SuppressMessage`.**
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One per task.
- **Never `git add -A` or `git add .`** — explicit paths. **Never stage `.claude/worktrees/`**, `.lucent/chunks.json` or `.lucent/embeddings.bin`.
- `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)** after each task.
- **Timestamp trap:** build without `--no-build` and confirm from the log that projects recompiled. A restored or reverted file keeps its old mtime, MSBuild skips it, and you test a stale binary.

**Baselines, nothing skipped:** `Rag.NET.Tests` **1325**, `Rag.NET.Benchmarks.Quality.Tests` **110**, `Rag.NET.Chunking.IntegrationTests` **4**, `Rag.NET.Chunking.Templates.Tests` **51**, `Rag.NET.Parsers.Archive.Tests` **52**, `Rag.NET.Parsers.Email.Tests` **76**, `RepoConventions` **9**.

**Regression gate for the whole phase: the BEIR *parity* numbers must not move.** SciFact 0.64593, ArguAna 0.50432, FiQA 0.37086. The parity protocol indexes one chunk per document and never calls the split path, so if a parity number moves, this phase changed something it had no business touching. That is more important than any new number here.

---

## Task 1: consult the size limit before splitting

**Files:**
- Modify: `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs`
- Modify: `tests/Rag.NET.Tests/Chunking/RecursiveChunkingStrategyTests.cs`

**Step 1: write the failing test**

Add to `RecursiveChunkingStrategyTests`:

```csharp
[Fact]
public async Task ChunkAsync_TextShorterThanMaxChunkSize_IsNotSplitAtAll()
{
    var ct = TestContext.Current.CancellationToken;
    // 35 characters against a 512 limit. There is nothing to split.
    var section = CreateSection("First paragraph.\n\nSecond paragraph.");
    var options = new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 };

    var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

    Assert.Single(chunks);
    Assert.Equal("First paragraph.\n\nSecond paragraph.", chunks[0].Text);
}
```

**Step 2: run it and watch it fail**

`dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RecursiveChunkingStrategyTests"`

Expected: FAIL — 2 chunks, not 1. If it passes, you have the wrong build; see the timestamp trap.

**Step 3: implement**

In `SplitRecursively`, the fit check currently only runs on the branch where the separator is *absent*. Hoist it to the top so it runs unconditionally:

```csharp
private static IEnumerable<string> SplitRecursively(string text, int maxSize, int separatorIndex)
{
    if (text.Length <= maxSize)
    {
        return YieldTrimmed(text);
    }

    if (separatorIndex >= Separators.Length)
    {
        return HardSplitCore(text, maxSize);
    }

    var parts = text.Split(Separators[separatorIndex]);

    if (parts.Length <= 1)
    {
        return SplitRecursively(text, maxSize, separatorIndex + 1);
    }

    return SplitParts(parts, maxSize, separatorIndex);
}
```

Delete `HardSplit` — the fit check above makes its `text.Length <= maxSize` branch unreachable, and leaving dead code that looks like a guard is how the next reader concludes the guard is load-bearing.

**Step 4: run the whole file**

`dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RecursiveChunkingStrategyTests"`

Expected: the new test passes, and **four pre-existing tests now fail** — `ChunkAsync_SplitsByParagraphsFirst`, `ChunkAsync_PreservesDocumentIdAndChunkIndex`, `ChunkAsync_WithOverlap_ChunksOverlap`, `ChunkAsync_TracksPositionsRelativeToSource`. All four use a 35-character input with `MaxChunkSize = 200` and assert two chunks.

**Those four failures are the point of this task.** They were testing splitting with an input that never needed splitting.

**Step 5: fix the four tests by making their input actually need splitting**

Do **not** weaken the assertions. Change `MaxChunkSize` from `200` to `20` in each of the four. `"First paragraph."` is 16 characters and `"Second paragraph."` is 17, so they no longer fit together (16 + 2 + 17 = 35 > 20) and still split into exactly the two chunks each test expects. Every existing assertion in those tests stays intact.

**Step 6: full suite**

`dotnet build Rag.NET.slnx` → 0/0, then `dotnet test tests/Rag.NET.Tests` → **1326** (1325 + 1).

**Step 7: commit**

```
git add src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs tests/Rag.NET.Tests/Chunking/RecursiveChunkingStrategyTests.cs
```

`fix(chunking): consult MaxChunkSize before splitting at all`

Body must record that four existing tests asserted the defect and what changed about them.

---

## Task 2: pack siblings back towards MaxChunkSize

**Files:**
- Modify: `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs`
- Modify: `tests/Rag.NET.Tests/Chunking/RecursiveChunkingStrategyTests.cs`

This is the defect the phase is named for.

**Step 1: write the failing tests** — the three probe cases from design §0:

```csharp
[Fact]
public async Task ChunkAsync_ManyShortLines_PacksThemTowardsMaxChunkSize()
{
    var ct = TestContext.Current.CancellationToken;
    // 60 lines of ~16 characters = 1,010 characters against a 512 limit.
    var text = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"- item number {i}"));
    var section = CreateSection(text);
    var options = new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 };

    var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

    // Before this task: 60 chunks averaging 31 characters.
    Assert.InRange(chunks.Count, 2, 3);
    Assert.All(chunks, c => Assert.True(c.Text.Length <= 512, $"Chunk of {c.Text.Length} exceeds 512"));
}

[Fact]
public async Task ChunkAsync_WordsWithNoSentenceOrLineBreak_DoNotBecomeOneChunkPerWord()
{
    var ct = TestContext.Current.CancellationToken;
    // 150 words, no ". " and no newline, so the recursion reaches the " " separator.
    var text = string.Join(" ", Enumerable.Repeat("word", 150));
    var section = CreateSection(text);
    var options = new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 };

    var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

    // Before this task: 150 chunks of 8 characters. This is the case that proves it is a defect.
    Assert.InRange(chunks.Count, 2, 3);
    Assert.All(chunks, c => Assert.True(c.Text.Length > 400, $"Chunk of {c.Text.Length} was not packed"));
}

[Fact]
public async Task ChunkAsync_SentencesArePackedAndKeepTheirSeparators()
{
    var ct = TestContext.Current.CancellationToken;
    var text = "Alpha one. Bravo two. Charlie three. Delta four.";
    var section = CreateSection(text);
    var options = new ChunkingOptions { MaxChunkSize = 30, Overlap = 0 };

    var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

    // Sentences pack until the next will not fit, and the ". " between packed
    // sentences is restored rather than dropped.
    Assert.Contains(chunks, c => c.Text.Contains(". ", StringComparison.Ordinal));
    Assert.All(chunks, c => Assert.True(c.Text.Length <= 30, $"Chunk of {c.Text.Length} exceeds 30"));
}
```

**Step 2: run and watch all three fail.**

**Step 3: implement.** `SplitParts` gains the separator and a packing buffer. Add `using System.Text;`.

```csharp
private static IEnumerable<string> SplitParts(string[] parts, int maxSize, int separatorIndex)
{
    var separator = Separators[separatorIndex];
    var pending = new List<string>();

    foreach (var part in parts)
    {
        if (part.Length <= maxSize)
        {
            pending.Add(part);
            continue;
        }

        foreach (var packed in Pack(pending, separator, maxSize))
        {
            yield return packed;
        }

        pending.Clear();

        foreach (var sub in SplitRecursively(part, maxSize, separatorIndex + 1))
        {
            yield return sub;
        }
    }

    foreach (var packed in Pack(pending, separator, maxSize))
    {
        yield return packed;
    }
}

private static IEnumerable<string> Pack(List<string> parts, string separator, int maxSize)
{
    if (parts.Count == 0)
    {
        yield break;
    }

    var buffer = new StringBuilder(parts[0]);

    for (var i = 1; i < parts.Count; i++)
    {
        if (buffer.Length + separator.Length + parts[i].Length <= maxSize)
        {
            buffer.Append(separator).Append(parts[i]);
            continue;
        }

        var flushed = buffer.ToString().Trim();
        if (flushed.Length > 0)
        {
            yield return flushed;
        }

        buffer.Clear().Append(parts[i]);
    }

    var last = buffer.ToString().Trim();
    if (last.Length > 0)
    {
        yield return last;
    }
}
```

**Two invariants you must not break, both from design §1 and §2:**

**Parts pack only with siblings from their own level.** When a part is too big, `pending` is flushed *before* recursing, and the recursion's results pass straight through without entering any buffer. Packing a deeper result with the outer separator would join two pieces with a separator that never sat between them in the document — fabricated source text.

**Do not filter empty or whitespace-only parts here.** The old `SplitParts` skipped them. An empty part between two separators is what reproduces a run of blank lines when rejoined; drop it and `"a\n\n\n\nb"` comes back as `"a\n\nb"`, which never appeared in the document. Empties contribute no characters but do re-add their separator. The trailing `.Trim()` on each flushed buffer is what removes them from the chunk's edges.

**Step 4: run the file.** The three new tests pass. `ChunkAsync_FallsBackToSentences_WhenParagraphTooLong` and `ChunkAsync_NoSeparatorFound_HardSplitsAtMaxSize` must still pass unchanged — check, do not assume.

**Step 5: full suite** — `dotnet build Rag.NET.slnx` 0/0, `dotnet test tests/Rag.NET.Tests` → **1329**.

**Step 6: commit** — `fix(chunking): pack split parts back towards MaxChunkSize`

Record the before/after for all three cases in the body. `150 chunks of 8 characters → 2` is the number worth having in the history.

---

## Task 3: every chunk is an exact substring of its source

**Files:**
- Modify: `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs`
- Modify: `tests/Rag.NET.Tests/Chunking/RecursiveChunkingStrategyTests.cs`

Task 2 packs untrimmed parts, but parts are still trimmed *elsewhere* — the `part.Trim()` in the old fits-branch is gone, but check `YieldTrimmed` and `HardSplitCore` and satisfy yourself about what each one can emit.

**Step 1: write the failing test.** This is the property that makes positions meaningful:

```csharp
[Theory]
[InlineData("a  \n\n  b")]
[InlineData("a\n\n\n\nb")]
[InlineData("  leading and trailing  \n\nsecond  ")]
[InlineData("one.  two.   three.")]
[InlineData("tab\there\n\nand\tthere")]
public async Task ChunkAsync_EveryChunkIsAnExactSubstringOfTheSource(string text)
{
    var ct = TestContext.Current.CancellationToken;
    var section = CreateSection(text);
    // Small enough to force splitting, Overlap 0 so chunk text is unmodified.
    var options = new ChunkingOptions { MaxChunkSize = 8, Overlap = 0 };

    var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

    Assert.All(chunks, c => Assert.True(
        text.Contains(c.Text, StringComparison.Ordinal),
        $"Chunk \"{c.Text}\" does not appear in the source \"{text}\""));
}
```

**Step 2: run it.** Any case that fails is a place text is being fabricated. Fix the fabrication, not the test.

**Step 3: add a generated-input property test.** Five hand-picked cases are five cases; the property should hold for everything:

```csharp
[Fact]
public async Task ChunkAsync_EveryChunkIsASubstring_AcrossGeneratedWhitespaceShapes()
{
    var ct = TestContext.Current.CancellationToken;
    var pieces = new[] { "alpha", "b", "  ", "\n", "\n\n", ". ", " ", "gamma delta", "\t", "x." };
    var random = new Random(20260731); // fixed seed — a failure must be reproducible

    for (var iteration = 0; iteration < 500; iteration++)
    {
        var builder = new StringBuilder();
        var pieceCount = random.Next(1, 40);
        for (var i = 0; i < pieceCount; i++)
        {
            builder.Append(pieces[random.Next(pieces.Length)]);
        }

        var text = builder.ToString();
        var options = new ChunkingOptions { MaxChunkSize = random.Next(4, 64), Overlap = 0 };
        var chunks = await _sut.ChunkAsync(CreateSection(text), options, ct).ToListAsync(ct);

        foreach (var chunk in chunks)
        {
            Assert.True(
                text.Contains(chunk.Text, StringComparison.Ordinal),
                $"iteration {iteration}: chunk \"{chunk.Text}\" not in source \"{text}\"");
            Assert.True(
                chunk.Text.Length <= options.MaxChunkSize,
                $"iteration {iteration}: chunk of {chunk.Text.Length} exceeds {options.MaxChunkSize}");
        }
    }
}
```

A fixed seed is deliberate — a random-seeded property test that fails once and passes on re-run tells you nothing.

**Step 4: make it pass.** If the size assertion trips, a pack boundary or the hard-split path is producing an over-long chunk; that is a real bug in Task 2's arithmetic, not a reason to relax the assertion.

**Step 5: full suite** → **1335** (1329 + 5 theory cases + 1).

**Step 6: commit** — `fix(chunking): a chunk is an exact substring of the text it came from`

---

## Task 4: positions stop being a search that can silently lie

**Files:**
- Modify: `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs`
- Modify: `tests/Rag.NET.Tests/Chunking/RecursiveChunkingStrategyTests.cs`

`ChunkAsync` currently does:

```csharp
int pos = sourceText.IndexOf(text, cursor, StringComparison.Ordinal);
if (pos < 0)
{
    pos = cursor;   // a wrong position, reported as a real one
}
```

Task 3 removed the only legitimate reason that fallback existed.

**Step 1: write the failing test** — positions must be exact on whitespace-irregular input, which the existing position test cannot check because its parts are already trim-clean:

```csharp
[Theory]
[InlineData("a  \n\n  b")]
[InlineData("  leading  \n\nsecond  \n\nthird")]
[InlineData("one.  two.   three.")]
public async Task ChunkAsync_PositionsPointAtTheChunkText(string text)
{
    var ct = TestContext.Current.CancellationToken;
    var options = new ChunkingOptions { MaxChunkSize = 8, Overlap = 0 };

    var chunks = await _sut.ChunkAsync(CreateSection(text), options, ct).ToListAsync(ct);

    foreach (var chunk in chunks)
    {
        Assert.InRange(chunk.StartPosition, 0, text.Length);
        Assert.InRange(chunk.EndPosition, chunk.StartPosition, text.Length);
        Assert.Equal(
            chunk.Text,
            text[chunk.StartPosition..chunk.EndPosition]);
    }
}
```

**Step 2: run it.** Failures here are the silent fallback producing positions that point at the wrong text.

**Step 3: implement.** Replace the fallback with a throw. A position that is quietly wrong is worse than a loud failure — `StartPosition`/`EndPosition` feed parent-document retrieval, so a wrong value is a silent data defect that surfaces as bad retrieval much later:

```csharp
var pos = sourceText.IndexOf(text, cursor, StringComparison.Ordinal);
if (pos < 0)
{
    throw new InvalidOperationException(
        FormattableString.Invariant(
            $"Chunk text of {text.Length} characters was not found in the section from offset {cursor}. " +
            "Every chunk must be an exact substring of the section text; this indicates the splitter " +
            "fabricated text rather than reproducing it."));
}
```

Then make `EndPosition` derive from `pos` rather than being computed separately, so the two cannot disagree.

**Step 4: prove the throw does not fire.** Re-run Task 3's generated-input test with position assertions added to it — 500 iterations across random whitespace shapes is the evidence that the throw is unreachable in practice rather than a hazard you have just added to a library.

**Step 5: full suite** → **1338**.

**Step 6: commit** — `fix(chunking): a chunk position can no longer be silently wrong`

---

## Task 5: documentation

**Files:**
- Modify: `docs/guide/chunking.md`
- Modify: `docs/getting-started.md` (only if it describes the split behaviour — check, do not assume)

**The flowchart currently draws the defect.** `docs/guide/chunking.md` around line 70 renders *"Candidate piece → Fits in MaxChunkSize? → yes → Emit chunk"* with no merge step. Redraw it with packing, and with the fit check ahead of the split.

Also record, because none of it is written down anywhere today:

- **Packing**: parts are merged back towards `MaxChunkSize` and rejoined with the separator they were split on.
- **The overlap ceiling** (design §4): overlap is prepended *after* packing, so an emitted chunk can reach `MaxChunkSize + Overlap` — 562 characters at stock options. True before this phase too, documented neither then nor now.
- **The overlap fraction has changed meaning** (design §4): chunks went from ~108 characters with 50 of overlap (46%) to ~512 with 50 (10%). Anyone who tuned `Overlap` against the old fragment sizes gets different behaviour without changing a line.
- **The breaking change** (design §5): chunk boundaries change, so `DeterministicChunkId` changes and **stored vectors must be re-ingested**. State the re-ingestion requirement plainly. Users who skip it get degraded retrieval with nothing to indicate why.
- **The residual punctuation loss** (design §2): each chunk's final sentence still loses its terminal period at a pack boundary. Do not describe punctuation as fixed.

**Commit:** `docs(chunking): packing, the overlap ceiling, and the re-ingestion requirement`

---

## Task 6: re-measure what moved — run this yourself, do not delegate

**Files:**
- Modify: `docs/reference/retrieval-quality.md`

**Do not background this run and do not run it inside a subagent that will exit.** Three agents have already stalled in this milestone waiting on background measurements that died with them. Run it in the foreground with a long timeout.

**Environment:** `RAGNET_ONNX_EMBED_MODEL`, `RAGNET_ONNX_EMBED_VOCAB`, `RAGNET_BEIR_CACHE`, plus `RAGNET_BEIR_LONG_RUNS` to un-gate the real legs.

**Step 1: the parity runs must not move.** SciFact **0.64593**, ArguAna **0.50432**. These never touch the split path. If either moves, stop — this phase changed something outside its scope, and that finding outranks everything below.

**Step 2: re-measure the real legs.** SciFact real and ArguAna real. Chunk counts drop roughly tenfold, so these are far cheaper than 3.12's 17-minute SciFact leg — but they are new chunk texts, so they are new cache misses.

**Step 3: check the prediction from design §6 before recording anything.**

3.12 measured SciFact real **0.65589** (+0.0100 over parity) and ArguAna real **0.42594** (−0.0784), and explained the opposite signs by where relevance lives. That predicts **ArguAna's loss shrinks substantially**, since packing takes its ~1,190-character documents from ~9.5 fragments to ~3.

**If ArguAna does not improve, 3.12's recorded explanation was wrong.** Say so, and correct the roadmap entry and `retrieval-quality.md` rather than leaving the explanation standing next to a number that contradicts it. A prediction that cannot be reported as failed is not a prediction.

**Step 4: update every number.** `docs/reference/retrieval-quality.md` has the old figures at lines ~46–50, ~78–81, ~94–95, ~123–128 and ~199–201. The fan-out table (SciFact 10.9×, FiQA 7.5×, ArguAna 9.5×, max-from-one-document 221/1,723/285) describes the old chunker and must be re-measured, not annotated. `Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments` needs no model and finishes in seconds — use it for the FiQA counts rather than running FiQA's real leg, which stays Phase 3.15's.

**Step 5:** `AssertTheProtocolActuallyChunkedAndAggregated` in `BeirRealChunkingTests` asserts the chunker still chunks and pooling still pools. Both should survive — SciFact's abstracts exceed 512 characters 99.2% of the time and ArguAna's documents average ~1,190 — but **verify rather than assume**. If either now fails, packing has collapsed a corpus into one-chunk-per-document and the real run has silently become the parity run.

**Commit:** `docs(quality): re-measure the real runs under the packing chunker`

---

## Task 7: close the phase and record what the audit found

**Files:**
- Modify: `docs/planning/ROADMAP.md`, `docs/planning/MILESTONE.md`

Flip 3.16 to complete in **both files in the same commit** — 3.10 and 3.7 both shipped with `MILESTONE.md` left at `[pending]`.

Record from design §7, as scheduled debt with its origin rather than as a loose note: **`HierarchicalMergerChunkingStrategy` never reads `MaxChunkSize`.** Its chunks are one heading subtree each and unbounded above. `BookChunkingStrategy`, `LegalChunkingStrategy` and `AcademicPaperChunkingStrategy` all delegate to it, so a user setting `MaxChunkSize` on any of those templates gets no effect from it. Plausibly deliberate — a heading subtree is a semantic unit — but undocumented, and the inverse of the defect this phase fixed.

Also update the roadmap's Phase 3.15 entry: its cost estimate for FiQA's real leg is built on 429,850 chunks, which is now wrong. Give the new count from Task 6 and mark the revised hours as derived from it.

**Commit:** `docs(planning): close phase 3.16 and schedule the HierarchicalMerger finding`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. **Parity unmoved: SciFact 0.64593, ArguAna 0.50432.** Non-negotiable.
3. Every baseline holds, with `Rag.NET.Tests` at its new higher count.
4. No new `#pragma` or `SuppressMessage`.
5. `git status` clean — no dataset, model or embedding file tracked, no `.claude/worktrees/`.

**Report:** every commit hash, verbatim build and test output, the before/after chunk counts for all four probe cases, the re-measured real numbers, **whether ArguAna improved as predicted and what it means if it did not**, and everything this plan got wrong.

That last item is not a formality. Every phase in this milestone has had a plan asserting something the code did not do — including this one's own design, which claimed the rejoin fixed punctuation "for free" before it was corrected to a tenfold reduction. If the four expected test failures in Task 1 do not appear, or appear in different tests, **say so** rather than adjusting quietly.
