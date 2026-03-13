# Header-Aware Metadata Propagation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Propagate heading hierarchy from `DocumentSection` into `TextChunk.Metadata` during ingestion so callers can filter or display chunks by section context.

**Architecture:** The Markdown and HTML parsers already populate `DocumentSection.Heading` and `DocumentSection.HeadingLevel`. `RagPipeline.ParseAndChunkAsync` iterates sections and yields chunks — the right place to track a heading breadcrumb array and merge it into each chunk's `Metadata` after chunking. No new types needed; `TextChunk.Metadata` is already `IDictionary<string, string>`.

**Tech Stack:** C# 13, .NET 10, xunit.v3, NSubstitute.

---

### Task 1: Heading breadcrumb logic in RagPipeline

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs:71-85`
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

The goal: when a `DocumentSection` has `HeadingLevel` and `Heading` set, track a breadcrumb array (one slot per heading level 1–6), populate three metadata keys on each chunk produced from that section:
- `heading` — the section's own heading text (e.g. "Subsection 3")
- `heading_level` — heading level as string (e.g. "2")
- `heading_breadcrumb` — full path joined with " > " (e.g. "Chapter 1 > Section 2 > Subsection 3")

**Step 1: Write the failing tests**

Add to `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`:

```csharp
[Fact]
public async Task IngestAsync_SectionWithHeading_PropagatesMetadataToChunks()
{
    // Arrange
    var section = new DocumentSection
    {
        Text = "Some content",
        DocumentId = "doc-1",
        SectionIndex = 0,
        HeadingLevel = 2,
        Heading = "My Section"
    };
    _parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable([section]));
    _chunker.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable([new TextChunk { Text = "Some content", DocumentId = "doc-1", ChunkIndex = 0 }]));

    // Act
    var result = await _pipeline.IngestAsync(new MemoryStream(), new DocumentMetadata { DocumentId = "doc-1", FileName = "f.md", ContentType = "text/markdown" });

    // Assert
    var stored = _capturedChunks.Single();
    Assert.Equal("My Section", stored.Chunk.Metadata["heading"]);
    Assert.Equal("2", stored.Chunk.Metadata["heading_level"]);
    Assert.Equal("My Section", stored.Chunk.Metadata["heading_breadcrumb"]);
}

[Fact]
public async Task IngestAsync_NestedHeadings_BuildsBreadcrumb()
{
    // Arrange: H1 section then H2 section
    var h1 = new DocumentSection { Text = "Chapter content", DocumentId = "doc-1", SectionIndex = 0, HeadingLevel = 1, Heading = "Chapter 1" };
    var h2 = new DocumentSection { Text = "Section content", DocumentId = "doc-1", SectionIndex = 1, HeadingLevel = 2, Heading = "Overview" };
    _parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable([h1, h2]));
    _chunker.ChunkAsync(h1, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable([new TextChunk { Text = "Chapter content", DocumentId = "doc-1", ChunkIndex = 0 }]));
    _chunker.ChunkAsync(h2, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable([new TextChunk { Text = "Section content", DocumentId = "doc-1", ChunkIndex = 0 }]));

    // Act
    await _pipeline.IngestAsync(new MemoryStream(), new DocumentMetadata { DocumentId = "doc-1", FileName = "f.md", ContentType = "text/markdown" });

    // Assert
    var h2Chunk = _capturedChunks.Last();
    Assert.Equal("Overview", h2Chunk.Chunk.Metadata["heading"]);
    Assert.Equal("2", h2Chunk.Chunk.Metadata["heading_level"]);
    Assert.Equal("Chapter 1 > Overview", h2Chunk.Chunk.Metadata["heading_breadcrumb"]);
}

[Fact]
public async Task IngestAsync_SectionWithoutHeading_NoHeadingMetadata()
{
    // Arrange
    var section = new DocumentSection { Text = "Plain text", DocumentId = "doc-1", SectionIndex = 0 };
    _parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable([section]));
    _chunker.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable([new TextChunk { Text = "Plain text", DocumentId = "doc-1", ChunkIndex = 0 }]));

    // Act
    await _pipeline.IngestAsync(new MemoryStream(), new DocumentMetadata { DocumentId = "doc-1", FileName = "f.txt", ContentType = "text/plain" });

    // Assert
    var stored = _capturedChunks.Single();
    Assert.False(stored.Chunk.Metadata.ContainsKey("heading"));
    Assert.False(stored.Chunk.Metadata.ContainsKey("heading_breadcrumb"));
}
```

> **Note on test setup:** Look at how existing tests in `RagPipelineTests.cs` set up `_capturedChunks` — NSubstitute captures `StoreAsync` args. The existing helper `AsyncEnumerable<T>` is likely already defined there. If not, add:
> ```csharp
> private static async IAsyncEnumerable<T> AsyncEnumerable<T>(IEnumerable<T> items) {
>     foreach (var item in items) yield return item;
>     await Task.CompletedTask;
> }
> ```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~IngestAsync_SectionWith" -v minimal
```

Expected: FAIL — heading metadata keys not present.

**Step 3: Implement heading breadcrumb tracking in `ParseAndChunkAsync`**

Replace the existing `ParseAndChunkAsync` method in `src/Rag.NET/Pipeline/RagPipeline.cs`:

```csharp
private async Task<List<TextChunk>> ParseAndChunkAsync(
    IDocumentParser parser,
    Stream document,
    DocumentMetadata metadata,
    CancellationToken cancellationToken)
{
    var chunks = new List<TextChunk>();
    var headingBreadcrumbs = new string?[6]; // slots for H1–H6

    await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken).ConfigureAwait(false))
    {
        // Update breadcrumb state before yielding chunks
        string? breadcrumb = null;
        if (section.HeadingLevel is int level && level >= 1 && level <= 6 && section.Heading is not null)
        {
            headingBreadcrumbs[level - 1] = section.Heading;
            // Clear all deeper levels
            for (int i = level; i < 6; i++)
                headingBreadcrumbs[i] = null;

            // Build breadcrumb string from filled slots
            breadcrumb = string.Join(" > ", headingBreadcrumbs[..level].Where(h => h is not null));
        }

        await foreach (var chunk in chunkingStrategy.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
        {
            if (section.Heading is not null && section.HeadingLevel is int headingLevel)
            {
                chunk.Metadata.TryAdd("heading", section.Heading);
                chunk.Metadata.TryAdd("heading_level", headingLevel.ToString());
                if (breadcrumb is not null)
                    chunk.Metadata.TryAdd("heading_breadcrumb", breadcrumb);
            }
            chunks.Add(chunk);
        }
    }

    return chunks;
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~IngestAsync_SectionWith" -v minimal
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~IngestAsync_SectionWithoutHeading" -v minimal
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~IngestAsync_NestedHeadings" -v minimal
```

Expected: all PASS.

**Step 5: Run full test suite**

```bash
dotnet test tests/Rag.NET.Tests -v minimal
```

Expected: all pass, no regressions.

**Step 6: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: propagate heading breadcrumb into TextChunk.Metadata during ingestion"
```

---

### Task 2: Integration test with real Markdown parser

**Files:**
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs` (or new file `RagPipelineHeadingIntegrationTests.cs`)

Verify end-to-end: a real markdown document with nested headings produces chunks with correct breadcrumbs.

**Step 1: Write the integration test**

Add a new test class `tests/Rag.NET.Tests/Pipeline/RagPipelineHeadingIntegrationTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Rag.NET.Chunking;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Pipeline;

public class RagPipelineHeadingIntegrationTests
{
    [Fact]
    public async Task IngestAsync_MarkdownWithNestedHeadings_ProducesBreadcrumbs()
    {
        // Arrange
        var markdown = """
            # Chapter 1

            Intro content.

            ## Section 1.1

            Sub content.

            ### Deep 1.1.1

            Deep content.

            # Chapter 2

            New chapter content.
            """;

        var vectorStore = Substitute.For<IVectorStore>();
        List<EmbeddedChunk> captured = [];
        await vectorStore.StoreAsync(Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured.AddRange(c)));

        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                return Task.FromResult<IList<Embedding<float>>>(
                    texts.Select(_ => new Embedding<float>(new float[3])).ToList());
            });

        var pipeline = new RagPipeline(
            [new MarkdownDocumentParser()],
            new FixedSizeChunkingStrategy(),
            vectorStore,
            embedder,
            chatClient: null,
            new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 });

        var meta = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.md", ContentType = "text/markdown" };

        // Act
        await pipeline.IngestAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markdown)), meta);

        // Assert
        Assert.NotEmpty(captured);

        var chapterChunk = captured.First(c => c.Chunk.Metadata.TryGetValue("heading", out var h) && h == "Chapter 1");
        Assert.Equal("1", chapterChunk.Chunk.Metadata["heading_level"]);
        Assert.Equal("Chapter 1", chapterChunk.Chunk.Metadata["heading_breadcrumb"]);

        var sectionChunk = captured.First(c => c.Chunk.Metadata.TryGetValue("heading", out var h) && h == "Section 1.1");
        Assert.Equal("Chapter 1 > Section 1.1", sectionChunk.Chunk.Metadata["heading_breadcrumb"]);

        var deepChunk = captured.First(c => c.Chunk.Metadata.TryGetValue("heading", out var h) && h == "Deep 1.1.1");
        Assert.Equal("Chapter 1 > Section 1.1 > Deep 1.1.1", deepChunk.Chunk.Metadata["heading_breadcrumb"]);

        // Chapter 2 should reset H2/H3 breadcrumbs
        var chapter2Chunk = captured.First(c => c.Chunk.Metadata.TryGetValue("heading", out var h) && h == "Chapter 2");
        Assert.Equal("Chapter 2", chapter2Chunk.Chunk.Metadata["heading_breadcrumb"]);
    }
}
```

**Step 2: Run the integration test**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RagPipelineHeadingIntegrationTests" -v minimal
```

Expected: PASS (implementation already done in Task 1).

**Step 3: Commit**

```bash
git add tests/Rag.NET.Tests/Pipeline/RagPipelineHeadingIntegrationTests.cs
git commit -m "test: add integration test for heading breadcrumb propagation"
```
