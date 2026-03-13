# Token-Aware Splitting Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `TokenAwareChunkingStrategy` that splits text by token count rather than character count, preventing chunks from silently exceeding embedding model token limits on dense text like code and URLs.

**Architecture:** New class `TokenAwareChunkingStrategy` implementing `IChunkingStrategy`. Uses `Microsoft.ML.Tokenizers` to encode text to token IDs, slice by `MaxChunkSize` tokens with `Overlap` token overlap, then decode back to strings. `ChunkingOptions.MaxChunkSize` and `Overlap` are reused — they mean **tokens** when this strategy is active. Registered via a new `RagBuilder.UseTokenAwareChunking()` fluent method.

**Tech Stack:** `Microsoft.ML.Tokenizers` NuGet package — provides `TiktokenTokenizer.CreateForModel("gpt-4")` (uses `cl100k_base` encoding, compatible with all OpenAI models and most modern embedding models).

---

### Task 1: Add the NuGet Package

**Files:**
- Modify: `src/Rag.NET/Rag.NET.csproj`

**Step 1: Add the package reference**

Open `src/Rag.NET/Rag.NET.csproj` and add inside the existing `<ItemGroup>` that contains other `PackageReference` entries:

```xml
<PackageReference Include="Microsoft.ML.Tokenizers" Version="0.*" />
```

**Step 2: Restore**

```bash
dotnet restore src/Rag.NET/Rag.NET.csproj
```

Expected: restore completes with no errors.

**Step 3: Commit**

```bash
git add src/Rag.NET/Rag.NET.csproj
git commit -m "build: add Microsoft.ML.Tokenizers package to Rag.NET"
```

---

### Task 2: Implement TokenAwareChunkingStrategy with Tests

**Files:**
- Create: `src/Rag.NET/Chunking/TokenAwareChunkingStrategy.cs`
- Create: `tests/Rag.NET.Tests/Chunking/TokenAwareChunkingStrategyTests.cs`

**Context:** The `IChunkingStrategy` interface has one method:
```csharp
IAsyncEnumerable<TextChunk> ChunkAsync(DocumentSection section, ChunkingOptions options, CancellationToken cancellationToken = default);
```

`DocumentSection` has: `string Text`, `string DocumentId`, `int SectionIndex`.
`TextChunk` has: `string Text`, `string DocumentId`, `int ChunkIndex`, `int StartPosition`, `int EndPosition`.
`ChunkingOptions` has: `int MaxChunkSize` (default 512), `int Overlap` (default 50).

`TiktokenTokenizer.CreateForModel("gpt-4")` returns a tokenizer. `tokenizer.EncodeToIds(text)` returns `IReadOnlyList<int>` of token IDs. `tokenizer.Decode(ids)` decodes back to string.

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Chunking/TokenAwareChunkingStrategyTests.cs`:

```csharp
using Microsoft.ML.Tokenizers;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class TokenAwareChunkingStrategyTests
{
    private readonly TokenAwareChunkingStrategy _sut = new();

    private static DocumentSection CreateSection(string text) => new()
    {
        Text = text,
        DocumentId = "doc-1",
        SectionIndex = 0,
    };

    [Fact]
    public async Task ChunkAsync_EmptyText_ReturnsNoChunks()
    {
        var section = CreateSection("");
        var options = new ChunkingOptions { MaxChunkSize = 10, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_TextShorterThanMax_ReturnsSingleChunk()
    {
        var section = CreateSection("Hello world");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.Equal("doc-1", chunks[0].DocumentId);
        Assert.Equal(0, chunks[0].ChunkIndex);
    }

    [Fact]
    public async Task ChunkAsync_AllChunksWithinTokenLimit()
    {
        // Dense text that might exceed char-based limits
        var denseText = string.Join(" ", Enumerable.Repeat("https://example.com/very/long/url/path?query=value&another=param", 20));
        var section = CreateSection(denseText);
        var options = new ChunkingOptions { MaxChunkSize = 50, Overlap = 0 };
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            var tokenCount = tokenizer.CountTokens(chunk.Text);
            Assert.True(tokenCount <= options.MaxChunkSize,
                $"Chunk has {tokenCount} tokens, expected <= {options.MaxChunkSize}");
        }
    }

    [Fact]
    public async Task ChunkAsync_AssignsIncrementingChunkIndex()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 200));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(chunks.Count >= 2);
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkIndex);
        }
    }

    [Fact]
    public async Task ChunkAsync_PreservesDocumentId()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 100));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(chunks, c => Assert.Equal("doc-1", c.DocumentId));
    }

    [Fact]
    public async Task ChunkAsync_WithOverlap_ProducesMoreChunksThanWithout()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 100));
        var section = CreateSection(text);

        var withoutOverlap = await _sut.ChunkAsync(section, new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 }, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        var withOverlap = await _sut.ChunkAsync(section, new ChunkingOptions { MaxChunkSize = 20, Overlap = 5 }, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(withOverlap.Count >= withoutOverlap.Count);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests --no-build -v normal 2>&1 | grep -E "error|Error|FAIL"
```

Expected: build error — `TokenAwareChunkingStrategy` does not exist yet.

**Step 3: Create the implementation**

Create `src/Rag.NET/Chunking/TokenAwareChunkingStrategy.cs`:

```csharp
using System.Runtime.CompilerServices;
using Microsoft.ML.Tokenizers;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public sealed class TokenAwareChunkingStrategy : IChunkingStrategy
{
    private readonly Tokenizer _tokenizer;

    public TokenAwareChunkingStrategy(string modelName = "gpt-4")
    {
        _tokenizer = TiktokenTokenizer.CreateForModel(modelName);
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(section.Text))
        {
            yield break;
        }

        var tokenIds = _tokenizer.EncodeToIds(section.Text);
        int chunkIndex = 0;
        int position = 0;
        int step = Math.Max(1, options.MaxChunkSize - options.Overlap);

        while (position < tokenIds.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int end = Math.Min(position + options.MaxChunkSize, tokenIds.Count);
            var slice = tokenIds.Skip(position).Take(end - position).ToList();
            var chunkText = _tokenizer.Decode(slice);

            if (!string.IsNullOrWhiteSpace(chunkText))
            {
                yield return new TextChunk
                {
                    Text = chunkText.Trim(),
                    DocumentId = section.DocumentId,
                    ChunkIndex = chunkIndex++,
                    StartPosition = position,
                    EndPosition = end,
                };
            }

            position += step;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
```

**Step 4: Build and run the tests**

```bash
dotnet build tests/Rag.NET.Tests --no-restore
dotnet test tests/Rag.NET.Tests --no-build -v normal 2>&1 | grep -E "Passed|Failed|passed|failed"
```

Expected: all tests pass including existing chunking tests.

**Step 5: Commit**

```bash
git add src/Rag.NET/Chunking/TokenAwareChunkingStrategy.cs tests/Rag.NET.Tests/Chunking/TokenAwareChunkingStrategyTests.cs
git commit -m "feat: add TokenAwareChunkingStrategy using Microsoft.ML.Tokenizers"
```

---

### Task 3: Add UseTokenAwareChunking to RagBuilder

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`

**Context:** `RagBuilder` already has `UseChunkingStrategy<TStrategy>()` which calls `Services.AddSingleton<IChunkingStrategy, TStrategy>()`. Because `TokenAwareChunkingStrategy` takes a constructor parameter (`modelName`), we register via a factory lambda instead.

**Step 1: Write the test**

Add to `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`:

```csharp
[Fact]
public void UseTokenAwareChunking_RegistersTokenAwareStrategy()
{
    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
    services.AddRagNet(b => b.UseTokenAwareChunking());

    var provider = services.BuildServiceProvider();
    var strategy = provider.GetService<Rag.NET.Abstractions.IChunkingStrategy>();

    Assert.IsType<Rag.NET.Chunking.TokenAwareChunkingStrategy>(strategy);
}
```

Add `using Microsoft.Extensions.DependencyInjection;` at the top of `RagPipelineTests.cs` if not present.

**Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests --no-build --filter "UseTokenAwareChunking_RegistersTokenAwareStrategy" -v normal
```

Expected: FAIL — `UseTokenAwareChunking` method doesn't exist yet.

**Step 3: Add the method to RagBuilder**

In `src/Rag.NET/DependencyInjection/RagBuilder.cs`, add `using Rag.NET.Chunking;` at the top, then add this method inside the class:

```csharp
public RagBuilder UseTokenAwareChunking(string modelName = "gpt-4")
{
    Services.AddSingleton<IChunkingStrategy>(_ => new TokenAwareChunkingStrategy(modelName));
    return this;
}
```

**Step 4: Build and run all tests**

```bash
dotnet build tests/Rag.NET.Tests --no-restore
dotnet test tests/Rag.NET.Tests --no-build -v normal 2>&1 | grep -E "Passed|Failed|passed|failed"
```

Expected: all tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: add UseTokenAwareChunking fluent registration to RagBuilder"
```
