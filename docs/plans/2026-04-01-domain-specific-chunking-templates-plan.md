# Domain-Specific Chunking Templates Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a single `Rag.NET.Chunking.Templates` package with six pre-built domain chunking templates (Legal, Book, Academic Paper, Q&A Pairs, Email, Resume) each registered via a single `UseXxx()` call.

**Architecture:** Templates compose `HierarchicalMergerChunkingStrategy` where possible (Legal, Book, Academic Paper) and implement from scratch where genuinely new logic is needed (Q&A Pairs, Email, Resume). Email and Q&A also register `IDocumentParser` implementations. Every chunk carries a `"template"` metadata key for retrieval-time filtering.

**Tech Stack:** xUnit v3, NSubstitute, MimeKit (Email), CsvHelper (Q&A CSV), ClosedXML (Q&A Excel), `Rag.NET.Chunking.HierarchicalMergerChunkingStrategy`.

---

### Task 1: Project scaffold

**Files:**
- Create: `src/Rag.NET.Chunking.Templates/Rag.NET.Chunking.Templates.csproj`
- Create: `tests/Rag.NET.Chunking.Templates.Tests/Rag.NET.Chunking.Templates.Tests.csproj`
- Modify: `Rag.NET.slnx` (add both projects)

**Step 1: Create source csproj**

```xml
<!-- src/Rag.NET.Chunking.Templates/Rag.NET.Chunking.Templates.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Chunking.Templates</RootNamespace>
    <PackageId>Rag.NET.Chunking.Templates</PackageId>
    <Description>Domain-specific chunking templates for Rag.NET (Legal, Book, Academic Paper, Q&amp;A Pairs, Email, Resume)</Description>
  </PropertyGroup>

  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.Chunking.Templates.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />
    <ProjectReference Include="..\Rag.NET.Chunking\Rag.NET.Chunking.csproj" />
    <PackageReference Include="MimeKit" Version="4.*" />
    <PackageReference Include="CsvHelper" Version="33.*" />
    <PackageReference Include="ClosedXML" Version="0.*" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create test csproj**

```xml
<!-- tests/Rag.NET.Chunking.Templates.Tests/Rag.NET.Chunking.Templates.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Chunking.Templates\Rag.NET.Chunking.Templates.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>

</Project>
```

**Step 3: Add to solution**

```bash
dotnet sln add src/Rag.NET.Chunking.Templates/Rag.NET.Chunking.Templates.csproj
dotnet sln add tests/Rag.NET.Chunking.Templates.Tests/Rag.NET.Chunking.Templates.Tests.csproj
```

**Step 4: Verify build**

```bash
dotnet build src/Rag.NET.Chunking.Templates/Rag.NET.Chunking.Templates.csproj -v minimal
dotnet build tests/Rag.NET.Chunking.Templates.Tests/Rag.NET.Chunking.Templates.Tests.csproj -v minimal
```

Expected: Build succeeded, 0 errors.

**Step 5: Commit**

```bash
git add src/Rag.NET.Chunking.Templates/ tests/Rag.NET.Chunking.Templates.Tests/ Rag.NET.slnx
git commit -m "chore: scaffold Rag.NET.Chunking.Templates project and test project"
```

---

### Task 2: Legal chunking

**Files:**
- Create: `src/Rag.NET.Chunking.Templates/LegalChunkingOptions.cs`
- Create: `src/Rag.NET.Chunking.Templates/LegalChunkingStrategy.cs`
- Create: `tests/Rag.NET.Chunking.Templates.Tests/LegalChunkingStrategyTests.cs`

**Step 1: Write the failing test**

Create `tests/Rag.NET.Chunking.Templates.Tests/LegalChunkingStrategyTests.cs`:

```csharp
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class LegalChunkingStrategyTests
{
    private static DocumentSection Section(string text, string? heading = null, int? headingLevel = null, int index = 0) =>
        new() { Text = text, DocumentId = new DocumentId("doc"), Heading = heading, HeadingLevel = headingLevel, SectionIndex = index };

    private static async Task<List<TextChunk>> ChunkAsync(LegalChunkingStrategy strategy, IEnumerable<DocumentSection> sections)
    {
        var chunks = new List<TextChunk>();
        var opts = new ChunkingOptions();
        await foreach (var chunk in strategy.ChunkDocumentAsync(sections.ToAsyncEnumerable(), opts))
            chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsTemplateMetadata()
    {
        var sut = new LegalChunkingStrategy(new LegalChunkingOptions());
        var sections = new[]
        {
            Section("General provisions apply to all parties.", "1. General Provisions", headingLevel: 1),
            Section("For the purposes of this agreement.", "1.1 Definitions", headingLevel: 2),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.All(chunks, c => Assert.Equal("legal", c.Metadata["template"]));
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsClauseMetadata()
    {
        var sut = new LegalChunkingStrategy(new LegalChunkingOptions());
        var sections = new[]
        {
            Section("Article text here.", "1. General Provisions", headingLevel: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.Contains(chunks, c => c.Metadata.ContainsKey("clause"));
    }

    [Fact]
    public async Task ChunkDocumentAsync_ProducesChunksForEachClause()
    {
        var sut = new LegalChunkingStrategy(new LegalChunkingOptions());
        var sections = new[]
        {
            Section("General provisions text.", "1. General Provisions", headingLevel: 1),
            Section("Obligations text.", "2. Obligations", headingLevel: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.Equal(2, chunks.Count);
    }
}
```

> **Note:** `ToAsyncEnumerable()` is an extension from `System.Linq.Async` — check if the test project already has it. If not, add `<PackageReference Include="System.Linq.Async" Version="6.*" />` to the test csproj. Alternatively use a manual helper:
> ```csharp
> private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source)
> { foreach (var item in source) yield return item; }
> ```

**Step 2: Run to verify it fails**

```bash
dotnet test tests/Rag.NET.Chunking.Templates.Tests/ --filter "LegalChunkingStrategyTests" -v minimal
```

Expected: compile error — `LegalChunkingStrategy` not found.

**Step 3: Implement options**

Create `src/Rag.NET.Chunking.Templates/LegalChunkingOptions.cs`:

```csharp
namespace Rag.NET.Chunking.Templates;

public sealed class LegalChunkingOptions
{
    public int MaxDepth { get; set; } = 3;

    /// <summary>
    /// Regex patterns for detecting numbered clause headings, ordered by hierarchy level.
    /// Default: "1.", "1.1", "1.1.1" patterns.
    /// </summary>
    public string[] HeadingPatterns { get; set; } =
    [
        @"^\d+\.\s",
        @"^\d+\.\d+\s",
        @"^\d+\.\d+\.\d+\s",
    ];
}
```

**Step 4: Implement strategy**

Create `src/Rag.NET.Chunking.Templates/LegalChunkingStrategy.cs`:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

public sealed class LegalChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
{
    private readonly HierarchicalMergerChunkingStrategy _inner;

    public LegalChunkingStrategy(LegalChunkingOptions options)
    {
        _inner = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions
        {
            MaxDepth = options.MaxDepth,
            HeadingPatterns = options.HeadingPatterns,
        });
    }

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _inner.ChunkDocumentAsync(sections, chunkingOptions, cancellationToken))
        {
            chunk.Metadata["template"] = "legal";
            chunk.Metadata["clause"] = chunk.Metadata.TryGetValue("heading", out var h) ? h : string.Empty;
            yield return chunk;
        }
    }

    public IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        CancellationToken cancellationToken = default)
        => _inner.ChunkAsync(section, chunkingOptions, cancellationToken);
}
```

> **Note on metadata mutation:** `TextChunk.Metadata` is an `IDictionary<string, string>` — `init`-only means the property reference is immutable, but the dictionary contents are mutable. Direct assignment (`chunk.Metadata["template"] = "legal"`) is valid.

> **Note on HierarchicalMergerOptions:** Check `src/Rag.NET.Chunking/` for the exact type of `HeadingPatterns` (may be `IReadOnlyList<string>` — cast if needed).

**Step 5: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Chunking.Templates.Tests/ --filter "LegalChunkingStrategyTests" -v minimal
```

Expected: PASS (3 tests).

**Step 6: Commit**

```bash
git add src/Rag.NET.Chunking.Templates/LegalChunkingOptions.cs src/Rag.NET.Chunking.Templates/LegalChunkingStrategy.cs tests/Rag.NET.Chunking.Templates.Tests/LegalChunkingStrategyTests.cs
git commit -m "feat(templates): add LegalChunkingStrategy"
```

---

### Task 3: Book chunking

**Files:**
- Create: `src/Rag.NET.Chunking.Templates/BookChunkingOptions.cs`
- Create: `src/Rag.NET.Chunking.Templates/BookChunkingStrategy.cs`
- Create: `tests/Rag.NET.Chunking.Templates.Tests/BookChunkingStrategyTests.cs`

**Step 1: Write the failing test**

```csharp
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class BookChunkingStrategyTests
{
    private static DocumentSection Section(string text, string? heading = null, int? headingLevel = null, int index = 0) =>
        new() { Text = text, DocumentId = new DocumentId("doc"), Heading = heading, HeadingLevel = headingLevel, SectionIndex = index };

    private static async Task<List<TextChunk>> ChunkAsync(BookChunkingStrategy strategy, IEnumerable<DocumentSection> sections)
    {
        var chunks = new List<TextChunk>();
        await foreach (var chunk in strategy.ChunkDocumentAsync(sections.ToAsync(), new ChunkingOptions()))
            chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public async Task ChunkDocumentAsync_FiltersTocSection()
    {
        var sut = new BookChunkingStrategy(new BookChunkingOptions());
        var sections = new[]
        {
            Section("Chapter 1 ......... 1\nChapter 2 ......... 5", "Table of Contents", headingLevel: 1, index: 0),
            Section("The first chapter content.", "Chapter 1", headingLevel: 1, index: 1),
            Section("The second chapter content.", "Chapter 2", headingLevel: 1, index: 2),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.DoesNotContain(chunks, c =>
            c.Metadata.TryGetValue("heading", out var h) &&
            h.Contains("Contents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChunkDocumentAsync_PreservesChapterContent()
    {
        var sut = new BookChunkingStrategy(new BookChunkingOptions());
        var sections = new[]
        {
            Section("Chapter 1 ......... 1", "Table of Contents", headingLevel: 1, index: 0),
            Section("The first chapter content.", "Chapter 1", headingLevel: 1, index: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.Contains(chunks, c => c.Text.Contains("first chapter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsTemplateMetadata()
    {
        var sut = new BookChunkingStrategy(new BookChunkingOptions());
        var sections = new[] { Section("Chapter content.", "Chapter 1", headingLevel: 1) };

        var chunks = await ChunkAsync(sut, sections);

        Assert.All(chunks, c => Assert.Equal("book", c.Metadata["template"]));
    }

    [Fact]
    public async Task ChunkDocumentAsync_FiltersIndexWhenDisabled()
    {
        var sut = new BookChunkingStrategy(new BookChunkingOptions { IncludeIndex = false });
        var sections = new[]
        {
            Section("Chapter content.", "Chapter 1", headingLevel: 1, index: 0),
            Section("A\n  1\nB\n  3", "Index", headingLevel: 1, index: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.DoesNotContain(chunks, c =>
            c.Metadata.TryGetValue("heading", out var h) &&
            h.Equals("Index", StringComparison.OrdinalIgnoreCase));
    }
}
```

**Step 2: Implement options**

```csharp
// src/Rag.NET.Chunking.Templates/BookChunkingOptions.cs
namespace Rag.NET.Chunking.Templates;

public sealed class BookChunkingOptions
{
    public int MaxDepth { get; set; } = 2;
    public bool IncludeIndex { get; set; } = false;
    public bool IncludeForeword { get; set; } = true;
}
```

**Step 3: Implement strategy**

```csharp
// src/Rag.NET.Chunking.Templates/BookChunkingStrategy.cs
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Rag.NET.Chunking.Templates;

public sealed class BookChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
{
    private static readonly Regex PageNumberLine =
        new(@"\s+\d+\s*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly string[] TocHeadings =
        ["table of contents", "contents"];

    private static readonly string[] IndexHeadings =
        ["index"];

    private static readonly string[] ForewordHeadings =
        ["foreword", "preface", "introduction"];

    private readonly HierarchicalMergerChunkingStrategy _inner;
    private readonly BookChunkingOptions _options;

    public BookChunkingStrategy(BookChunkingOptions options)
    {
        _options = options;
        _inner = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions
        {
            MaxDepth = options.MaxDepth,
        });
    }

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filtered = Filter(sections, cancellationToken);
        await foreach (var chunk in _inner.ChunkDocumentAsync(filtered, chunkingOptions, cancellationToken))
        {
            chunk.Metadata["template"] = "book";
            if (chunk.Metadata.TryGetValue("heading", out var h))
                chunk.Metadata["chapter"] = h;
            yield return chunk;
        }
    }

    public IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        CancellationToken cancellationToken = default)
        => _inner.ChunkAsync(section, chunkingOptions, cancellationToken);

    private async IAsyncEnumerable<DocumentSection> Filter(
        IAsyncEnumerable<DocumentSection> sections,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var section in sections.WithCancellation(cancellationToken))
        {
            if (IsToc(section)) continue;
            if (!_options.IncludeIndex && IsIndex(section)) continue;
            if (!_options.IncludeForeword && IsForeword(section)) continue;
            yield return section;
        }
    }

    private static bool IsToc(DocumentSection section)
    {
        if (section.Heading is { } h &&
            TocHeadings.Any(t => h.Trim().Equals(t, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Heuristic: >50% of non-empty lines end with a page number
        var lines = section.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;
        var pageLines = lines.Count(l => PageNumberLine.IsMatch(l));
        return (double)pageLines / lines.Length > 0.5;
    }

    private static bool IsIndex(DocumentSection section) =>
        section.Heading is { } h &&
        IndexHeadings.Any(i => h.Trim().Equals(i, StringComparison.OrdinalIgnoreCase));

    private static bool IsForeword(DocumentSection section) =>
        section.Heading is { } h &&
        ForewordHeadings.Any(f => h.Trim().Equals(f, StringComparison.OrdinalIgnoreCase));
}
```

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Chunking.Templates.Tests/ --filter "BookChunkingStrategyTests" -v minimal
```

Expected: PASS (4 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET.Chunking.Templates/BookChunkingOptions.cs src/Rag.NET.Chunking.Templates/BookChunkingStrategy.cs tests/Rag.NET.Chunking.Templates.Tests/BookChunkingStrategyTests.cs
git commit -m "feat(templates): add BookChunkingStrategy with TOC filtering"
```

---

### Task 4: Academic Paper chunking

**Files:**
- Create: `src/Rag.NET.Chunking.Templates/AcademicPaperChunkingOptions.cs`
- Create: `src/Rag.NET.Chunking.Templates/AcademicPaperChunkingStrategy.cs`
- Create: `tests/Rag.NET.Chunking.Templates.Tests/AcademicPaperChunkingStrategyTests.cs`

**Step 1: Write the failing test**

```csharp
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class AcademicPaperChunkingStrategyTests
{
    private static DocumentSection Section(string text, string? heading = null, int? headingLevel = null, int index = 0) =>
        new() { Text = text, DocumentId = new DocumentId("doc"), Heading = heading, HeadingLevel = headingLevel, SectionIndex = index };

    private static async Task<List<TextChunk>> ChunkAsync(AcademicPaperChunkingStrategy strategy, IEnumerable<DocumentSection> sections)
    {
        var chunks = new List<TextChunk>();
        await foreach (var chunk in strategy.ChunkDocumentAsync(sections.ToAsync(), new ChunkingOptions()))
            chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public async Task ChunkDocumentAsync_FiltersFrontMatter()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions());
        var sections = new[]
        {
            Section("John Smith, Jane Doe", index: 0),                         // authors (front matter)
            Section("University of Science", index: 1),                        // affiliation (front matter)
            Section("This paper examines temperature effects.", "Abstract", headingLevel: 1, index: 2),
            Section("Previous studies have shown...", "Introduction", headingLevel: 1, index: 3),
        };

        var chunks = await ChunkAsync(sut, sections);

        // front matter sections have no heading — verify no un-headed front matter appears
        Assert.DoesNotContain(chunks, c => c.Text.Contains("University of Science", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkDocumentAsync_EmitsAbstractWithSectionTypeMetadata()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions());
        var sections = new[]
        {
            Section("This paper examines temperature effects.", "Abstract", headingLevel: 1, index: 0),
            Section("Previous studies...", "Introduction", headingLevel: 1, index: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        var abstractChunk = Assert.Single(chunks, c =>
            c.Metadata.TryGetValue("section_type", out var t) && t == "abstract");
        Assert.Contains("temperature effects", abstractChunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkDocumentAsync_FiltersReferencesWhenDisabled()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions { IncludeReferences = false });
        var sections = new[]
        {
            Section("This paper examines...", "Abstract", headingLevel: 1, index: 0),
            Section("Background.", "Introduction", headingLevel: 1, index: 1),
            Section("[1] Author et al.", "References", headingLevel: 1, index: 2),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.DoesNotContain(chunks, c => c.Text.Contains("Author et al.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsTemplateMetadata()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions());
        var sections = new[]
        {
            Section("This paper examines...", "Abstract", headingLevel: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.All(chunks, c => Assert.Equal("academic_paper", c.Metadata["template"]));
    }
}
```

**Step 2: Implement options**

```csharp
// src/Rag.NET.Chunking.Templates/AcademicPaperChunkingOptions.cs
namespace Rag.NET.Chunking.Templates;

public sealed class AcademicPaperChunkingOptions
{
    public bool IncludeReferences { get; set; } = false;
    public bool IncludeAbstract { get; set; } = true;
}
```

**Step 3: Implement strategy**

```csharp
// src/Rag.NET.Chunking.Templates/AcademicPaperChunkingStrategy.cs
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

public sealed class AcademicPaperChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
{
    private static readonly string[] AbstractHeadings = ["abstract"];
    private static readonly string[] ReferencesHeadings = ["references", "bibliography", "works cited"];

    private readonly HierarchicalMergerChunkingStrategy _inner;
    private readonly AcademicPaperChunkingOptions _options;

    public AcademicPaperChunkingStrategy(AcademicPaperChunkingOptions options)
    {
        _options = options;
        _inner = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());
    }

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Collect sections, find abstract position, split into: pre-abstract (skip),
        // abstract (emit standalone), body (pass to hierarchical merger).
        var allSections = await sections.ToListAsync(cancellationToken);

        var abstractIndex = allSections.FindIndex(s =>
            s.Heading is { } h &&
            AbstractHeadings.Any(a => h.Trim().Equals(a, StringComparison.OrdinalIgnoreCase)));

        // Determine start: skip everything before abstract (front matter)
        var startIndex = abstractIndex >= 0 ? abstractIndex : 0;

        // Emit abstract as a standalone chunk
        if (abstractIndex >= 0 && _options.IncludeAbstract)
        {
            var abstractSection = allSections[abstractIndex];
            yield return new TextChunk
            {
                Text = abstractSection.Text,
                DocumentId = abstractSection.DocumentId,
                ChunkIndex = 0,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["template"] = "academic_paper",
                    ["section_type"] = "abstract",
                },
            };
            startIndex = abstractIndex + 1;
        }

        // Body sections (skip abstract, filter references)
        var bodySections = allSections
            .Skip(startIndex)
            .Where(s => _options.IncludeReferences ||
                        s.Heading is null ||
                        !ReferencesHeadings.Any(r =>
                            s.Heading.Trim().Equals(r, StringComparison.OrdinalIgnoreCase)));

        var chunkIndex = _options.IncludeAbstract && abstractIndex >= 0 ? 1 : 0;
        await foreach (var chunk in _inner.ChunkDocumentAsync(bodySections.ToAsyncEnumerable(), chunkingOptions, cancellationToken))
        {
            chunk.Metadata["template"] = "academic_paper";
            chunk.Metadata["section_type"] = "body";
            yield return chunk with { ChunkIndex = chunkIndex++ };
        }
    }

    public IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        CancellationToken cancellationToken = default)
        => _inner.ChunkAsync(section, chunkingOptions, cancellationToken);
}
```

> **Note:** `ToListAsync` and `ToAsyncEnumerable` are LINQ extensions. If not available, buffer manually: `var list = new List<DocumentSection>(); await foreach(var s in sections) list.Add(s);`

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Chunking.Templates.Tests/ --filter "AcademicPaperChunkingStrategyTests" -v minimal
```

Expected: PASS (4 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET.Chunking.Templates/AcademicPaperChunkingOptions.cs src/Rag.NET.Chunking.Templates/AcademicPaperChunkingStrategy.cs tests/Rag.NET.Chunking.Templates.Tests/AcademicPaperChunkingStrategyTests.cs
git commit -m "feat(templates): add AcademicPaperChunkingStrategy"
```

---

### Task 5: Q&A Pairs

**Files:**
- Create: `src/Rag.NET.Chunking.Templates/QAPairsChunkingOptions.cs`
- Create: `src/Rag.NET.Chunking.Templates/QAPairsDocumentParser.cs`
- Create: `src/Rag.NET.Chunking.Templates/QAPairsChunkingStrategy.cs`
- Create: `tests/Rag.NET.Chunking.Templates.Tests/QAPairsTests.cs`

**Design note:** `DocumentSection` has no metadata dictionary. The Q&A parser passes the answer to the strategy via `DocumentSection.Heading` (question = `Text`, answer = `Heading`). This is an intentional internal contract — the `Heading` field is not rendered in this template.

**Step 1: Write the failing test**

```csharp
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Text;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class QAPairsTests
{
    private static readonly DocumentMetadata Metadata =
        new() { DocumentId = new DocumentId("doc.csv"), FileName = "doc.csv" };

    [Fact]
    public async Task QAPairsDocumentParser_ParsesCsvRows()
    {
        var csv = "question,answer\nWhat is the capital of France?,Paris\nWhat is 2+2?,4";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parser = new QAPairsDocumentParser(new QAPairsChunkingOptions());

        var sections = new List<DocumentSection>();
        await foreach (var section in parser.ParseAsync(stream, Metadata))
            sections.Add(section);

        Assert.Equal(2, sections.Count);
        Assert.Equal("What is the capital of France?", sections[0].Text);
    }

    [Fact]
    public async Task QAPairsDocumentParser_CanParseCsv()
    {
        var parser = new QAPairsDocumentParser(new QAPairsChunkingOptions());
        Assert.True(parser.CanParse("text/csv"));
    }

    [Fact]
    public async Task QAPairsChunkingStrategy_StoresAnswerInMetadata()
    {
        var strategy = new QAPairsChunkingStrategy();
        var sections = new[]
        {
            new DocumentSection { Text = "What is Paris?", Heading = "Capital of France", DocumentId = new DocumentId("doc"), SectionIndex = 0 },
        };

        var chunks = new List<TextChunk>();
        await foreach (var chunk in strategy.ChunkDocumentAsync(sections.ToAsync(), new ChunkingOptions()))
            chunks.Add(chunk);

        var chunk = Assert.Single(chunks);
        Assert.Equal("What is Paris?", chunk.Text);
        Assert.Equal("Capital of France", chunk.Metadata["answer"]);
        Assert.Equal("qa_pairs", chunk.Metadata["template"]);
    }

    [Fact]
    public async Task QAPairsDocumentParser_SkipsRowsMissingQuestion()
    {
        var csv = "question,answer\n,Paris\nWhat is 2+2?,4";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parser = new QAPairsDocumentParser(new QAPairsChunkingOptions());

        var sections = new List<DocumentSection>();
        await foreach (var section in parser.ParseAsync(stream, Metadata))
            sections.Add(section);

        // Row with empty question is skipped
        Assert.Single(sections);
    }
}
```

**Step 2: Implement options**

```csharp
// src/Rag.NET.Chunking.Templates/QAPairsChunkingOptions.cs
namespace Rag.NET.Chunking.Templates;

public sealed class QAPairsChunkingOptions
{
    /// <summary>Column name for question text. Null = auto-detect from common names.</summary>
    public string? QuestionColumn { get; set; }

    /// <summary>Column name for answer text. Null = auto-detect from common names.</summary>
    public string? AnswerColumn { get; set; }

    internal static readonly string[] DefaultQuestionColumns = ["question", "q", "prompt", "input"];
    internal static readonly string[] DefaultAnswerColumns = ["answer", "a", "response", "output"];
}
```

**Step 3: Implement parser**

```csharp
// src/Rag.NET.Chunking.Templates/QAPairsDocumentParser.cs
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

public sealed class QAPairsDocumentParser(
    QAPairsChunkingOptions options,
    ILogger<QAPairsDocumentParser>? logger = null) : IDocumentParser
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public bool CanParse(string contentType) =>
        contentType is "text/csv"
            or "application/vnd.ms-excel"
            or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            or "application/octet-stream"; // fallback for unknown content types

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Determine format from file extension
        var ext = Path.GetExtension(metadata.FileName ?? string.Empty).ToLowerInvariant();
        if (ext is ".xlsx" or ".xls")
        {
            await foreach (var section in ParseExcelAsync(stream, metadata, cancellationToken))
                yield return section;
            yield break;
        }

        // Default: CSV
        await foreach (var section in ParseCsvAsync(stream, metadata, cancellationToken))
            yield return section;
    }

    private async IAsyncEnumerable<DocumentSection> ParseCsvAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync().ConfigureAwait(false);
        csv.ReadHeader();

        var headers = csv.HeaderRecord ?? [];
        var questionCol = options.QuestionColumn
            ?? headers.FirstOrDefault(h => QAPairsChunkingOptions.DefaultQuestionColumns
                .Contains(h, StringComparer.OrdinalIgnoreCase));
        var answerCol = options.AnswerColumn
            ?? headers.FirstOrDefault(h => QAPairsChunkingOptions.DefaultAnswerColumns
                .Contains(h, StringComparer.OrdinalIgnoreCase));

        if (questionCol is null || answerCol is null)
            throw new InvalidOperationException(
                $"Cannot resolve question/answer columns. Headers: [{string.Join(", ", headers)}]. " +
                $"Set QAPairsChunkingOptions.QuestionColumn and AnswerColumn explicitly.");

        var index = 0;
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var question = csv.GetField(questionCol) ?? string.Empty;
            var answer = csv.GetField(answerCol) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(question))
            {
                _logger.LogWarning("Skipping row {Row} — empty question column.", index + 1);
                continue;
            }

            // Answer is stored in Heading — intentional internal contract with QAPairsChunkingStrategy
            yield return new DocumentSection
            {
                Text = question,
                Heading = answer,
                DocumentId = metadata.DocumentId,
                SectionIndex = index++,
            };
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseExcelAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var rows = sheet.RowsUsed().ToList();
        if (rows.Count < 2) yield break;

        var headerRow = rows[0];
        var headers = headerRow.Cells().Select(c => c.GetString()).ToArray();

        var questionColIdx = FindColumnIndex(headers, options.QuestionColumn, QAPairsChunkingOptions.DefaultQuestionColumns);
        var answerColIdx = FindColumnIndex(headers, options.AnswerColumn, QAPairsChunkingOptions.DefaultAnswerColumns);

        if (questionColIdx < 0 || answerColIdx < 0)
            throw new InvalidOperationException(
                $"Cannot resolve question/answer columns. Headers: [{string.Join(", ", headers)}].");

        var index = 0;
        foreach (var row in rows.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var question = row.Cell(questionColIdx + 1).GetString();
            var answer = row.Cell(answerColIdx + 1).GetString();

            if (string.IsNullOrWhiteSpace(question))
            {
                _logger.LogWarning("Skipping row {Row} — empty question column.", index + 1);
                continue;
            }

            yield return new DocumentSection
            {
                Text = question,
                Heading = answer,
                DocumentId = metadata.DocumentId,
                SectionIndex = index++,
            };
        }
    }

    private static int FindColumnIndex(string[] headers, string? preferred, string[] defaults)
    {
        if (preferred is not null)
            return Array.FindIndex(headers, h => h.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        foreach (var d in defaults)
        {
            var i = Array.FindIndex(headers, h => h.Equals(d, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }
}
```

**Step 4: Implement chunking strategy**

```csharp
// src/Rag.NET.Chunking.Templates/QAPairsChunkingStrategy.cs
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

/// <summary>
/// Pass-through strategy: emits one chunk per Q&amp;A section.
/// Reads the answer from <see cref="DocumentSection.Heading"/> — this is an internal contract
/// established by <see cref="QAPairsDocumentParser"/>.
/// </summary>
public sealed class QAPairsChunkingStrategy : IDocumentChunkingStrategy
{
    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        await foreach (var section in sections.WithCancellation(cancellationToken))
        {
            yield return new TextChunk
            {
                Text = section.Text,
                DocumentId = section.DocumentId,
                ChunkIndex = index++,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["template"] = "qa_pairs",
                    ["answer"] = section.Heading ?? string.Empty,
                },
            };
        }
    }
}
```

**Step 5: Run tests**

```bash
dotnet test tests/Rag.NET.Chunking.Templates.Tests/ --filter "QAPairsTests" -v minimal
```

Expected: PASS (4 tests).

**Step 6: Commit**

```bash
git add src/Rag.NET.Chunking.Templates/QAPairsChunkingOptions.cs src/Rag.NET.Chunking.Templates/QAPairsDocumentParser.cs src/Rag.NET.Chunking.Templates/QAPairsChunkingStrategy.cs tests/Rag.NET.Chunking.Templates.Tests/QAPairsTests.cs
git commit -m "feat(templates): add QAPairsDocumentParser and QAPairsChunkingStrategy"
```

---

### Task 6: Email parser

**Files:**
- Create: `src/Rag.NET.Chunking.Templates/EmailChunkingOptions.cs`
- Create: `src/Rag.NET.Chunking.Templates/EmailDocumentParser.cs`
- Create: `tests/Rag.NET.Chunking.Templates.Tests/EmailDocumentParserTests.cs`

**Step 1: Write the failing test**

```csharp
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using System.Text;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class EmailDocumentParserTests
{
    private const string SimpleEml = """
        From: sender@example.com
        To: recipient@example.com
        Subject: Hello World
        Date: Tue, 01 Apr 2026 12:00:00 +0000
        MIME-Version: 1.0
        Content-Type: text/plain; charset=utf-8

        This is the body of the email. It contains important information.
        """;

    private static readonly DocumentMetadata Metadata =
        new() { DocumentId = new DocumentId("email.eml"), FileName = "email.eml" };

    [Fact]
    public async Task ParseAsync_EmitsBodySection()
    {
        var parser = new EmailDocumentParser(new EmailChunkingOptions { IncludeHeaders = false });
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(SimpleEml));

        var sections = new List<DocumentSection>();
        await foreach (var s in parser.ParseAsync(stream, Metadata))
            sections.Add(s);

        Assert.Contains(sections, s => s.Text.Contains("body of the email", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_EmitsHeadersSectionWhenEnabled()
    {
        var parser = new EmailDocumentParser(new EmailChunkingOptions { IncludeHeaders = true });
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(SimpleEml));

        var sections = new List<DocumentSection>();
        await foreach (var s in parser.ParseAsync(stream, Metadata))
            sections.Add(s);

        Assert.Contains(sections, s => s.Text.Contains("sender@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_CanParseMessageRfc822()
    {
        var parser = new EmailDocumentParser(new EmailChunkingOptions());
        Assert.True(parser.CanParse("message/rfc822"));
    }

    [Fact]
    public async Task ParseAsync_HeadersSectionMarkedWithPartMetadata()
    {
        var parser = new EmailDocumentParser(new EmailChunkingOptions { IncludeHeaders = true });
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(SimpleEml));

        var sections = new List<DocumentSection>();
        await foreach (var s in parser.ParseAsync(stream, Metadata))
            sections.Add(s);

        // Headers section has Heading = "headers" (used as metadata signal by downstream chunker)
        Assert.Contains(sections, s =>
            s.Heading != null &&
            s.Heading.Equals("headers", StringComparison.Ordinal));
    }
}
```

**Step 2: Implement options**

```csharp
// src/Rag.NET.Chunking.Templates/EmailChunkingOptions.cs
namespace Rag.NET.Chunking.Templates;

public sealed class EmailChunkingOptions
{
    public bool IncludeHeaders { get; set; } = true;
    public bool IncludeAttachments { get; set; } = true;
}
```

**Step 3: Implement parser**

```csharp
// src/Rag.NET.Chunking.Templates/EmailDocumentParser.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using System.Runtime.CompilerServices;
using System.Text;

namespace Rag.NET.Chunking.Templates;

public sealed class EmailDocumentParser(
    EmailChunkingOptions options,
    ILogger<EmailDocumentParser>? logger = null) : IDocumentParser
{
    private static readonly string[] TextExtensions = [".txt", ".md", ".csv", ".tsv"];
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public bool CanParse(string contentType) =>
        contentType is "message/rfc822" or "application/octet-stream";

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse .eml file '{metadata.FileName}'.", ex);
        }

        var index = 0;

        // Headers section
        if (options.IncludeHeaders)
        {
            var headerText = new StringBuilder();
            headerText.AppendLine($"From: {message.From}");
            headerText.AppendLine($"To: {message.To}");
            headerText.AppendLine($"Subject: {message.Subject}");
            headerText.AppendLine($"Date: {message.Date:R}");

            yield return new DocumentSection
            {
                Text = headerText.ToString().Trim(),
                Heading = "headers",
                DocumentId = metadata.DocumentId,
                SectionIndex = index++,
            };
        }

        // Body
        var bodyText = GetBodyText(message);
        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            yield return new DocumentSection
            {
                Text = bodyText,
                Heading = "body",
                DocumentId = metadata.DocumentId,
                SectionIndex = index++,
            };
        }

        // Attachments
        if (options.IncludeAttachments)
        {
            foreach (var attachment in message.Attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (attachment is not MimePart part) continue;

                var ext = Path.GetExtension(part.FileName ?? string.Empty).ToLowerInvariant();
                if (!TextExtensions.Contains(ext))
                {
                    _logger.LogDebug(
                        "Skipping binary attachment '{FileName}' — binary attachments are not inlined.",
                        part.FileName);
                    continue;
                }

                using var ms = new MemoryStream();
                await part.Content.DecodeToAsync(ms, cancellationToken).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(ms.ToArray());

                if (string.IsNullOrWhiteSpace(text)) continue;

                yield return new DocumentSection
                {
                    Text = text,
                    Heading = $"attachment:{part.FileName}",
                    DocumentId = metadata.DocumentId,
                    SectionIndex = index++,
                };
            }
        }
    }

    private static string GetBodyText(MimeMessage message)
    {
        // Prefer plain text; fall back to HTML stripped to plain text
        if (message.TextBody is { } plain)
            return plain;

        if (message.HtmlBody is { } html)
        {
            // Minimal HTML strip — remove tags, decode common entities
            var stripped = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            stripped = stripped
                .Replace("&amp;", "&", StringComparison.Ordinal)
                .Replace("&lt;", "<", StringComparison.Ordinal)
                .Replace("&gt;", ">", StringComparison.Ordinal)
                .Replace("&nbsp;", " ", StringComparison.Ordinal);
            return System.Text.RegularExpressions.Regex.Replace(stripped, @"\s{2,}", " ").Trim();
        }

        return string.Empty;
    }
}
```

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Chunking.Templates.Tests/ --filter "EmailDocumentParserTests" -v minimal
```

Expected: PASS (4 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET.Chunking.Templates/EmailChunkingOptions.cs src/Rag.NET.Chunking.Templates/EmailDocumentParser.cs tests/Rag.NET.Chunking.Templates.Tests/EmailDocumentParserTests.cs
git commit -m "feat(templates): add EmailDocumentParser"
```

---

### Task 7: Resume chunking

**Files:**
- Create: `src/Rag.NET.Chunking.Templates/ResumeChunkingOptions.cs`
- Create: `src/Rag.NET.Chunking.Templates/ResumeChunkingStrategy.cs`
- Create: `tests/Rag.NET.Chunking.Templates.Tests/ResumeChunkingStrategyTests.cs`

**Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class ResumeChunkingStrategyTests
{
    private const string ValidJson = """
        {
          "contact_info": "John Smith, john@example.com",
          "work_history": [
            {"company": "Tech Corp", "title": "Engineer", "dates": "2020-2023", "description": "Led platform."}
          ],
          "education": [
            {"institution": "State University", "degree": "B.S. CS", "dates": "2016-2020"}
          ],
          "skills": "C#, Python, JavaScript"
        }
        """;

    private static IChatClient MakeChatClient(string response)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
        return client;
    }

    private static IEnumerable<DocumentSection> ResumeDoc(string text = "John Smith\njohn@example.com") =>
    [
        new() { Text = text, DocumentId = new DocumentId("resume"), SectionIndex = 0 }
    ];

    [Fact]
    public async Task ChunkDocumentAsync_CallsLlmOnce()
    {
        var client = MakeChatClient(ValidJson);
        var sut = new ResumeChunkingStrategy(client, new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ResumeDoc().ToAsync(), new ChunkingOptions()))
            chunks.Add(c);

        await client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChunkDocumentAsync_ProducesChunkPerWorkHistoryEntry()
    {
        var sut = new ResumeChunkingStrategy(MakeChatClient(ValidJson), new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ResumeDoc().ToAsync(), new ChunkingOptions()))
            chunks.Add(c);

        Assert.Contains(chunks, c => c.Metadata.TryGetValue("section", out var s) && s == "work_history");
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsTemplateMetadata()
    {
        var sut = new ResumeChunkingStrategy(MakeChatClient(ValidJson), new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ResumeDoc().ToAsync(), new ChunkingOptions()))
            chunks.Add(c);

        Assert.All(chunks, c => Assert.Equal("resume", c.Metadata["template"]));
    }

    [Fact]
    public async Task ChunkDocumentAsync_FallsBackOnMalformedJson()
    {
        var sut = new ResumeChunkingStrategy(MakeChatClient("not valid json {{ "), new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ResumeDoc("Full resume text.").ToAsync(), new ChunkingOptions()))
            chunks.Add(c);

        // Should not throw; should yield at least one fallback chunk with the original text
        var fallback = Assert.Single(chunks);
        Assert.Contains("Full resume text.", fallback.Text, StringComparison.Ordinal);
    }
}
```

**Step 2: Implement options**

```csharp
// src/Rag.NET.Chunking.Templates/ResumeChunkingOptions.cs
using Microsoft.Extensions.AI;

namespace Rag.NET.Chunking.Templates;

public sealed class ResumeChunkingOptions
{
    /// <summary>Optional model override. Null = use constructor-injected IChatClient.</summary>
    public IChatClient? ChatClient { get; set; }

    public string Prompt { get; set; } = """
        Extract the following sections from this resume as JSON. Return ONLY valid JSON, no markdown.

        {
          "contact_info": "full contact block as a single string",
          "work_history": [{"company": "...", "title": "...", "dates": "...", "description": "..."}],
          "education": [{"institution": "...", "degree": "...", "dates": "..."}],
          "skills": "skills as a single string"
        }

        Resume:
        {text}
        """;
}
```

**Step 3: Implement strategy**

```csharp
// src/Rag.NET.Chunking.Templates/ResumeChunkingStrategy.cs
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rag.NET.Chunking.Templates;

public sealed class ResumeChunkingStrategy(
    IChatClient chatClient,
    ResumeChunkingOptions options,
    ILogger<ResumeChunkingStrategy>? logger = null) : IDocumentChunkingStrategy
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fullText = new System.Text.StringBuilder();
        DocumentId? docId = null;
        await foreach (var section in sections.WithCancellation(cancellationToken))
        {
            docId ??= section.DocumentId;
            if (fullText.Length > 0) fullText.Append("\n\n");
            fullText.Append(section.Text);
        }

        var id = docId ?? new DocumentId("unknown");
        var text = fullText.ToString();

        var activeClient = options.ChatClient ?? chatClient;
        var prompt = options.Prompt.Replace("{text}", text, StringComparison.Ordinal);

        JsonNode? parsed = null;
        try
        {
            var response = await activeClient
                .GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            parsed = JsonNode.Parse(response.Text ?? string.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resume LLM call or JSON parse failed for document {DocumentId}; falling back to full-text chunk.", id);
        }

        if (parsed is null)
        {
            yield return MakeChunk(text, id, 0, "full_text");
            yield break;
        }

        var index = 0;

        if (parsed["contact_info"]?.GetValue<string>() is { Length: > 0 } contact)
            yield return MakeChunk(contact, id, index++, "contact_info");

        if (parsed["work_history"] is JsonArray workHistory)
            foreach (var job in workHistory)
            {
                var jobText = FormatObject(job);
                if (!string.IsNullOrWhiteSpace(jobText))
                    yield return MakeChunk(jobText, id, index++, "work_history");
            }

        if (parsed["education"] is JsonArray education)
            foreach (var edu in education)
            {
                var eduText = FormatObject(edu);
                if (!string.IsNullOrWhiteSpace(eduText))
                    yield return MakeChunk(eduText, id, index++, "education");
            }

        if (parsed["skills"]?.GetValue<string>() is { Length: > 0 } skills)
            yield return MakeChunk(skills, id, index++, "skills");
    }

    private static TextChunk MakeChunk(string text, DocumentId docId, int index, string section) =>
        new()
        {
            Text = text,
            DocumentId = docId,
            ChunkIndex = index,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["template"] = "resume",
                ["section"] = section,
            },
        };

    private static string FormatObject(JsonNode? node)
    {
        if (node is null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        if (node is JsonObject obj)
            foreach (var prop in obj)
                sb.AppendLine($"{prop.Key}: {prop.Value}");
        return sb.ToString().Trim();
    }
}
```

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Chunking.Templates.Tests/ --filter "ResumeChunkingStrategyTests" -v minimal
```

Expected: PASS (4 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET.Chunking.Templates/ResumeChunkingOptions.cs src/Rag.NET.Chunking.Templates/ResumeChunkingStrategy.cs tests/Rag.NET.Chunking.Templates.Tests/ResumeChunkingStrategyTests.cs
git commit -m "feat(templates): add ResumeChunkingStrategy with LLM extraction and JSON fallback"
```

---

### Task 8: DI registration — RagBuilderExtensions

**Files:**
- Create: `src/Rag.NET.Chunking.Templates/RagBuilderExtensions.cs`
- Modify: `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` (add project reference)
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseChunkingTemplatesTests.cs`

**Step 1: Write the failing DI tests**

Add project reference to test csproj:
```xml
<!-- In tests/Rag.NET.Tests/Rag.NET.Tests.csproj, inside the existing <ItemGroup> for project references -->
<ProjectReference Include="..\..\src\Rag.NET.Chunking.Templates\Rag.NET.Chunking.Templates.csproj" />
```

Create `tests/Rag.NET.Tests/DependencyInjection/UseChunkingTemplatesTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.Templates;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseChunkingTemplatesTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UseLegalChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseLegalChunking()).BuildServiceProvider();
        Assert.IsType<LegalChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseBookChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseBookChunking()).BuildServiceProvider();
        Assert.IsType<BookChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseAcademicPaperChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseAcademicPaperChunking()).BuildServiceProvider();
        Assert.IsType<AcademicPaperChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseQAPairsChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseQAPairsChunking()).BuildServiceProvider();
        Assert.IsType<QAPairsChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseQAPairsChunking_RegistersIDocumentParser()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseQAPairsChunking()).BuildServiceProvider();
        Assert.NotNull(sp.GetService<QAPairsDocumentParser>());
    }

    [Fact]
    public void UseEmailChunking_RegistersIDocumentParser()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseEmailChunking()).BuildServiceProvider();
        Assert.NotNull(sp.GetService<EmailDocumentParser>());
    }

    [Fact]
    public void UseResumeChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseResumeChunking()).BuildServiceProvider();
        Assert.IsType<ResumeChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseLegalChunking_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseLegalChunking(o => o.MaxDepth = 2))
            .BuildServiceProvider();
        Assert.Equal(2, sp.GetRequiredService<LegalChunkingOptions>().MaxDepth);
    }
}
```

**Step 2: Run to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "UseChunkingTemplatesTests" -v minimal
```

Expected: compile error — `UseLegalChunking` etc. not found.

**Step 3: Implement RagBuilderExtensions**

Create `src/Rag.NET.Chunking.Templates/RagBuilderExtensions.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Chunking.Templates;

public static class RagBuilderExtensions
{
    public static TBuilder UseLegalChunking<TBuilder>(
        this TBuilder builder, Action<LegalChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new LegalChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<LegalChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<LegalChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<LegalChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseBookChunking<TBuilder>(
        this TBuilder builder, Action<BookChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new BookChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<BookChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<BookChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<BookChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseAcademicPaperChunking<TBuilder>(
        this TBuilder builder, Action<AcademicPaperChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new AcademicPaperChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<AcademicPaperChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<AcademicPaperChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<AcademicPaperChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseQAPairsChunking<TBuilder>(
        this TBuilder builder, Action<QAPairsChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new QAPairsChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<QAPairsDocumentParser>();
        builder.Services.AddSingleton<QAPairsChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<QAPairsChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseEmailChunking<TBuilder>(
        this TBuilder builder, Action<EmailChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new EmailChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<EmailDocumentParser>();
        return builder;
    }

    public static TBuilder UseResumeChunking<TBuilder>(
        this TBuilder builder, Action<ResumeChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new ResumeChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<ResumeChunkingStrategy>(sp =>
            new ResumeChunkingStrategy(
                opts.ChatClient ?? sp.GetRequiredService<IChatClient>(),
                opts,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<ResumeChunkingStrategy>>()));
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<ResumeChunkingStrategy>());
        return builder;
    }
}
```

> **Note on IDocumentChunkingStrategy registration:** Check how the pipeline resolves `IDocumentChunkingStrategy`. If it uses `GetService<IDocumentChunkingStrategy>()` (nullable), single registration is fine. If there's a conflict with `IChunkingStrategy` double-registration, look at how existing strategies handle it in `src/Rag.NET/DependencyInjection/`.

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "UseChunkingTemplatesTests" -v minimal
```

Expected: PASS (8 tests).

**Step 5: Run full test suite**

```bash
dotnet test tests/Rag.NET.Tests/ -v minimal
```

Expected: all pass, no regressions.

**Step 6: Commit**

```bash
git add src/Rag.NET.Chunking.Templates/RagBuilderExtensions.cs tests/Rag.NET.Tests/Rag.NET.Tests.csproj tests/Rag.NET.Tests/DependencyInjection/UseChunkingTemplatesTests.cs
git commit -m "feat(templates): add RagBuilderExtensions and DI registration for all 6 templates"
```

---

### Task 9: Update features.md

**Files:**
- Modify: `docs/reference/features.md`

**Step 1: Mark Domain-Specific Chunking Templates as Done**

In `docs/reference/features.md`, find the `Domain-Specific Chunking Templates` section and add `**Status:** ✅ Done`.

In the Priority table, change:
```
| [ ] | Domain-Specific Chunking Templates | High | Per-domain logic |
```
to:
```
| [x] | Domain-Specific Chunking Templates | High | Per-domain logic |
```

**Step 2: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark Domain-Specific Chunking Templates as done"
```
