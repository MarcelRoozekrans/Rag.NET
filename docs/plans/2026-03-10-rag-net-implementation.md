# Rag.NET Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a modular .NET 10 RAG pipeline library with pluggable document parsing, chunking, embedding, and vector storage.

**Architecture:** Core abstractions package (`Rag.NET`) with built-in text/Markdown parsers and chunking strategies. Separate `Rag.NET.PgVector` package for vector storage. Pipeline orchestrates: parse → chunk → embed → store → retrieve → (optional) generate. All wired via `Microsoft.Extensions.DependencyInjection`.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI, Npgsql + pgvector-dotnet, xUnit, NSubstitute

---

### Task 1: Project Scaffolding

**Files:**
- Create: `Rag.NET.slnx`
- Create: `Directory.Build.props`
- Create: `.gitignore`
- Create: `src/Rag.NET/Rag.NET.csproj`
- Create: `src/Rag.NET.PgVector/Rag.NET.PgVector.csproj`
- Create: `src/Rag.NET.Parsers.Pdf/Rag.NET.Parsers.Pdf.csproj`
- Create: `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`
- Create: `tests/Rag.NET.PgVector.Tests/Rag.NET.PgVector.Tests.csproj`
- Create: `samples/Rag.NET.Sample/Rag.NET.Sample.csproj`

**Step 1: Create .gitignore**

```
bin/
obj/
*.user
*.suo
.vs/
*.DotSettings.user
```

**Step 2: Create Directory.Build.props**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>preview</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Meziantou.Analyzer" Version="2.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

**Step 3: Create src/Rag.NET/Rag.NET.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET</RootNamespace>
    <PackageId>Rag.NET</PackageId>
    <Description>Modular RAG pipeline library for .NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>

</Project>
```

**Step 4: Create src/Rag.NET.PgVector/Rag.NET.PgVector.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.PgVector</RootNamespace>
    <PackageId>Rag.NET.PgVector</PackageId>
    <Description>pgvector vector store for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Npgsql" Version="9.*" />
    <PackageReference Include="Pgvector" Version="0.*" />
  </ItemGroup>

</Project>
```

**Step 5: Create src/Rag.NET.Parsers.Pdf/Rag.NET.Parsers.Pdf.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Parsers.Pdf</RootNamespace>
    <PackageId>Rag.NET.Parsers.Pdf</PackageId>
    <Description>PDF document parser for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="UglyToad.PdfPig" Version="0.*" />
  </ItemGroup>

</Project>
```

**Step 6: Create tests/Rag.NET.Tests/Rag.NET.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
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

**Step 7: Create tests/Rag.NET.PgVector.Tests/Rag.NET.PgVector.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.PgVector\Rag.NET.PgVector.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
    <PackageReference Include="Testcontainers.PostgreSql" Version="4.*" />
  </ItemGroup>

</Project>
```

**Step 8: Create samples/Rag.NET.Sample/Rag.NET.Sample.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET.PgVector\Rag.NET.PgVector.csproj" />
    <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="9.*" />
  </ItemGroup>

</Project>
```

**Step 9: Create Rag.NET.slnx**

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/Rag.NET/Rag.NET.csproj" />
    <Project Path="src/Rag.NET.PgVector/Rag.NET.PgVector.csproj" />
    <Project Path="src/Rag.NET.Parsers.Pdf/Rag.NET.Parsers.Pdf.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Rag.NET.Tests/Rag.NET.Tests.csproj" />
    <Project Path="tests/Rag.NET.PgVector.Tests/Rag.NET.PgVector.Tests.csproj" />
  </Folder>
  <Folder Name="/samples/">
    <Project Path="samples/Rag.NET.Sample/Rag.NET.Sample.csproj" />
  </Folder>
</Solution>
```

**Step 10: Verify build**

Run: `dotnet build Rag.NET.slnx`
Expected: Build succeeded with 0 errors.

**Step 11: Commit**

```bash
git add -A
git commit -m "feat: scaffold solution structure with all projects"
```

---

### Task 2: Core Models

**Files:**
- Create: `src/Rag.NET/Models/DocumentMetadata.cs`
- Create: `src/Rag.NET/Models/DocumentSection.cs`
- Create: `src/Rag.NET/Models/TextChunk.cs`
- Create: `src/Rag.NET/Models/EmbeddedChunk.cs`
- Create: `src/Rag.NET/Models/SearchResult.cs`
- Create: `src/Rag.NET/Models/RagResponse.cs`
- Create: `src/Rag.NET/Models/IngestionResult.cs`
- Create: `src/Rag.NET/Models/Options/ChunkingOptions.cs`
- Create: `src/Rag.NET/Models/Options/SearchOptions.cs`
- Create: `src/Rag.NET/Models/Options/RagOptions.cs`
- Create: `src/Rag.NET/Models/Options/RetrievalOptions.cs`

**Step 1: Create DocumentMetadata.cs**

```csharp
namespace Rag.NET.Models;

public sealed record DocumentMetadata
{
    public required string DocumentId { get; init; }
    public required string FileName { get; init; }
    public string? ContentType { get; init; }
    public Dictionary<string, string> Tags { get; init; } = [];
}
```

**Step 2: Create DocumentSection.cs**

```csharp
namespace Rag.NET.Models;

public sealed record DocumentSection
{
    public required string Text { get; init; }
    public required string DocumentId { get; init; }
    public int? HeadingLevel { get; init; }
    public string? Heading { get; init; }
    public int? PageNumber { get; init; }
    public int SectionIndex { get; init; }
}
```

**Step 3: Create TextChunk.cs**

```csharp
namespace Rag.NET.Models;

public sealed record TextChunk
{
    public required string Text { get; init; }
    public required string DocumentId { get; init; }
    public required int ChunkIndex { get; init; }
    public int StartPosition { get; init; }
    public int EndPosition { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}
```

**Step 4: Create EmbeddedChunk.cs**

```csharp
namespace Rag.NET.Models;

public sealed record EmbeddedChunk
{
    public required TextChunk Chunk { get; init; }
    public required ReadOnlyMemory<float> Embedding { get; init; }
}
```

**Step 5: Create SearchResult.cs**

```csharp
namespace Rag.NET.Models;

public sealed record SearchResult
{
    public required TextChunk Chunk { get; init; }
    public required double Score { get; init; }
}
```

**Step 6: Create RagResponse.cs**

```csharp
namespace Rag.NET.Models;

public sealed record RagResponse
{
    public required string Answer { get; init; }
    public required IReadOnlyList<SearchResult> Sources { get; init; }
}
```

**Step 7: Create IngestionResult.cs**

```csharp
namespace Rag.NET.Models;

public sealed record IngestionResult
{
    public required string DocumentId { get; init; }
    public required int ChunksStored { get; init; }
}
```

**Step 8: Create Options/ChunkingOptions.cs**

```csharp
namespace Rag.NET.Models.Options;

public sealed class ChunkingOptions
{
    public int MaxChunkSize { get; set; } = 512;
    public int Overlap { get; set; } = 50;
}
```

**Step 9: Create Options/SearchOptions.cs**

```csharp
namespace Rag.NET.Models.Options;

public sealed class SearchOptions
{
    public int TopK { get; set; } = 5;
    public double MinScore { get; set; } = 0.0;
    public Dictionary<string, string>? MetadataFilter { get; set; }
}
```

**Step 10: Create Options/RetrievalOptions.cs**

```csharp
namespace Rag.NET.Models.Options;

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 5;
    public double MinScore { get; set; } = 0.0;
    public Dictionary<string, string>? MetadataFilter { get; set; }
}
```

**Step 11: Create Options/RagOptions.cs**

```csharp
namespace Rag.NET.Models.Options;

public sealed class RagOptions
{
    public int TopK { get; set; } = 5;
    public double MinScore { get; set; } = 0.0;
    public string? SystemPrompt { get; set; }
    public float? Temperature { get; set; }
}
```

**Step 12: Verify build**

Run: `dotnet build src/Rag.NET/Rag.NET.csproj`
Expected: Build succeeded.

**Step 13: Commit**

```bash
git add src/Rag.NET/Models/
git commit -m "feat: add core domain models and options"
```

---

### Task 3: Core Abstractions

**Files:**
- Create: `src/Rag.NET/Abstractions/IDocumentParser.cs`
- Create: `src/Rag.NET/Abstractions/IChunkingStrategy.cs`
- Create: `src/Rag.NET/Abstractions/IVectorStore.cs`
- Create: `src/Rag.NET/Abstractions/IRagPipeline.cs`

**Step 1: Create IDocumentParser.cs**

```csharp
using Rag.NET.Models;

namespace Rag.NET.Abstractions;

public interface IDocumentParser
{
    bool CanParse(string contentType);
    IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default);
}
```

**Step 2: Create IChunkingStrategy.cs**

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

public interface IChunkingStrategy
{
    IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        CancellationToken cancellationToken = default);
}
```

**Step 3: Create IVectorStore.cs**

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

public interface IVectorStore
{
    Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default);

    Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default);
}
```

**Step 4: Create IRagPipeline.cs**

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

public interface IRagPipeline
{
    Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

**Step 5: Verify build**

Run: `dotnet build src/Rag.NET/Rag.NET.csproj`
Expected: Build succeeded.

**Step 6: Commit**

```bash
git add src/Rag.NET/Abstractions/
git commit -m "feat: add core abstraction interfaces"
```

---

### Task 4: Fixed-Size Chunking Strategy (TDD)

**Files:**
- Create: `tests/Rag.NET.Tests/Chunking/FixedSizeChunkingStrategyTests.cs`
- Create: `src/Rag.NET/Chunking/FixedSizeChunkingStrategy.cs`

**Step 1: Write the failing tests**

```csharp
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Tests.Chunking;

public class FixedSizeChunkingStrategyTests
{
    private readonly FixedSizeChunkingStrategy _sut = new();

    private static DocumentSection CreateSection(string text) => new()
    {
        Text = text,
        DocumentId = "doc-1",
        SectionIndex = 0
    };

    [Fact]
    public async Task ChunkAsync_TextShorterThanMax_ReturnsSingleChunk()
    {
        var section = CreateSection("Short text.");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.Single(chunks);
        Assert.Equal("Short text.", chunks[0].Text);
        Assert.Equal(0, chunks[0].ChunkIndex);
    }

    [Fact]
    public async Task ChunkAsync_TextLongerThanMax_SplitsIntoMultipleChunks()
    {
        var section = CreateSection("AAAAAAAAAA BBBBBBBBBB CCCCCCCCCC");
        var options = new ChunkingOptions { MaxChunkSize = 10, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Text.Length <= 10);
        }
    }

    [Fact]
    public async Task ChunkAsync_WithOverlap_ChunksOverlap()
    {
        var text = string.Join(" ", Enumerable.Range(0, 20).Select(i => $"word{i}"));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 30, Overlap = 10 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.True(chunks.Count >= 2);
        for (int i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].StartPosition < chunks[i - 1].EndPosition,
                "Chunks should overlap");
        }
    }

    [Fact]
    public async Task ChunkAsync_PreservesDocumentId()
    {
        var section = CreateSection("Some text here.");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.All(chunks, c => Assert.Equal("doc-1", c.DocumentId));
    }

    [Fact]
    public async Task ChunkAsync_AssignsIncrementingChunkIndex()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 100));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkIndex);
        }
    }

    [Fact]
    public async Task ChunkAsync_EmptyText_ReturnsNoChunks()
    {
        var section = CreateSection("");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.Empty(chunks);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "FixedSizeChunking" --no-build 2>&1 || true`
Expected: Compilation error — `FixedSizeChunkingStrategy` does not exist.

**Step 3: Write minimal implementation**

```csharp
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public sealed class FixedSizeChunkingStrategy : IChunkingStrategy
{
    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(section.Text))
        {
            yield break;
        }

        var text = section.Text;
        int chunkIndex = 0;
        int position = 0;

        while (position < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int end = Math.Min(position + options.MaxChunkSize, text.Length);

            // Try to break at a space boundary if not at the end
            if (end < text.Length)
            {
                int lastSpace = text.LastIndexOf(' ', end - 1, end - position);
                if (lastSpace > position)
                {
                    end = lastSpace;
                }
            }

            var chunkText = text[position..end].Trim();

            if (chunkText.Length > 0)
            {
                yield return new TextChunk
                {
                    Text = chunkText,
                    DocumentId = section.DocumentId,
                    ChunkIndex = chunkIndex++,
                    StartPosition = position,
                    EndPosition = end
                };
            }

            int advance = end - position - options.Overlap;
            if (advance <= 0)
            {
                advance = end - position;
            }

            position += advance;
        }

        await Task.CompletedTask;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "FixedSizeChunking" -v minimal`
Expected: All 6 tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/Chunking/FixedSizeChunkingStrategy.cs tests/Rag.NET.Tests/Chunking/FixedSizeChunkingStrategyTests.cs
git commit -m "feat: add fixed-size chunking strategy with tests"
```

---

### Task 5: Recursive Chunking Strategy (TDD)

**Files:**
- Create: `tests/Rag.NET.Tests/Chunking/RecursiveChunkingStrategyTests.cs`
- Create: `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs`

**Step 1: Write the failing tests**

```csharp
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Tests.Chunking;

public class RecursiveChunkingStrategyTests
{
    private readonly RecursiveChunkingStrategy _sut = new();

    private static DocumentSection CreateSection(string text) => new()
    {
        Text = text,
        DocumentId = "doc-1",
        SectionIndex = 0
    };

    [Fact]
    public async Task ChunkAsync_SplitsByParagraphsFirst()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 200, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.Equal(2, chunks.Count);
        Assert.Equal("First paragraph.", chunks[0].Text);
        Assert.Equal("Second paragraph.", chunks[1].Text);
    }

    [Fact]
    public async Task ChunkAsync_FallsBackToSentences_WhenParagraphTooLong()
    {
        var text = "First sentence. Second sentence. Third sentence.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 20));
    }

    [Fact]
    public async Task ChunkAsync_ShortText_ReturnsSingleChunk()
    {
        var section = CreateSection("Hello.");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.Single(chunks);
        Assert.Equal("Hello.", chunks[0].Text);
    }

    [Fact]
    public async Task ChunkAsync_EmptyText_ReturnsNoChunks()
    {
        var section = CreateSection("");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_PreservesDocumentIdAndChunkIndex()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 200, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options).ToListAsync();

        Assert.All(chunks, c => Assert.Equal("doc-1", c.DocumentId));
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(1, chunks[1].ChunkIndex);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "RecursiveChunking" --no-build 2>&1 || true`
Expected: Compilation error — `RecursiveChunkingStrategy` does not exist.

**Step 3: Write minimal implementation**

Separators in priority order: `\n\n` (paragraph), `\n` (line), `. ` (sentence), ` ` (word). Try the highest-priority separator first; if a segment is still too large, recurse with the next separator.

```csharp
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public sealed class RecursiveChunkingStrategy : IChunkingStrategy
{
    private static readonly string[] Separators = ["\n\n", "\n", ". ", " "];

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(section.Text))
        {
            yield break;
        }

        int chunkIndex = 0;
        foreach (var text in SplitRecursively(section.Text, options.MaxChunkSize, 0))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new TextChunk
            {
                Text = text,
                DocumentId = section.DocumentId,
                ChunkIndex = chunkIndex++,
                StartPosition = 0,
                EndPosition = text.Length
            };
        }

        await Task.CompletedTask;
    }

    private static IEnumerable<string> SplitRecursively(string text, int maxSize, int separatorIndex)
    {
        if (text.Length <= maxSize)
        {
            var trimmed = text.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
            yield break;
        }

        if (separatorIndex >= Separators.Length)
        {
            // Hard split as last resort
            for (int i = 0; i < text.Length; i += maxSize)
            {
                var segment = text.Substring(i, Math.Min(maxSize, text.Length - i)).Trim();
                if (segment.Length > 0)
                {
                    yield return segment;
                }
            }
            yield break;
        }

        var separator = Separators[separatorIndex];
        var parts = text.Split(separator);

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (part.Length <= maxSize)
            {
                yield return part.Trim();
            }
            else
            {
                foreach (var sub in SplitRecursively(part, maxSize, separatorIndex + 1))
                {
                    yield return sub;
                }
            }
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "RecursiveChunking" -v minimal`
Expected: All 5 tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs tests/Rag.NET.Tests/Chunking/RecursiveChunkingStrategyTests.cs
git commit -m "feat: add recursive chunking strategy with tests"
```

---

### Task 6: Text Document Parser (TDD)

**Files:**
- Create: `tests/Rag.NET.Tests/Parsers/TextDocumentParserTests.cs`
- Create: `src/Rag.NET/Parsers/TextDocumentParser.cs`

**Step 1: Write the failing tests**

```csharp
using System.Text;
using Rag.NET.Models;
using Rag.NET.Parsers;

namespace Rag.NET.Tests.Parsers;

public class TextDocumentParserTests
{
    private readonly TextDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.txt"
    };

    [Fact]
    public void CanParse_TextPlain_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("text/plain"));
    }

    [Fact]
    public void CanParse_ApplicationJson_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/json"));
    }

    [Fact]
    public async Task ParseAsync_SimpleText_ReturnsSingleSection()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, world!"));
        var metadata = CreateMetadata();

        var sections = await _sut.ParseAsync(stream, metadata).ToListAsync();

        Assert.Single(sections);
        Assert.Equal("Hello, world!", sections[0].Text);
        Assert.Equal("doc-1", sections[0].DocumentId);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsNoSections()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));
        var metadata = CreateMetadata();

        var sections = await _sut.ParseAsync(stream, metadata).ToListAsync();

        Assert.Empty(sections);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "TextDocumentParser" --no-build 2>&1 || true`
Expected: Compilation error.

**Step 3: Write minimal implementation**

```csharp
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers;

public sealed class TextDocumentParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        yield return new DocumentSection
        {
            Text = text,
            DocumentId = metadata.DocumentId,
            SectionIndex = 0
        };
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "TextDocumentParser" -v minimal`
Expected: All 4 tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/Parsers/TextDocumentParser.cs tests/Rag.NET.Tests/Parsers/TextDocumentParserTests.cs
git commit -m "feat: add text document parser with tests"
```

---

### Task 7: Markdown Document Parser (TDD)

**Files:**
- Create: `tests/Rag.NET.Tests/Parsers/MarkdownDocumentParserTests.cs`
- Create: `src/Rag.NET/Parsers/MarkdownDocumentParser.cs`

**Step 1: Write the failing tests**

```csharp
using System.Text;
using Rag.NET.Models;
using Rag.NET.Parsers;

namespace Rag.NET.Tests.Parsers;

public class MarkdownDocumentParserTests
{
    private readonly MarkdownDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.md"
    };

    [Theory]
    [InlineData("text/markdown")]
    [InlineData("text/x-markdown")]
    public void CanParse_MarkdownTypes_ReturnsTrue(string contentType)
    {
        Assert.True(_sut.CanParse(contentType));
    }

    [Fact]
    public async Task ParseAsync_SplitsByHeadings()
    {
        var md = "# Title\n\nIntro text.\n\n## Section 1\n\nContent 1.\n\n## Section 2\n\nContent 2.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(md));

        var sections = await _sut.ParseAsync(stream, CreateMetadata()).ToListAsync();

        Assert.Equal(3, sections.Count);
        Assert.Contains("Title", sections[0].Text);
        Assert.Equal(1, sections[0].HeadingLevel);
        Assert.Contains("Content 1", sections[1].Text);
        Assert.Equal(2, sections[1].HeadingLevel);
        Assert.Contains("Content 2", sections[2].Text);
    }

    [Fact]
    public async Task ParseAsync_NoHeadings_ReturnsSingleSection()
    {
        var md = "Just some plain text in markdown.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(md));

        var sections = await _sut.ParseAsync(stream, CreateMetadata()).ToListAsync();

        Assert.Single(sections);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsNoSections()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));

        var sections = await _sut.ParseAsync(stream, CreateMetadata()).ToListAsync();

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_PreservesDocumentId()
    {
        var md = "# Heading\n\nText.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(md));

        var sections = await _sut.ParseAsync(stream, CreateMetadata()).ToListAsync();

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "MarkdownDocumentParser" --no-build 2>&1 || true`
Expected: Compilation error.

**Step 3: Write minimal implementation**

Splits Markdown by ATX headings (`# ...`). Each heading starts a new section. Content before the first heading becomes its own section.

```csharp
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers;

public sealed partial class MarkdownDocumentParser : IDocumentParser
{
    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    public bool CanParse(string contentType) =>
        contentType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("text/x-markdown", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var matches = HeadingRegex().Matches(text);

        if (matches.Count == 0)
        {
            yield return new DocumentSection
            {
                Text = text.Trim(),
                DocumentId = metadata.DocumentId,
                SectionIndex = 0
            };
            yield break;
        }

        int sectionIndex = 0;

        // Content before first heading
        if (matches[0].Index > 0)
        {
            var preText = text[..matches[0].Index].Trim();
            if (preText.Length > 0)
            {
                yield return new DocumentSection
                {
                    Text = preText,
                    DocumentId = metadata.DocumentId,
                    SectionIndex = sectionIndex++
                };
            }
        }

        for (int i = 0; i < matches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = matches[i];
            int headingLevel = match.Groups[1].Value.Length;
            string heading = match.Groups[2].Value.Trim();

            int contentStart = match.Index;
            int contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var sectionText = text[contentStart..contentEnd].Trim();

            if (sectionText.Length > 0)
            {
                yield return new DocumentSection
                {
                    Text = sectionText,
                    DocumentId = metadata.DocumentId,
                    SectionIndex = sectionIndex++,
                    HeadingLevel = headingLevel,
                    Heading = heading
                };
            }
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "MarkdownDocumentParser" -v minimal`
Expected: All 5 tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/Parsers/MarkdownDocumentParser.cs tests/Rag.NET.Tests/Parsers/MarkdownDocumentParserTests.cs
git commit -m "feat: add markdown document parser with tests"
```

---

### Task 8: RAG Pipeline (TDD)

**Files:**
- Create: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`
- Create: `src/Rag.NET/Pipeline/RagPipeline.cs`

**Step 1: Write the failing tests**

Tests use NSubstitute to mock all dependencies. This tests the orchestration logic.

```csharp
using System.Text;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;

namespace Rag.NET.Tests.Pipeline;

public class RagPipelineTests
{
    private readonly IDocumentParser _parser = Substitute.For<IDocumentParser>();
    private readonly IChunkingStrategy _chunker = Substitute.For<IChunkingStrategy>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly RagPipeline _sut;

    public RagPipelineTests()
    {
        _parser.CanParse(Arg.Any<string>()).Returns(true);
        _sut = new RagPipeline(
            [_parser],
            _chunker,
            _vectorStore,
            _embedder,
            chatClient: null,
            new ChunkingOptions());
    }

    [Fact]
    public async Task IngestAsync_OrchestatesParseChunkEmbedStore()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello world", DocumentId = "doc-1", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello world", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));

        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello world"));

        var result = await _sut.IngestAsync(stream, metadata);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(1, result.ChunksStored);
        await _vectorStore.Received(1).StoreAsync(
            Arg.Is<IReadOnlyList<EmbeddedChunk>>(c => c.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_EmbedsQueryAndSearches()
    {
        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var searchResult = new SearchResult
        {
            Chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 },
            Score = 0.95
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([searchResult]);

        var results = await _sut.RetrieveAsync("test query");

        Assert.Single(results);
        Assert.Equal(0.95, results[0].Score);
    }

    [Fact]
    public async Task AskAsync_WithoutChatClient_ThrowsInvalidOperation()
    {
        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.AskAsync("question"));
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipeline" --no-build 2>&1 || true`
Expected: Compilation error.

**Step 3: Write minimal implementation**

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Pipeline;

public sealed class RagPipeline(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IChatClient? chatClient,
    ChunkingOptions chunkingOptions) : IRagPipeline
{
    private const string DefaultSystemPrompt =
        "Answer the user's question based only on the provided context. " +
        "If the context doesn't contain enough information, say so. " +
        "Cite which sources you used.";

    public async Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var parser = parsers.FirstOrDefault(p => p.CanParse(metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{metadata.ContentType}'.");

        var chunks = new List<TextChunk>();

        await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken))
        {
            await foreach (var chunk in chunkingStrategy.ChunkAsync(section, chunkingOptions, cancellationToken))
            {
                chunks.Add(chunk);
            }
        }

        if (chunks.Count == 0)
        {
            return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 0 };
        }

        var texts = chunks.Select(c => c.Text).ToList();
        var embeddings = await embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken);

        var embeddedChunks = chunks
            .Zip(embeddings, (chunk, embedding) => new EmbeddedChunk
            {
                Chunk = chunk,
                Embedding = embedding.Vector
            })
            .ToList();

        await vectorStore.StoreAsync(embeddedChunks, cancellationToken);

        return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = embeddedChunks.Count };
    }

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();
        var queryEmbeddings = await embeddingGenerator.GenerateAsync(
            [query], cancellationToken: cancellationToken);

        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter
        };

        return await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken);
    }

    public async Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (chatClient is null)
        {
            throw new InvalidOperationException(
                "IChatClient is not registered. Register an IChatClient in DI to use AskAsync.");
        }

        var opts = options ?? new RagOptions();
        var retrievalOptions = new RetrievalOptions { TopK = opts.TopK, MinScore = opts.MinScore };
        var sources = await RetrieveAsync(query, retrievalOptions, cancellationToken);

        var context = string.Join("\n\n---\n\n",
            sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}"));

        var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}")
        };

        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
        {
            chatOptions.Temperature = opts.Temperature.Value;
        }

        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);

        return new RagResponse
        {
            Answer = response.Text ?? string.Empty,
            Sources = sources
        };
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "RagPipeline" -v minimal`
Expected: All 3 tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: add RAG pipeline orchestration with tests"
```

---

### Task 9: DI Registration

**Files:**
- Create: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Create: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`

**Step 1: Create RagBuilder.cs**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;

namespace Rag.NET.DependencyInjection;

public sealed class RagBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    public RagBuilder UseChunkingStrategy<TStrategy>(Action<ChunkingOptions>? configure = null)
        where TStrategy : class, IChunkingStrategy
    {
        Services.AddSingleton<IChunkingStrategy, TStrategy>();

        if (configure is not null)
        {
            var options = new ChunkingOptions();
            configure(options);
            Services.AddSingleton(options);
        }

        return this;
    }

    public RagBuilder AddParser<TParser>() where TParser : class, IDocumentParser
    {
        Services.AddSingleton<IDocumentParser, TParser>();
        return this;
    }
}
```

**Step 2: Create ServiceCollectionExtensions.cs**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Rag.NET.Pipeline;

namespace Rag.NET.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNet(
        this IServiceCollection services,
        Action<RagBuilder>? configure = null)
    {
        // Register defaults
        services.AddSingleton<IDocumentParser, TextDocumentParser>();
        services.AddSingleton<IDocumentParser, MarkdownDocumentParser>();

        services.TryAddSingleton<ChunkingOptions>();
        services.TryAddSingleton<IChunkingStrategy, RecursiveChunkingStrategy>();

        services.AddSingleton<IRagPipeline>(sp =>
        {
            var parsers = sp.GetServices<IDocumentParser>();
            var chunker = sp.GetRequiredService<IChunkingStrategy>();
            var store = sp.GetRequiredService<IVectorStore>();
            var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            var chatClient = sp.GetService<IChatClient>();
            var options = sp.GetRequiredService<ChunkingOptions>();

            return new RagPipeline(parsers, chunker, store, embedder, chatClient, options);
        });

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);

        return services;
    }
}
```

Note: `ServiceCollectionExtensions.cs` needs a `using Microsoft.Extensions.DependencyInjection.Extensions;` for `TryAddSingleton`, and `using Microsoft.Extensions.AI;` for the AI types.

**Step 3: Verify build**

Run: `dotnet build src/Rag.NET/Rag.NET.csproj`
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add src/Rag.NET/DependencyInjection/
git commit -m "feat: add DI registration with RagBuilder"
```

---

### Task 10: PgVector Store Implementation (TDD)

**Files:**
- Create: `src/Rag.NET.PgVector/PgVectorStore.cs`
- Create: `src/Rag.NET.PgVector/PgVectorBuilderExtensions.cs`
- Create: `tests/Rag.NET.PgVector.Tests/PgVectorStoreTests.cs`

**Step 1: Write failing integration tests**

These tests use Testcontainers to spin up a real PostgreSQL + pgvector instance.

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Testcontainers.PostgreSql;

namespace Rag.NET.PgVector.Tests;

public class PgVectorStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .Build();

    private PgVectorStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _sut = new PgVectorStore(_postgres.GetConnectionString(), vectorDimensions: 3);
        await _sut.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task StoreAndSearch_ReturnsRelevantResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "cats are great", DocumentId = "doc-1", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f }
            },
            new()
            {
                Chunk = new TextChunk { Text = "dogs are great", DocumentId = "doc-1", ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 1.0f, 0.0f }
            }
        };

        await _sut.StoreAsync(chunks);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 });

        Assert.Single(results);
        Assert.Equal("cats are great", results[0].Chunk.Text);
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "text1", DocumentId = "doc-to-delete", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f }
            }
        };

        await _sut.StoreAsync(chunks);
        await _sut.DeleteByDocumentIdAsync("doc-to-delete");

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 });

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_RespectsMinScore()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "close match", DocumentId = "doc-1", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f }
            },
            new()
            {
                Chunk = new TextChunk { Text = "far match", DocumentId = "doc-1", ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 0.0f, 1.0f }
            }
        };

        await _sut.StoreAsync(chunks);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10, MinScore = 0.9 });

        Assert.Single(results);
        Assert.Equal("close match", results[0].Chunk.Text);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.PgVector.Tests --filter "PgVectorStore" --no-build 2>&1 || true`
Expected: Compilation error.

**Step 3: Write PgVectorStore implementation**

```csharp
using System.Text.Json;
using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.PgVector;

public sealed class PgVectorStore : IVectorStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _vectorDimensions;

    public PgVectorStore(string connectionString, int vectorDimensions = 1536)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        _dataSource = dataSourceBuilder.Build();
        _vectorDimensions = vectorDimensions;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var enableExt = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", conn);
        await enableExt.ExecuteNonQueryAsync(cancellationToken);

        await using var createTable = new NpgsqlCommand($$"""
            CREATE TABLE IF NOT EXISTS rag_chunks (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                document_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                text TEXT NOT NULL,
                metadata JSONB NOT NULL DEFAULT '{}',
                embedding vector({{_vectorDimensions}}) NOT NULL
            )
            """, conn);
        await createTable.ExecuteNonQueryAsync(cancellationToken);

        await using var createIndex = new NpgsqlCommand(
            "CREATE INDEX IF NOT EXISTS idx_rag_chunks_document_id ON rag_chunks (document_id)", conn);
        await createIndex.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var chunk in chunks)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO rag_chunks (document_id, chunk_index, text, metadata, embedding)
                VALUES ($1, $2, $3, $4, $5)
                """, conn);

            cmd.Parameters.AddWithValue(chunk.Chunk.DocumentId);
            cmd.Parameters.AddWithValue(chunk.Chunk.ChunkIndex);
            cmd.Parameters.AddWithValue(chunk.Chunk.Text);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(chunk.Chunk.Metadata));
            cmd.Parameters.AddWithValue(new Vector(chunk.Embedding.ToArray()));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Use cosine distance: 1 - distance = similarity score
        await using var cmd = new NpgsqlCommand($$"""
            SELECT document_id, chunk_index, text, metadata,
                   1 - (embedding <=> $1) AS score
            FROM rag_chunks
            WHERE 1 - (embedding <=> $1) >= $2
            ORDER BY embedding <=> $1
            LIMIT $3
            """, conn);

        cmd.Parameters.AddWithValue(new Vector(queryEmbedding.ToArray()));
        cmd.Parameters.AddWithValue(options.MinScore);
        cmd.Parameters.AddWithValue(options.TopK);

        var results = new List<SearchResult>();

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
                reader.GetString(3)) ?? [];

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    DocumentId = reader.GetString(0),
                    ChunkIndex = reader.GetInt32(1),
                    Text = reader.GetString(2),
                    Metadata = metadata
                },
                Score = reader.GetDouble(4)
            });
        }

        return results;
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM rag_chunks WHERE document_id = $1", conn);
        cmd.Parameters.AddWithValue(documentId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose() => _dataSource.Dispose();
}
```

**Step 4: Create PgVectorBuilderExtensions.cs**

```csharp
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.PgVector;

public static class PgVectorBuilderExtensions
{
    public static RagBuilder UsePgVector(
        this RagBuilder builder,
        string connectionString,
        int vectorDimensions = 1536)
    {
        var store = new PgVectorStore(connectionString, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        return builder;
    }
}
```

Note: `PgVectorBuilderExtensions` needs `using Microsoft.Extensions.DependencyInjection;`.

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.PgVector.Tests --filter "PgVectorStore" -v minimal`
Expected: All 3 tests pass (requires Docker running for Testcontainers).

**Step 6: Commit**

```bash
git add src/Rag.NET.PgVector/ tests/Rag.NET.PgVector.Tests/
git commit -m "feat: add pgvector store implementation with integration tests"
```

---

### Task 11: PDF Parser Skeleton

**Files:**
- Create: `src/Rag.NET.Parsers.Pdf/PdfDocumentParser.cs`
- Create: `src/Rag.NET.Parsers.Pdf/PdfParserBuilderExtensions.cs`

**Step 1: Create PdfDocumentParser.cs**

```csharp
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using UglyToad.PdfPig;

namespace Rag.NET.Parsers.Pdf;

public sealed class PdfDocumentParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(stream);

        int sectionIndex = 0;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = page.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            yield return new DocumentSection
            {
                Text = text,
                DocumentId = metadata.DocumentId,
                SectionIndex = sectionIndex++,
                PageNumber = page.Number
            };
        }

        await Task.CompletedTask;
    }
}
```

**Step 2: Create PdfParserBuilderExtensions.cs**

```csharp
using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.Pdf;

public static class PdfParserBuilderExtensions
{
    public static RagBuilder AddPdfParser(this RagBuilder builder)
    {
        builder.AddParser<PdfDocumentParser>();
        return builder;
    }
}
```

**Step 3: Verify build**

Run: `dotnet build src/Rag.NET.Parsers.Pdf/Rag.NET.Parsers.Pdf.csproj`
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add src/Rag.NET.Parsers.Pdf/
git commit -m "feat: add PDF document parser"
```

---

### Task 12: Sample Application

**Files:**
- Create: `samples/Rag.NET.Sample/Program.cs`
- Create: `samples/Rag.NET.Sample/appsettings.json`

**Step 1: Create appsettings.json**

```json
{
  "ConnectionStrings": {
    "PostgreSQL": "Host=localhost;Database=ragnet;Username=postgres;Password=postgres"
  },
  "OpenAI": {
    "ApiKey": "your-api-key-here",
    "EmbeddingModel": "text-embedding-3-small",
    "ChatModel": "gpt-4o-mini"
  }
}
```

**Step 2: Create Program.cs**

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.PgVector;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSQL")!;
var apiKey = builder.Configuration["OpenAI:ApiKey"]!;
var embeddingModel = builder.Configuration["OpenAI:EmbeddingModel"]!;
var chatModel = builder.Configuration["OpenAI:ChatModel"]!;

builder.Services.AddEmbeddingGenerator(
    new OpenAI.Embeddings.EmbeddingClient(embeddingModel, apiKey).AsIEmbeddingGenerator());

builder.Services.AddChatClient(
    new OpenAI.Chat.ChatClient(chatModel, apiKey).AsIChatClient());

builder.Services.AddRagNet(rag => rag
    .UsePgVector(connectionString));

var app = builder.Build();

// Initialize pgvector schema
var vectorStore = app.Services.GetRequiredService<IVectorStore>() as PgVectorStore;
if (vectorStore is not null)
{
    await vectorStore.InitializeAsync();
}

app.MapPost("/ingest", async (IRagPipeline pipeline, HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file")
        ?? throw new BadHttpRequestException("No file provided.");

    var metadata = new Rag.NET.Models.DocumentMetadata
    {
        DocumentId = Guid.NewGuid().ToString(),
        FileName = file.FileName,
        ContentType = file.ContentType
    };

    using var stream = file.OpenReadStream();
    var result = await pipeline.IngestAsync(stream, metadata);

    return Results.Ok(result);
});

app.MapPost("/ask", async (IRagPipeline pipeline, AskRequest request) =>
{
    var response = await pipeline.AskAsync(request.Question);
    return Results.Ok(response);
});

app.MapPost("/search", async (IRagPipeline pipeline, SearchRequest request) =>
{
    var results = await pipeline.RetrieveAsync(request.Query);
    return Results.Ok(results);
});

app.Run();

record AskRequest(string Question);
record SearchRequest(string Query);
```

**Step 3: Verify build**

Run: `dotnet build samples/Rag.NET.Sample/Rag.NET.Sample.csproj`
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add samples/
git commit -m "feat: add sample minimal API application"
```

---

### Task 13: Final Verification

**Step 1: Build entire solution**

Run: `dotnet build Rag.NET.slnx`
Expected: Build succeeded, 0 errors, 0 warnings.

**Step 2: Run all unit tests**

Run: `dotnet test Rag.NET.slnx --filter "FullyQualifiedName~Rag.NET.Tests" -v minimal`
Expected: All tests pass.

**Step 3: Commit any remaining changes**

```bash
git add -A
git commit -m "chore: final cleanup"
```
