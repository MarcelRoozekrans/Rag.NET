# Document Parsers Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add six document parsers (CSV, JSON, HTML, Word, Excel, PowerPoint) to Rag.NET so users can ingest common document formats into the RAG pipeline.

**Architecture:** CSV and JSON parsers live in the core `Rag.NET` package (BCL-only, no external dependencies) alongside the existing Text and Markdown parsers. HTML, Word, Excel, and PowerPoint each get their own NuGet package with a single `IDocumentParser` implementation, following the existing `Rag.NET.Parsers.Pdf` pattern.

**Tech Stack:** .NET 10, DocumentFormat.OpenXml (Office), AngleSharp (HTML), System.Text.Json (JSON), xunit.v3, Meziantou.Analyzer

**Note:** Markdown parser already exists in `src/Rag.NET/Parsers/MarkdownDocumentParser.cs` — skip.

---

## Reference patterns

Before implementing any parser, study these existing files to understand the patterns:

- **Parser interface:** `src/Rag.NET/Abstractions/IDocumentParser.cs` — `CanParse(contentType)` + `ParseAsync(stream, metadata)` returning `IAsyncEnumerable<DocumentSection>`
- **Models:** `src/Rag.NET/Models/DocumentSection.cs` (Text, DocumentId, SectionIndex, Heading, HeadingLevel, PageNumber) and `src/Rag.NET/Models/DocumentMetadata.cs`
- **Core parser example:** `src/Rag.NET/Parsers/TextDocumentParser.cs` — simplest parser, lives in core package
- **Core parser example 2:** `src/Rag.NET/Parsers/MarkdownDocumentParser.cs` — heading-aware parser in core
- **Separate package parser:** `src/Rag.NET.Parsers.Pdf/PdfDocumentParser.cs` — external dependency parser
- **DI extension:** `src/Rag.NET.Parsers.Pdf/PdfParserBuilderExtensions.cs` — `builder.AddParser<T>()`
- **Csproj pattern:** `src/Rag.NET.Parsers.Pdf/Rag.NET.Parsers.Pdf.csproj` — RootNamespace, PackageId, Description, ProjectReference to core
- **Test pattern:** `tests/Rag.NET.Tests/Parsers/TextDocumentParserTests.cs` — MemoryStream-based tests, `TestContext.Current.CancellationToken`
- **Test csproj:** `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` — xunit.v3 v2.*, Microsoft.NET.Test.Sdk v17.*

**Important conventions:**
- All `async IAsyncEnumerable` methods need `[EnumeratorCancellation]` attribute on the CancellationToken parameter
- Use `await Task.CompletedTask.ConfigureAwait(false);` at the end of `async IAsyncEnumerable` methods that don't actually await (to satisfy the async requirement)
- Library code must use `.ConfigureAwait(false)` on all awaits (Meziantou MA0004)
- Tests suppress MA0004 via `tests/Directory.Build.props`
- Use `using var` for disposables; use `await using (x.ConfigureAwait(false))` block syntax for async disposables
- `TreatWarningsAsErrors` is enabled globally
- Solution file is `Rag.NET.slnx` (XML format)

---

### Task 1: CSV Parser — Tests

**Files:**
- Create: `src/Rag.NET/Parsers/CsvDocumentParser.cs` (stub only in this task)
- Create: `tests/Rag.NET.Tests/Parsers/CsvDocumentParserTests.cs`

**Step 1: Write the test file**

Create `tests/Rag.NET.Tests/Parsers/CsvDocumentParserTests.cs`:

```csharp
using System.Text;
using Rag.NET.Models;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class CsvDocumentParserTests
{
    private readonly CsvDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.csv"
    };

    [Fact]
    public void CanParse_TextCsv_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("text/csv"));
    }

    [Fact]
    public void CanParse_TextPlain_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("text/plain"));
    }

    [Fact]
    public async Task ParseAsync_BasicCsv_ReturnsRowPerSection()
    {
        var csv = "Name,Age,City\nAlice,30,Amsterdam\nBob,25,Berlin";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Name: Alice | Age: 30 | City: Amsterdam", sections[0].Text);
        Assert.Equal("Name: Bob | Age: 25 | City: Berlin", sections[1].Text);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        var csv = "Col\nVal1\nVal2";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsNoSections()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_HeaderOnly_ReturnsNoSections()
    {
        var csv = "Name,Age,City";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SkipsEmptyRows()
    {
        var csv = "Name\nAlice\n\nBob";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~CsvDocumentParserTests" --no-restore`
Expected: FAIL (compilation error — CsvDocumentParser does not exist yet)

**Step 3: Commit**

```bash
git add tests/Rag.NET.Tests/Parsers/CsvDocumentParserTests.cs
git commit -m "test: add CSV parser tests (red)"
```

---

### Task 2: CSV Parser — Implementation

**Files:**
- Create: `src/Rag.NET/Parsers/CsvDocumentParser.cs`

**Step 1: Write the implementation**

Create `src/Rag.NET/Parsers/CsvDocumentParser.cs`:

```csharp
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers;

public sealed class CsvDocumentParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(headerLine))
        {
            yield break;
        }

        var headers = ParseCsvLine(headerLine);
        int sectionIndex = 0;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = ParseCsvLine(line);
            var pairs = new List<string>(headers.Length);

            for (int i = 0; i < headers.Length; i++)
            {
                var value = i < values.Length ? values[i] : string.Empty;
                pairs.Add($"{headers[i]}: {value}");
            }

            yield return new DocumentSection
            {
                Text = string.Join(" | ", pairs),
                DocumentId = metadata.DocumentId,
                SectionIndex = sectionIndex++,
            };
        }
    }

    private static string[] ParseCsvLine(string line) =>
        line.Split(',').Select(v => v.Trim()).ToArray();
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~CsvDocumentParserTests" --no-restore`
Expected: All 6 tests PASS

**Step 3: Commit**

```bash
git add src/Rag.NET/Parsers/CsvDocumentParser.cs
git commit -m "feat: add CSV document parser"
```

---

### Task 3: JSON Parser — Tests

**Files:**
- Create: `tests/Rag.NET.Tests/Parsers/JsonDocumentParserTests.cs`

**Step 1: Write the test file**

Create `tests/Rag.NET.Tests/Parsers/JsonDocumentParserTests.cs`:

```csharp
using System.Text;
using Rag.NET.Models;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class JsonDocumentParserTests
{
    private readonly JsonDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.json"
    };

    [Fact]
    public void CanParse_ApplicationJson_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("application/json"));
    }

    [Fact]
    public void CanParse_TextPlain_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("text/plain"));
    }

    [Fact]
    public async Task ParseAsync_Array_ReturnsSectionPerElement()
    {
        var json = """[{"name":"Alice"},{"name":"Bob"}]""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Alice", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Bob", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_SingleObject_ReturnsSingleSection()
    {
        var json = """{"name":"Alice","age":30}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Contains("Alice", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        var json = """[{"a":1},{"b":2}]""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    [Fact]
    public async Task ParseAsync_EmptyArray_ReturnsNoSections()
    {
        var json = "[]";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsNoSections()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~JsonDocumentParserTests" --no-restore`
Expected: FAIL (compilation error — JsonDocumentParser does not exist)

**Step 3: Commit**

```bash
git add tests/Rag.NET.Tests/Parsers/JsonDocumentParserTests.cs
git commit -m "test: add JSON parser tests (red)"
```

---

### Task 4: JSON Parser — Implementation

**Files:**
- Create: `src/Rag.NET/Parsers/JsonDocumentParser.cs`

**Step 1: Write the implementation**

Create `src/Rag.NET/Parsers/JsonDocumentParser.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers;

public sealed class JsonDocumentParser : IDocumentParser
{
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    public bool CanParse(string contentType) =>
        contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (stream.Length == 0)
        {
            yield break;
        }

        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        int sectionIndex = 0;

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return new DocumentSection
                {
                    Text = JsonSerializer.Serialize(element, s_writeOptions),
                    DocumentId = metadata.DocumentId,
                    SectionIndex = sectionIndex++,
                };
            }
        }
        else
        {
            yield return new DocumentSection
            {
                Text = JsonSerializer.Serialize(document.RootElement, s_writeOptions),
                DocumentId = metadata.DocumentId,
                SectionIndex = 0,
            };
        }
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~JsonDocumentParserTests" --no-restore`
Expected: All 6 tests PASS

**Step 3: Commit**

```bash
git add src/Rag.NET/Parsers/JsonDocumentParser.cs
git commit -m "feat: add JSON document parser"
```

---

### Task 5: HTML Parser — Project Setup

**Files:**
- Create: `src/Rag.NET.Parsers.Html/Rag.NET.Parsers.Html.csproj`
- Create: `tests/Rag.NET.Parsers.Html.Tests/Rag.NET.Parsers.Html.Tests.csproj`

**Step 1: Create the source project**

Create `src/Rag.NET.Parsers.Html/Rag.NET.Parsers.Html.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Parsers.Html</RootNamespace>
    <PackageId>Rag.NET.Parsers.Html</PackageId>
    <Description>HTML document parser for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="AngleSharp" Version="1.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create the test project**

Create `tests/Rag.NET.Parsers.Html.Tests/Rag.NET.Parsers.Html.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Parsers.Html\Rag.NET.Parsers.Html.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

**Step 3: Update solution file**

Add both projects to `Rag.NET.slnx` — add the src project under `/src/` folder and the test project under `/tests/` folder.

**Step 4: Verify build**

Run: `dotnet build src/Rag.NET.Parsers.Html`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.Html/Rag.NET.Parsers.Html.csproj tests/Rag.NET.Parsers.Html.Tests/Rag.NET.Parsers.Html.Tests.csproj Rag.NET.slnx
git commit -m "chore: scaffold HTML parser project and test project"
```

---

### Task 6: HTML Parser — Tests

**Files:**
- Create: `tests/Rag.NET.Parsers.Html.Tests/HtmlDocumentParserTests.cs`

**Step 1: Write the test file**

Create `tests/Rag.NET.Parsers.Html.Tests/HtmlDocumentParserTests.cs`:

```csharp
using System.Text;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Html.Tests;

public class HtmlDocumentParserTests
{
    private readonly HtmlDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.html"
    };

    [Fact]
    public void CanParse_TextHtml_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("text/html"));
    }

    [Fact]
    public void CanParse_ApplicationPdf_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/pdf"));
    }

    [Fact]
    public async Task ParseAsync_SplitsByHeadings()
    {
        var html = "<html><body><h1>Title</h1><p>Intro text.</p><h2>Section 1</h2><p>Content 1.</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Title", sections[0].Heading);
        Assert.Equal(1, sections[0].HeadingLevel);
        Assert.Contains("Intro text.", sections[0].Text, StringComparison.Ordinal);
        Assert.Equal("Section 1", sections[1].Heading);
        Assert.Equal(2, sections[1].HeadingLevel);
        Assert.Contains("Content 1.", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_NoHeadings_ReturnsSingleSection()
    {
        var html = "<html><body><p>Just some text.</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Contains("Just some text.", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_StripsScriptsAndStyles()
    {
        var html = "<html><head><style>body{}</style></head><body><script>alert(1)</script><p>Visible text.</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.DoesNotContain("alert", sections[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("body{}", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Visible text.", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_ConvertsLinksToTextUrl()
    {
        var html = "<html><body><p>Visit <a href=\"https://example.com\">Example</a> site.</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Contains("Example (https://example.com)", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_EmptyBody_ReturnsNoSections()
    {
        var html = "<html><body></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        var html = "<html><body><h1>A</h1><p>Text A</p><h2>B</h2><p>Text B</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Parsers.Html.Tests --filter "FullyQualifiedName~HtmlDocumentParserTests" --no-restore`
Expected: FAIL (compilation error — HtmlDocumentParser does not exist)

**Step 3: Commit**

```bash
git add tests/Rag.NET.Parsers.Html.Tests/HtmlDocumentParserTests.cs
git commit -m "test: add HTML parser tests (red)"
```

---

### Task 7: HTML Parser — Implementation

**Files:**
- Create: `src/Rag.NET.Parsers.Html/HtmlDocumentParser.cs`
- Create: `src/Rag.NET.Parsers.Html/HtmlParserBuilderExtensions.cs`

**Step 1: Write the implementation**

Create `src/Rag.NET.Parsers.Html/HtmlDocumentParser.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Html;

public sealed class HtmlDocumentParser : IDocumentParser
{
    private static readonly string[] s_headingTags = ["h1", "h2", "h3", "h4", "h5", "h6"];
    private static readonly string[] s_removeTags = ["script", "style", "nav", "footer", "header"];

    public bool CanParse(string contentType) =>
        contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(stream, cancellationToken).ConfigureAwait(false);

        // Remove non-content elements
        foreach (var tag in s_removeTags)
        {
            foreach (var element in document.QuerySelectorAll(tag).ToList())
            {
                element.Remove();
            }
        }

        // Convert links to "text (url)" format
        foreach (var link in document.QuerySelectorAll("a[href]").ToList())
        {
            var href = link.GetAttribute("href");
            var text = link.TextContent.Trim();
            if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(href))
            {
                link.TextContent = $"{text} ({href})";
            }
        }

        var body = document.Body;
        if (body is null)
        {
            yield break;
        }

        var headings = body.QuerySelectorAll(string.Join(", ", s_headingTags)).ToList();

        if (headings.Count == 0)
        {
            var text = GetCleanText(body);
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return new DocumentSection
                {
                    Text = text,
                    DocumentId = metadata.DocumentId,
                    SectionIndex = 0,
                };
            }
            yield break;
        }

        int sectionIndex = 0;

        for (int i = 0; i < headings.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var heading = headings[i];
            int headingLevel = heading.TagName[1] - '0';
            var headingText = heading.TextContent.Trim();

            var sectionContent = new StringBuilder();
            sectionContent.AppendLine(headingText);

            // Collect text from sibling elements until the next heading
            var sibling = heading.NextElementSibling;
            while (sibling is not null && !s_headingTags.Contains(sibling.TagName, StringComparer.OrdinalIgnoreCase))
            {
                var siblingText = GetCleanText(sibling);
                if (!string.IsNullOrWhiteSpace(siblingText))
                {
                    sectionContent.AppendLine(siblingText);
                }
                sibling = sibling.NextElementSibling;
            }

            var finalText = sectionContent.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(finalText))
            {
                yield return new DocumentSection
                {
                    Text = finalText,
                    DocumentId = metadata.DocumentId,
                    SectionIndex = sectionIndex++,
                    Heading = headingText,
                    HeadingLevel = headingLevel,
                };
            }
        }
    }

    private static string GetCleanText(IElement element)
    {
        var text = element.TextContent;
        // Normalize whitespace
        return string.Join(' ', text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
    }
}
```

**Step 2: Write the DI extension**

Create `src/Rag.NET.Parsers.Html/HtmlParserBuilderExtensions.cs`:

```csharp
using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.Html;

public static class HtmlParserBuilderExtensions
{
    public static RagBuilder AddHtmlParser(this RagBuilder builder)
    {
        builder.AddParser<HtmlDocumentParser>();
        return builder;
    }
}
```

**Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Parsers.Html.Tests --no-restore`
Expected: All 7 tests PASS

**Step 4: Commit**

```bash
git add src/Rag.NET.Parsers.Html/HtmlDocumentParser.cs src/Rag.NET.Parsers.Html/HtmlParserBuilderExtensions.cs
git commit -m "feat: add HTML document parser with AngleSharp"
```

---

### Task 8: Word Parser — Project Setup

**Files:**
- Create: `src/Rag.NET.Parsers.Word/Rag.NET.Parsers.Word.csproj`
- Create: `tests/Rag.NET.Parsers.Word.Tests/Rag.NET.Parsers.Word.Tests.csproj`

**Step 1: Create the source project**

Create `src/Rag.NET.Parsers.Word/Rag.NET.Parsers.Word.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Parsers.Word</RootNamespace>
    <PackageId>Rag.NET.Parsers.Word</PackageId>
    <Description>Word document parser for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="DocumentFormat.OpenXml" Version="3.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create the test project**

Create `tests/Rag.NET.Parsers.Word.Tests/Rag.NET.Parsers.Word.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Parsers.Word\Rag.NET.Parsers.Word.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

**Step 3: Update solution file**

Add both projects to `Rag.NET.slnx`.

**Step 4: Verify build**

Run: `dotnet build src/Rag.NET.Parsers.Word`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.Word/Rag.NET.Parsers.Word.csproj tests/Rag.NET.Parsers.Word.Tests/Rag.NET.Parsers.Word.Tests.csproj Rag.NET.slnx
git commit -m "chore: scaffold Word parser project and test project"
```

---

### Task 9: Word Parser — Tests

**Files:**
- Create: `tests/Rag.NET.Parsers.Word.Tests/WordDocumentParserTests.cs`

Tests create .docx files programmatically using OpenXml (no binary test files needed).

**Step 1: Write the test file**

Create `tests/Rag.NET.Parsers.Word.Tests/WordDocumentParserTests.cs`:

```csharp
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Word.Tests;

public class WordDocumentParserTests
{
    private readonly WordDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.docx"
    };

    [Fact]
    public void CanParse_Docx_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    [Fact]
    public void CanParse_Pdf_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/pdf"));
    }

    [Fact]
    public async Task ParseAsync_ParagraphsWithHeadings_SplitsByHeading()
    {
        using var stream = CreateDocx(doc =>
        {
            AddParagraph(doc, "Introduction", "Heading1");
            AddParagraph(doc, "This is the intro text.");
            AddParagraph(doc, "Details", "Heading2");
            AddParagraph(doc, "Some detail content.");
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Introduction", sections[0].Heading);
        Assert.Equal(1, sections[0].HeadingLevel);
        Assert.Contains("intro text", sections[0].Text, StringComparison.Ordinal);
        Assert.Equal("Details", sections[1].Heading);
        Assert.Equal(2, sections[1].HeadingLevel);
        Assert.Contains("detail content", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_NoHeadings_ReturnsSingleSection()
    {
        using var stream = CreateDocx(doc =>
        {
            AddParagraph(doc, "Just a normal paragraph.");
            AddParagraph(doc, "Another paragraph.");
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Contains("normal paragraph", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_EmptyDocument_ReturnsNoSections()
    {
        using var stream = CreateDocx(_ => { });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        using var stream = CreateDocx(doc =>
        {
            AddParagraph(doc, "H1", "Heading1");
            AddParagraph(doc, "Text 1");
            AddParagraph(doc, "H2", "Heading1");
            AddParagraph(doc, "Text 2");
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    private static MemoryStream CreateDocx(Action<Body> configure)
    {
        var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            configure(mainPart.Document.Body!);
            mainPart.Document.Save();
        }
        stream.Position = 0;
        return stream;
    }

    private static void AddParagraph(Body body, string text, string? styleId = null)
    {
        var para = new Paragraph();
        if (styleId is not null)
        {
            para.ParagraphProperties = new ParagraphProperties(
                new ParagraphStyleId { Val = styleId });
        }
        para.AppendChild(new Run(new Text(text)));
        body.AppendChild(para);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Parsers.Word.Tests --no-restore`
Expected: FAIL (compilation error — WordDocumentParser does not exist)

**Step 3: Commit**

```bash
git add tests/Rag.NET.Parsers.Word.Tests/WordDocumentParserTests.cs
git commit -m "test: add Word parser tests (red)"
```

---

### Task 10: Word Parser — Implementation

**Files:**
- Create: `src/Rag.NET.Parsers.Word/WordDocumentParser.cs`
- Create: `src/Rag.NET.Parsers.Word/WordParserBuilderExtensions.cs`

**Step 1: Write the implementation**

Create `src/Rag.NET.Parsers.Word/WordDocumentParser.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Word;

public sealed class WordDocumentParser : IDocumentParser
{
    private static readonly Dictionary<string, int> s_headingStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Heading1"] = 1,
        ["Heading2"] = 2,
        ["Heading3"] = 3,
        ["Heading4"] = 4,
        ["Heading5"] = 5,
        ["Heading6"] = 6,
    };

    public bool CanParse(string contentType) =>
        contentType.Equals("application/vnd.openxmlformats-officedocument.wordprocessingml.document", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document.Body;

        if (body is null)
        {
            yield break;
        }

        var paragraphs = body.Elements<Paragraph>().ToList();
        if (paragraphs.Count == 0)
        {
            yield break;
        }

        // Check if any headings exist
        bool hasHeadings = paragraphs.Any(p => GetHeadingLevel(p) is not null);

        if (!hasHeadings)
        {
            var allText = GetAllText(paragraphs);
            if (!string.IsNullOrWhiteSpace(allText))
            {
                yield return new DocumentSection
                {
                    Text = allText,
                    DocumentId = metadata.DocumentId,
                    SectionIndex = 0,
                };
            }
            yield break;
        }

        int sectionIndex = 0;
        string? currentHeading = null;
        int? currentHeadingLevel = null;
        var currentContent = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var headingLevel = GetHeadingLevel(paragraph);
            var text = paragraph.InnerText.Trim();

            if (headingLevel is not null)
            {
                // Emit previous section if exists
                if (currentHeading is not null)
                {
                    var sectionText = currentContent.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(sectionText))
                    {
                        yield return new DocumentSection
                        {
                            Text = sectionText,
                            DocumentId = metadata.DocumentId,
                            SectionIndex = sectionIndex++,
                            Heading = currentHeading,
                            HeadingLevel = currentHeadingLevel,
                        };
                    }
                }

                currentHeading = text;
                currentHeadingLevel = headingLevel;
                currentContent.Clear();
                currentContent.AppendLine(text);
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                currentContent.AppendLine(text);
            }
        }

        // Emit last section
        if (currentHeading is not null)
        {
            var sectionText = currentContent.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(sectionText))
            {
                yield return new DocumentSection
                {
                    Text = sectionText,
                    DocumentId = metadata.DocumentId,
                    SectionIndex = sectionIndex++,
                    Heading = currentHeading,
                    HeadingLevel = currentHeadingLevel,
                };
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static int? GetHeadingLevel(Paragraph paragraph)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is not null && s_headingStyles.TryGetValue(styleId, out var level))
        {
            return level;
        }
        return null;
    }

    private static string GetAllText(List<Paragraph> paragraphs)
    {
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            var text = p.InnerText.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text);
            }
        }
        return sb.ToString().Trim();
    }
}
```

**Step 2: Write the DI extension**

Create `src/Rag.NET.Parsers.Word/WordParserBuilderExtensions.cs`:

```csharp
using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.Word;

public static class WordParserBuilderExtensions
{
    public static RagBuilder AddWordParser(this RagBuilder builder)
    {
        builder.AddParser<WordDocumentParser>();
        return builder;
    }
}
```

**Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Parsers.Word.Tests --no-restore`
Expected: All 4 tests PASS

**Step 4: Commit**

```bash
git add src/Rag.NET.Parsers.Word/WordDocumentParser.cs src/Rag.NET.Parsers.Word/WordParserBuilderExtensions.cs
git commit -m "feat: add Word document parser with OpenXml"
```

---

### Task 11: Excel Parser — Project Setup

**Files:**
- Create: `src/Rag.NET.Parsers.Excel/Rag.NET.Parsers.Excel.csproj`
- Create: `tests/Rag.NET.Parsers.Excel.Tests/Rag.NET.Parsers.Excel.Tests.csproj`

**Step 1: Create the source project**

Create `src/Rag.NET.Parsers.Excel/Rag.NET.Parsers.Excel.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Parsers.Excel</RootNamespace>
    <PackageId>Rag.NET.Parsers.Excel</PackageId>
    <Description>Excel document parser for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="DocumentFormat.OpenXml" Version="3.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create the test project**

Create `tests/Rag.NET.Parsers.Excel.Tests/Rag.NET.Parsers.Excel.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Parsers.Excel\Rag.NET.Parsers.Excel.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

**Step 3: Update solution file**

Add both projects to `Rag.NET.slnx`.

**Step 4: Verify build**

Run: `dotnet build src/Rag.NET.Parsers.Excel`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.Excel/Rag.NET.Parsers.Excel.csproj tests/Rag.NET.Parsers.Excel.Tests/Rag.NET.Parsers.Excel.Tests.csproj Rag.NET.slnx
git commit -m "chore: scaffold Excel parser project and test project"
```

---

### Task 12: Excel Parser — Tests

**Files:**
- Create: `tests/Rag.NET.Parsers.Excel.Tests/ExcelDocumentParserTests.cs`

Tests create .xlsx files programmatically using OpenXml.

**Step 1: Write the test file**

Create `tests/Rag.NET.Parsers.Excel.Tests/ExcelDocumentParserTests.cs`:

```csharp
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Excel.Tests;

public class ExcelDocumentParserTests
{
    private readonly ExcelDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.xlsx"
    };

    [Fact]
    public void CanParse_Xlsx_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
    }

    [Fact]
    public void CanParse_Pdf_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/pdf"));
    }

    [Fact]
    public async Task ParseAsync_BasicSheet_ReturnsSectionPerRow()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>
        {
            ["Sheet1"] =
            [
                ["Name", "Age"],
                ["Alice", "30"],
                ["Bob", "25"],
            ]
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Name: Alice | Age: 30", sections[0].Text);
        Assert.Equal("Name: Bob | Age: 25", sections[1].Text);
    }

    [Fact]
    public async Task ParseAsync_SetsSheetNameAsHeading()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>
        {
            ["Employees"] =
            [
                ["Name"],
                ["Alice"],
            ]
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Equal("Employees", sections[0].Heading);
    }

    [Fact]
    public async Task ParseAsync_MultipleSheets_ProcessesAll()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>
        {
            ["Sheet1"] = [["Col"], ["Val1"]],
            ["Sheet2"] = [["Col"], ["Val2"]],
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Sheet1", sections[0].Heading);
        Assert.Equal("Sheet2", sections[1].Heading);
    }

    [Fact]
    public async Task ParseAsync_EmptySheet_ReturnsNoSections()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>
        {
            ["Sheet1"] = []
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>
        {
            ["Sheet1"] = [["C"], ["A"], ["B"]],
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    private static MemoryStream CreateXlsx(Dictionary<string, string[][]> sheets)
    {
        var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new Sheets());
            var docSheets = workbookPart.Workbook.GetFirstChild<Sheets>()!;

            uint sheetId = 1;
            foreach (var (sheetName, rows) in sheets)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();

                uint rowIndex = 1;
                foreach (var row in rows)
                {
                    var sheetRow = new Row { RowIndex = rowIndex };
                    int colIndex = 0;
                    foreach (var cellValue in row)
                    {
                        var colLetter = (char)('A' + colIndex);
                        sheetRow.AppendChild(new Cell
                        {
                            CellReference = $"{colLetter}{rowIndex}",
                            DataType = CellValues.InlineString,
                            InlineString = new InlineString(new Text(cellValue)),
                        });
                        colIndex++;
                    }
                    sheetData.AppendChild(sheetRow);
                    rowIndex++;
                }

                worksheetPart.Worksheet = new Worksheet(sheetData);
                worksheetPart.Worksheet.Save();

                docSheets.AppendChild(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = sheetId++,
                    Name = sheetName,
                });
            }

            workbookPart.Workbook.Save();
        }
        stream.Position = 0;
        return stream;
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Parsers.Excel.Tests --no-restore`
Expected: FAIL (compilation error — ExcelDocumentParser does not exist)

**Step 3: Commit**

```bash
git add tests/Rag.NET.Parsers.Excel.Tests/ExcelDocumentParserTests.cs
git commit -m "test: add Excel parser tests (red)"
```

---

### Task 13: Excel Parser — Implementation

**Files:**
- Create: `src/Rag.NET.Parsers.Excel/ExcelDocumentParser.cs`
- Create: `src/Rag.NET.Parsers.Excel/ExcelParserBuilderExtensions.cs`

**Step 1: Write the implementation**

Create `src/Rag.NET.Parsers.Excel/ExcelDocumentParser.cs`:

```csharp
using System.Runtime.CompilerServices;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Excel;

public sealed class ExcelDocumentParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;

        if (workbookPart is null)
        {
            yield break;
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? [];
        int sectionIndex = 0;

        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sheet.Id?.Value is null)
            {
                continue;
            }

            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
            var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>();

            if (sheetData is null)
            {
                continue;
            }

            var rows = sheetData.Elements<Row>().ToList();
            if (rows.Count < 2) // Need at least header + 1 data row
            {
                continue;
            }

            var headers = GetRowValues(rows[0], sharedStrings);
            var sheetName = sheet.Name?.Value;

            for (int i = 1; i < rows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var values = GetRowValues(rows[i], sharedStrings);
                var pairs = new List<string>(headers.Count);

                for (int j = 0; j < headers.Count; j++)
                {
                    var value = j < values.Count ? values[j] : string.Empty;
                    pairs.Add($"{headers[j]}: {value}");
                }

                var text = string.Join(" | ", pairs);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return new DocumentSection
                    {
                        Text = text,
                        DocumentId = metadata.DocumentId,
                        SectionIndex = sectionIndex++,
                        Heading = sheetName,
                    };
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static List<string> GetRowValues(Row row, SharedStringTable? sharedStrings)
    {
        var values = new List<string>();
        foreach (var cell in row.Elements<Cell>())
        {
            values.Add(GetCellValue(cell, sharedStrings));
        }
        return values;
    }

    private static string GetCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.CellValue is null)
        {
            if (cell.InlineString?.Text is not null)
            {
                return cell.InlineString.Text.Text;
            }
            return string.Empty;
        }

        var value = cell.CellValue.Text;

        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings is not null)
        {
            return sharedStrings.ElementAt(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).InnerText;
        }

        return value;
    }
}
```

**Step 2: Write the DI extension**

Create `src/Rag.NET.Parsers.Excel/ExcelParserBuilderExtensions.cs`:

```csharp
using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.Excel;

public static class ExcelParserBuilderExtensions
{
    public static RagBuilder AddExcelParser(this RagBuilder builder)
    {
        builder.AddParser<ExcelDocumentParser>();
        return builder;
    }
}
```

**Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Parsers.Excel.Tests --no-restore`
Expected: All 6 tests PASS

**Step 4: Commit**

```bash
git add src/Rag.NET.Parsers.Excel/ExcelDocumentParser.cs src/Rag.NET.Parsers.Excel/ExcelParserBuilderExtensions.cs
git commit -m "feat: add Excel document parser with OpenXml"
```

---

### Task 14: PowerPoint Parser — Project Setup

**Files:**
- Create: `src/Rag.NET.Parsers.PowerPoint/Rag.NET.Parsers.PowerPoint.csproj`
- Create: `tests/Rag.NET.Parsers.PowerPoint.Tests/Rag.NET.Parsers.PowerPoint.Tests.csproj`

**Step 1: Create the source project**

Create `src/Rag.NET.Parsers.PowerPoint/Rag.NET.Parsers.PowerPoint.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Parsers.PowerPoint</RootNamespace>
    <PackageId>Rag.NET.Parsers.PowerPoint</PackageId>
    <Description>PowerPoint document parser for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="DocumentFormat.OpenXml" Version="3.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create the test project**

Create `tests/Rag.NET.Parsers.PowerPoint.Tests/Rag.NET.Parsers.PowerPoint.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Parsers.PowerPoint\Rag.NET.Parsers.PowerPoint.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

**Step 3: Update solution file**

Add both projects to `Rag.NET.slnx`.

**Step 4: Verify build**

Run: `dotnet build src/Rag.NET.Parsers.PowerPoint`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.PowerPoint/Rag.NET.Parsers.PowerPoint.csproj tests/Rag.NET.Parsers.PowerPoint.Tests/Rag.NET.Parsers.PowerPoint.Tests.csproj Rag.NET.slnx
git commit -m "chore: scaffold PowerPoint parser project and test project"
```

---

### Task 15: PowerPoint Parser — Tests

**Files:**
- Create: `tests/Rag.NET.Parsers.PowerPoint.Tests/PowerPointDocumentParserTests.cs`

Tests create .pptx files programmatically using OpenXml.

**Step 1: Write the test file**

Create `tests/Rag.NET.Parsers.PowerPoint.Tests/PowerPointDocumentParserTests.cs`:

```csharp
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Rag.NET.Models;
using Xunit;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace Rag.NET.Parsers.PowerPoint.Tests;

public class PowerPointDocumentParserTests
{
    private readonly PowerPointDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.pptx"
    };

    [Fact]
    public void CanParse_Pptx_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("application/vnd.openxmlformats-officedocument.presentationml.presentation"));
    }

    [Fact]
    public void CanParse_Pdf_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/pdf"));
    }

    [Fact]
    public async Task ParseAsync_MultipleSlides_ReturnsSectionPerSlide()
    {
        using var stream = CreatePptx(["Slide One Text", "Slide Two Text"]);

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Slide One Text", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Slide Two Text", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_SetsPageNumber()
    {
        using var stream = CreatePptx(["Text 1", "Text 2"]);

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, sections[0].PageNumber);
        Assert.Equal(2, sections[1].PageNumber);
    }

    [Fact]
    public async Task ParseAsync_EmptyPresentation_ReturnsNoSections()
    {
        using var stream = CreatePptx([]);

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        using var stream = CreatePptx(["A", "B"]);

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    private static MemoryStream CreatePptx(string[] slideTexts)
    {
        var stream = new MemoryStream();
        using (var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new Presentation(new SlideIdList());

            var slideIdList = presentationPart.Presentation.SlideIdList!;
            uint slideId = 256;

            foreach (var text in slideTexts)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = new Slide(
                    new CommonSlideData(
                        new ShapeTree(
                            new NonVisualGroupShapeProperties(
                                new NonVisualDrawingProperties { Id = 1, Name = "" },
                                new NonVisualGroupShapeDrawingProperties(),
                                new ApplicationNonVisualDrawingProperties()),
                            new GroupShapeProperties(),
                            new Shape(
                                new NonVisualShapeProperties(
                                    new NonVisualDrawingProperties { Id = 2, Name = "TextBox" },
                                    new NonVisualShapeDrawingProperties(),
                                    new ApplicationNonVisualDrawingProperties()),
                                new ShapeProperties(),
                                new TextBody(
                                    new Drawing.BodyProperties(),
                                    new Drawing.Paragraph(
                                        new Drawing.Run(
                                            new Drawing.RunProperties { Language = "en-US" },
                                            new Drawing.Text(text))))))));

                slidePart.Slide.Save();

                slideIdList.AppendChild(new SlideId
                {
                    Id = slideId++,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart),
                });
            }

            presentationPart.Presentation.Save();
        }
        stream.Position = 0;
        return stream;
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Parsers.PowerPoint.Tests --no-restore`
Expected: FAIL (compilation error — PowerPointDocumentParser does not exist)

**Step 3: Commit**

```bash
git add tests/Rag.NET.Parsers.PowerPoint.Tests/PowerPointDocumentParserTests.cs
git commit -m "test: add PowerPoint parser tests (red)"
```

---

### Task 16: PowerPoint Parser — Implementation

**Files:**
- Create: `src/Rag.NET.Parsers.PowerPoint/PowerPointDocumentParser.cs`
- Create: `src/Rag.NET.Parsers.PowerPoint/PowerPointParserBuilderExtensions.cs`

**Step 1: Write the implementation**

Create `src/Rag.NET.Parsers.PowerPoint/PowerPointDocumentParser.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace Rag.NET.Parsers.PowerPoint;

public sealed class PowerPointDocumentParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("application/vnd.openxmlformats-officedocument.presentationml.presentation", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var document = PresentationDocument.Open(stream, false);
        var presentationPart = document.PresentationPart;

        if (presentationPart?.Presentation.SlideIdList is null)
        {
            yield break;
        }

        var slideIds = presentationPart.Presentation.SlideIdList.Elements<SlideId>();
        int sectionIndex = 0;
        int slideNumber = 0;

        foreach (var slideId in slideIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            slideNumber++;

            if (slideId.RelationshipId?.Value is null)
            {
                continue;
            }

            var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId.Value);
            var text = ExtractSlideText(slidePart);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            yield return new DocumentSection
            {
                Text = text,
                DocumentId = metadata.DocumentId,
                SectionIndex = sectionIndex++,
                PageNumber = slideNumber,
            };
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string ExtractSlideText(SlidePart slidePart)
    {
        var sb = new StringBuilder();

        foreach (var paragraph in slidePart.Slide.Descendants<Drawing.Paragraph>())
        {
            var paragraphText = new StringBuilder();
            foreach (var text in paragraph.Descendants<Drawing.Text>())
            {
                paragraphText.Append(text.Text);
            }

            var line = paragraphText.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString().Trim();
    }
}
```

**Step 2: Write the DI extension**

Create `src/Rag.NET.Parsers.PowerPoint/PowerPointParserBuilderExtensions.cs`:

```csharp
using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.PowerPoint;

public static class PowerPointParserBuilderExtensions
{
    public static RagBuilder AddPowerPointParser(this RagBuilder builder)
    {
        builder.AddParser<PowerPointDocumentParser>();
        return builder;
    }
}
```

**Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Parsers.PowerPoint.Tests --no-restore`
Expected: All 4 tests PASS

**Step 4: Commit**

```bash
git add src/Rag.NET.Parsers.PowerPoint/PowerPointDocumentParser.cs src/Rag.NET.Parsers.PowerPoint/PowerPointParserBuilderExtensions.cs
git commit -m "feat: add PowerPoint document parser with OpenXml"
```

---

### Task 17: Final — Full Build and Test Verification

**Step 1: Build the entire solution**

Run: `dotnet build Rag.NET.slnx`
Expected: Build succeeded with 0 errors

**Step 2: Run all tests**

Run: `dotnet test Rag.NET.slnx`
Expected: All tests pass (existing + new parser tests)

**Step 3: Commit solution file if not already committed**

Verify `Rag.NET.slnx` has all new projects. If any updates needed:

```bash
git add Rag.NET.slnx
git commit -m "chore: add all document parser projects to solution"
```
