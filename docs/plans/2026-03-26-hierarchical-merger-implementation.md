# Hierarchical Merger Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `HierarchicalMergerChunkingStrategy` that merges document sections into heading-subtree chunks, selected via `builder.UseHierarchicalMerging(...)`.

**Architecture:** New `IDocumentChunkingStrategy` interface takes the full section stream; `HierarchicalMergerChunkingStrategy` implements it with a streaming heading-stack algorithm; `ParseBehavior` is updated with a one-branch check to route document-level strategies differently from per-section strategies.

**Note:** The design doc referenced `ChunkingBehavior` as the modification point. After reading the code, the correct file is `ParseBehavior` — that is where `IChunkingStrategy.ChunkAsync` is called per section.

**Tech Stack:** C# 13, `System.Text.RegularExpressions`, `xUnit`, `TestContext.Current.CancellationToken`

---

### Task 1: `IDocumentChunkingStrategy` interface + `HierarchicalMergerOptions`

**Files:**
- Create: `src/Rag.NET/Abstractions/IDocumentChunkingStrategy.cs`
- Create: `src/Rag.NET/Models/Options/HierarchicalMergerOptions.cs`
- Create: `tests/Rag.NET.Tests/Models/Options/HierarchicalMergerOptionsTests.cs`

**Step 1: Write the failing test**

`tests/Rag.NET.Tests/Models/Options/HierarchicalMergerOptionsTests.cs`:
```csharp
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models.Options;

public class HierarchicalMergerOptionsTests
{
    [Fact]
    public void MaxDepth_DefaultsToTwo()
    {
        var opts = new HierarchicalMergerOptions();
        Assert.Equal(2, opts.MaxDepth);
    }

    [Fact]
    public void HeadingPatterns_DefaultsToNull()
    {
        var opts = new HierarchicalMergerOptions();
        Assert.Null(opts.HeadingPatterns);
    }
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~HierarchicalMergerOptionsTests" --no-build
```

Expected: compile error — `HierarchicalMergerOptions` not found.

**Step 3: Write minimal implementation**

`src/Rag.NET/Abstractions/IDocumentChunkingStrategy.cs`:
```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Document-level chunking strategy that receives the full section stream for a document.
/// Use when chunking decisions require cross-section context (e.g. heading-tree merging).
/// <see cref="IChunkingStrategy"/> operates per-section; this interface operates per-document.
/// </summary>
public interface IDocumentChunkingStrategy
{
    IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions options,
        CancellationToken cancellationToken = default);
}
```

`src/Rag.NET/Models/Options/HierarchicalMergerOptions.cs`:
```csharp
namespace Rag.NET.Models.Options;

public sealed class HierarchicalMergerOptions
{
    /// <summary>
    /// Maximum heading depth treated as chunk boundaries.
    /// Headings deeper than this are folded into their nearest in-scope ancestor as body text.
    /// </summary>
    public int MaxDepth { get; init; } = 2;

    /// <summary>
    /// Per-level regex patterns used when <see cref="DocumentSection.HeadingLevel"/> is null.
    /// <c>HeadingPatterns[0]</c> = level-1 patterns, <c>HeadingPatterns[1]</c> = level-2 patterns, etc.
    /// <see langword="null"/> means rely on the parser's heading level metadata only.
    /// </summary>
    public string[][]? HeadingPatterns { get; init; }
}
```

**Step 4: Run test to verify it passes**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~HierarchicalMergerOptionsTests" --no-build
```

Expected: PASS (2 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET/Abstractions/IDocumentChunkingStrategy.cs \
        src/Rag.NET/Models/Options/HierarchicalMergerOptions.cs \
        tests/Rag.NET.Tests/Models/Options/HierarchicalMergerOptionsTests.cs
git commit -m "feat: add IDocumentChunkingStrategy interface and HierarchicalMergerOptions"
```

---

### Task 2: Implement `HierarchicalMergerChunkingStrategy`

**Files:**
- Create: `src/Rag.NET/Chunking/HierarchicalMergerChunkingStrategy.cs`
- Create: `tests/Rag.NET.Tests/Chunking/HierarchicalMergerChunkingStrategyTests.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/Chunking/HierarchicalMergerChunkingStrategyTests.cs`:
```csharp
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class HierarchicalMergerChunkingStrategyTests
{
    private static readonly ChunkingOptions DefaultOptions = new();
    private static readonly DocumentId DocId = new("doc-1");

    private static DocumentSection Heading(string text, int level) => new()
    {
        Text = text, DocumentId = DocId, SectionIndex = 0, Heading = text, HeadingLevel = level
    };

    private static DocumentSection Body(string text) => new()
    {
        Text = text, DocumentId = DocId, SectionIndex = 0
    };

    private static async IAsyncEnumerable<DocumentSection> Sections(
        params DocumentSection[] sections)
    {
        foreach (var s in sections)
            yield return s;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChunkDocumentAsync_ThreeH1Sections_ProducesThreeChunks()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions { MaxDepth = 1 });

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Heading("Section A", 1), Body("Body A"),
                Heading("Section B", 1), Body("Body B"),
                Heading("Section C", 1), Body("Body C")),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, chunks.Count);
        Assert.Contains("Section A", chunks[0].Text);
        Assert.Contains("Body A", chunks[0].Text);
        Assert.Contains("Section B", chunks[1].Text);
        Assert.Contains("Section C", chunks[2].Text);
    }

    [Fact]
    public async Task ChunkDocumentAsync_H1ThenH2ThenH3_MaxDepth2_MergesH3IntoH2()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions { MaxDepth = 2 });

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Heading("Chapter", 1), Body("Intro"),
                Heading("Section", 2), Body("Section body"),
                Heading("Subsection", 3), Body("Sub body")),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // H1 chunk: "Chapter\n\nIntro"
        // H2 chunk: "Section\n\nSection body\n\nSubsection\n\nSub body" (H3 folded in)
        Assert.Equal(2, chunks.Count);
        Assert.Contains("Chapter", chunks[0].Text);
        Assert.Contains("Section", chunks[1].Text);
        Assert.Contains("Sub body", chunks[1].Text);
    }

    [Fact]
    public async Task ChunkDocumentAsync_BodyBeforeFirstHeading_EmittedAsChunkWithNoPrefix()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Body("Preamble text"),
                Heading("First heading", 1), Body("Under heading")),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Preamble text", chunks[0].Text);
        Assert.DoesNotContain("\n\n", chunks[0].Text); // no heading prefix
    }

    [Fact]
    public async Task ChunkDocumentAsync_EmptySectionStream_ProducesNoChunks()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());

        var chunks = await sut.ChunkDocumentAsync(
            AsyncEnumerable.Empty<DocumentSection>(),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkDocumentAsync_RegexFallback_DetectsHeadingsWhenLevelIsNull()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions
        {
            MaxDepth = 1,
            HeadingPatterns = [["^# "]]  // level-1 regex
        });

        // Sections have no HeadingLevel set — regex must detect them
        var plain1 = new DocumentSection { Text = "# Alpha", DocumentId = DocId, SectionIndex = 0 };
        var body1  = new DocumentSection { Text = "Content A", DocumentId = DocId, SectionIndex = 1 };
        var plain2 = new DocumentSection { Text = "# Beta",  DocumentId = DocId, SectionIndex = 2 };
        var body2  = new DocumentSection { Text = "Content B", DocumentId = DocId, SectionIndex = 3 };

        var chunks = await sut.ChunkDocumentAsync(
            Sections(plain1, body1, plain2, body2),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("Content A", chunks[0].Text);
        Assert.Contains("Content B", chunks[1].Text);
    }

    [Fact]
    public async Task ChunkDocumentAsync_SetsHeadingMetadata()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());

        var chunks = await sut.ChunkDocumentAsync(
            Sections(Heading("My Heading", 1), Body("body")),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.True(chunks[0].Metadata.TryGetValue("heading", out var h));
        Assert.Equal("My Heading", h);
        Assert.True(chunks[0].Metadata.TryGetValue("heading_level", out var level));
        Assert.Equal("1", level);
    }

    [Fact]
    public async Task ChunkAsync_PerSectionFallback_ReturnsEachSectionAsOneChunk()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());
        var section = new DocumentSection
        {
            Text = "some text",
            DocumentId = DocId,
            SectionIndex = 3,
        };

        var chunks = await sut.ChunkAsync(section, DefaultOptions, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.Equal("some text", chunks[0].Text);
        Assert.Equal(3, chunks[0].ChunkIndex);
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~HierarchicalMergerChunkingStrategyTests" --no-build
```

Expected: compile error — `HierarchicalMergerChunkingStrategy` not found.

**Step 3: Write minimal implementation**

`src/Rag.NET/Chunking/HierarchicalMergerChunkingStrategy.cs`:
```csharp
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

/// <summary>
/// Merges document sections into heading-subtree chunks using a streaming heading-stack algorithm.
/// Each chunk covers one heading and all body text under it up to <see cref="HierarchicalMergerOptions.MaxDepth"/>.
/// Implements <see cref="IDocumentChunkingStrategy"/> for pipeline use and
/// <see cref="IChunkingStrategy"/> as a per-section fallback.
/// </summary>
public sealed class HierarchicalMergerChunkingStrategy(HierarchicalMergerOptions options)
    : IDocumentChunkingStrategy, IChunkingStrategy
{
    private readonly Regex[][]? _compiledPatterns = options.HeadingPatterns is null ? null :
        options.HeadingPatterns
            .Select(level => level
                .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.Multiline))
                .ToArray())
            .ToArray();

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions _,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new StringBuilder();
        var currentHeading = string.Empty;
        var currentLevel = int.MaxValue;
        var chunkIndex = 0;
        DocumentId? documentId = null;

        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            documentId ??= section.DocumentId;
            var level = DetectLevel(section);

            if (level is not null && level <= options.MaxDepth)
            {
                // Flush accumulated buffer as a chunk before starting the new heading
                if (buffer.Length > 0 || currentHeading.Length > 0)
                    yield return BuildChunk(documentId, chunkIndex++, currentHeading, currentLevel, buffer);

                currentHeading = section.Heading ?? StripMarkdownPrefix(section.Text);
                currentLevel = level.Value;
                buffer.Clear();
            }
            else
            {
                // Body text or heading deeper than MaxDepth — fold into current chunk
                if (buffer.Length > 0)
                    buffer.AppendLine();
                buffer.Append(section.Text.Trim());
            }
        }

        // Flush the final accumulated chunk
        if (buffer.Length > 0 || currentHeading.Length > 0)
            yield return BuildChunk(documentId ?? new DocumentId("unknown"), chunkIndex, currentHeading, currentLevel, buffer);
    }

    /// <inheritdoc/>
    /// <remarks>Fallback implementation: emits each section as a single chunk without merging.</remarks>
    public IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions _,
        CancellationToken cancellationToken = default)
    {
        TextChunk[] result =
        [
            new TextChunk
            {
                Text = section.Text,
                DocumentId = section.DocumentId,
                ChunkIndex = section.SectionIndex,
            }
        ];
        return result.ToAsyncEnumerable();
    }

    private int? DetectLevel(DocumentSection section)
    {
        // Prefer parser-supplied heading level
        if (section.HeadingLevel.HasValue)
            return section.HeadingLevel;

        // Fall back to user-supplied regex patterns
        if (_compiledPatterns is null)
            return null;

        for (var i = 0; i < _compiledPatterns.Length; i++)
            foreach (var regex in _compiledPatterns[i])
                if (regex.IsMatch(section.Text))
                    return i + 1;

        return null;
    }

    private static TextChunk BuildChunk(DocumentId docId, int index, string heading, int level, StringBuilder body)
    {
        var bodyText = body.ToString().Trim();
        var text = heading.Length > 0
            ? $"{heading}\n\n{bodyText}"
            : bodyText;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (heading.Length > 0)
        {
            metadata["heading"] = heading;
            if (level < int.MaxValue)
                metadata["heading_level"] = level.ToString(CultureInfo.InvariantCulture);
        }

        return new TextChunk
        {
            Text = text,
            DocumentId = docId,
            ChunkIndex = index,
            Metadata = metadata,
        };
    }

    private static string StripMarkdownPrefix(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('#').Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return text.Trim();
    }
}
```

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~HierarchicalMergerChunkingStrategyTests" --no-build
```

Expected: PASS (7 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET/Chunking/HierarchicalMergerChunkingStrategy.cs \
        tests/Rag.NET.Tests/Chunking/HierarchicalMergerChunkingStrategyTests.cs
git commit -m "feat: implement HierarchicalMergerChunkingStrategy with streaming heading-stack"
```

---

### Task 3: Update `ParseBehavior` to support `IDocumentChunkingStrategy`

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/ParseBehaviorDocumentChunkingTests.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/Ingestion/ParseBehaviorDocumentChunkingTests.cs`:
```csharp
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class ParseBehaviorDocumentChunkingTests
{
    private static IngestionContext MakeContext(Stream stream) => new()
    {
        Stream = stream,
        Metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("doc-1"),
            ContentType = "text/plain",
        },
        GetNextBm25DocId = () => 0,
    };

    private static ValueTask<IngestionResult> NoopNext(IngestionContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    [Fact]
    public async Task HandleAsync_WithDocumentChunkingStrategy_CallsChunkDocumentAsync()
    {
        // Arrange: a strategy that implements both interfaces
        var strategy = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());

        // Plain-text parser produces a single section
        var sut = BuildParseBehavior(strategy);

        var ctx = MakeContext(new MemoryStream("hello world"u8.ToArray()));

        // Act
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NoopNext);

        // Assert: chunks were produced (one per section from the plain-text parser)
        Assert.NotEmpty(ctx.Chunks);
        Assert.NotEmpty(ctx.Sections);
    }

    [Fact]
    public async Task HandleAsync_WithPerSectionStrategy_PopulatesChunksAndSections()
    {
        var strategy = new RecursiveChunkingStrategy();
        var sut = BuildParseBehavior(strategy);

        var ctx = MakeContext(new MemoryStream("hello world"u8.ToArray()));
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NoopNext);

        Assert.NotEmpty(ctx.Chunks);
        Assert.NotEmpty(ctx.Sections);
    }

    private static ParseBehavior BuildParseBehavior(IChunkingStrategy strategy)
    {
        // ParseBehavior uses [Inject] property injection from ZeroAlloc.Inject.
        // Construct directly and set properties for testing.
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        var parser = new Rag.NET.Parsers.PlainTextDocumentParser();
        var behavior = new ParseBehavior
        {
            Parsers = [parser],
            ChunkingStrategy = strategy,
            ChunkingOptions = new ChunkingOptions(),
        };
        return behavior;
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ParseBehaviorDocumentChunkingTests" --no-build
```

Expected: compile errors or test failures — `ParseBehavior` doesn't support `IDocumentChunkingStrategy` yet.

**Step 3: Update `ParseBehavior`**

Modify `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs` — add a branch for `IDocumentChunkingStrategy`:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ParseBehavior : IIngestionBehavior
{
    [Inject] public IEnumerable<IDocumentParser> Parsers { get; set; } = null!;
    [Inject] public IChunkingStrategy ChunkingStrategy { get; set; } = null!;
    [Inject] public ChunkingOptions ChunkingOptions { get; set; } = null!;

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        var parser = Parsers.FirstOrDefault(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"))
            ?? throw new NoParserFoundException(ctx.Metadata.ContentType ?? "text/plain");

        if (ChunkingStrategy is IDocumentChunkingStrategy docStrategy)
        {
            // Document-level strategy: collect all sections, then chunk as a whole
            await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
                ctx.Sections.Add(section);

            await foreach (var chunk in docStrategy.ChunkDocumentAsync(
                ctx.Sections.ToAsyncEnumerable(), ChunkingOptions, ct).ConfigureAwait(false))
                ctx.Chunks.Add(chunk);
        }
        else
        {
            // Per-section strategy: existing behaviour with heading breadcrumb metadata
            var headingBreadcrumbs = new string?[6];

            await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
            {
                Dictionary<string, string>? headingMetadata = null;

                if (section.HeadingLevel is { } level && level >= 1 && level <= 6 && section.Heading is not null)
                {
                    headingBreadcrumbs[level - 1] = section.Heading;
                    foreach (ref var slot in headingBreadcrumbs.AsSpan(level))
                        slot = null;

                    var parts = new List<string>(level);
                    foreach (var h in headingBreadcrumbs[..level])
                        if (h is not null) parts.Add(h);

                    headingMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["heading"] = section.Heading,
                        ["heading_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["heading_breadcrumb"] = string.Join(" > ", parts),
                    };
                }

                await foreach (var chunk in ChunkingStrategy.ChunkAsync(section, ChunkingOptions, ct).ConfigureAwait(false))
                {
                    if (headingMetadata is not null)
                        foreach (var kv in headingMetadata)
                            chunk.Metadata.TryAdd(kv.Key, kv.Value);

                    ctx.Chunks.Add(chunk);
                }

                ctx.Sections.Add(section);
            }
        }

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Parsing,
            DocumentId = ctx.Metadata.DocumentId,
            Message = "Parsing complete",
        });

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ParseBehaviorDocumentChunkingTests" --no-build
```

Expected: PASS (2 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs \
        tests/Rag.NET.Tests/Ingestion/ParseBehaviorDocumentChunkingTests.cs
git commit -m "feat: update ParseBehavior to support IDocumentChunkingStrategy"
```

---

### Task 4: DI Registration — `RagBuilder.UseHierarchicalMerging`

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseHierarchicalMergingTests.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/DependencyInjection/UseHierarchicalMergingTests.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseHierarchicalMergingTests
{
    [Fact]
    public void UseHierarchicalMerging_RegistersStrategyAsIChunkingStrategy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagNet(rag => rag.UseHierarchicalMerging());

        var sp = services.BuildServiceProvider();
        var strategy = sp.GetRequiredService<IChunkingStrategy>();

        Assert.IsType<HierarchicalMergerChunkingStrategy>(strategy);
    }

    [Fact]
    public void UseHierarchicalMerging_WithOptions_RegistersOptionsAsSingleton()
    {
        var opts = new HierarchicalMergerOptions { MaxDepth = 3 };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagNet(rag => rag.UseHierarchicalMerging(opts));

        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<HierarchicalMergerOptions>();

        Assert.Equal(3, resolved.MaxDepth);
    }

    [Fact]
    public void UseHierarchicalMerging_StrategyAlsoImplementsIDocumentChunkingStrategy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagNet(rag => rag.UseHierarchicalMerging());

        var sp = services.BuildServiceProvider();
        var strategy = sp.GetRequiredService<IChunkingStrategy>();

        Assert.IsAssignableFrom<IDocumentChunkingStrategy>(strategy);
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~UseHierarchicalMergingTests" --no-build
```

Expected: compile error or test failure — `UseHierarchicalMerging` method not found.

**Step 3: Add `UseHierarchicalMerging` to `RagBuilder`**

Add after `UseTokenAwareChunking` in `src/Rag.NET/DependencyInjection/RagBuilder.cs`:
```csharp
/// <summary>
/// Registers <see cref="HierarchicalMergerChunkingStrategy"/> which merges document sections
/// into heading-subtree chunks. Each chunk covers one heading and all body text under it
/// down to <paramref name="options"/>.<see cref="HierarchicalMergerOptions.MaxDepth"/>.
/// Uses <see cref="DocumentSection.HeadingLevel"/> when available; falls back to
/// <see cref="HierarchicalMergerOptions.HeadingPatterns"/> for formats without heading metadata.
/// </summary>
public RagBuilder UseHierarchicalMerging(HierarchicalMergerOptions? options = null)
{
    var opts = options ?? new HierarchicalMergerOptions();
    Services.AddSingleton(opts);
    Services.AddSingleton<IChunkingStrategy>(_ => new HierarchicalMergerChunkingStrategy(opts));
    return this;
}
```

Also add the using at the top of `RagBuilder.cs` (if not already present):
```csharp
using Rag.NET.Chunking;
```

**Step 4: Run all tests**

```
dotnet test tests/Rag.NET.Tests --no-build
```

Expected: all tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs \
        tests/Rag.NET.Tests/DependencyInjection/UseHierarchicalMergingTests.cs
git commit -m "feat: add RagBuilder.UseHierarchicalMerging DI registration"
```

---

## Final Verification

```
dotnet test --no-build
```

All tests must pass. Then use `superpowers:finishing-a-development-branch` to complete the branch.
