# Audio Transcription Parser (Whisper.net) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** New `Rag.NET.Parsers.Audio` package that transcribes audio files into `DocumentSection` records using Whisper.net — one section per Whisper segment, with timestamps in metadata.

**Architecture:** Follows the same structure as `Rag.NET.Parsers.Pdf` and `Rag.NET.Parsers.Word`: a separate project referencing `Rag.NET` core with a single `IDocumentParser` implementation. `AudioDocumentParser` copies the stream to a temp file (Whisper.net requires a file path), downloads the GGML model on first use via `WhisperGgmlDownloader`, and yields one `DocumentSection` per non-whitespace segment. Timestamps go in `Metadata["start_ms"]` and `Metadata["end_ms"]`.

**Tech Stack:** .NET 10, Whisper.net (NuGet: `Whisper.net` + `Whisper.net.Runtime`), xunit.v3, `IDocumentParser` interface from `Rag.NET` core.

---

### Task 1: Create the `Rag.NET.Parsers.Audio` project

**Files:**
- Create: `src/Rag.NET.Parsers.Audio/Rag.NET.Parsers.Audio.csproj`
- Create: `tests/Rag.NET.Parsers.Audio.Tests/Rag.NET.Parsers.Audio.Tests.csproj`

> This task scaffolds the projects. No code yet — just compilable empty shells.

**Step 1: Verify existing parser structure**

Look at `src/Rag.NET.Parsers.Pdf/Rag.NET.Parsers.Pdf.csproj` for the template:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.Parsers.Pdf</RootNamespace>
    <PackageId>Rag.NET.Parsers.Pdf</PackageId>
    <Description>PDF document parser for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="PdfPig" Version="0.*" />
  </ItemGroup>
</Project>
```

**Step 2: Create the source project file**

`src/Rag.NET.Parsers.Audio/Rag.NET.Parsers.Audio.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Parsers.Audio</RootNamespace>
    <PackageId>Rag.NET.Parsers.Audio</PackageId>
    <Description>Audio transcription parser for Rag.NET using Whisper.net</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Whisper.net" Version="1.*" />
    <PackageReference Include="Whisper.net.Runtime" Version="1.*" />
  </ItemGroup>

</Project>
```

**Step 3: Create the test project file**

`tests/Rag.NET.Parsers.Audio.Tests/Rag.NET.Parsers.Audio.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Parsers.Audio\Rag.NET.Parsers.Audio.csproj" />
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

**Step 4: Add both projects to the solution**

```bash
dotnet sln add src/Rag.NET.Parsers.Audio/Rag.NET.Parsers.Audio.csproj
dotnet sln add tests/Rag.NET.Parsers.Audio.Tests/Rag.NET.Parsers.Audio.Tests.csproj
```

**Step 5: Verify solution builds**

```
dotnet build src/Rag.NET.Parsers.Audio/Rag.NET.Parsers.Audio.csproj
```
Expected: Build succeeds (empty project, no errors).

**Step 6: Commit**

```bash
git add src/Rag.NET.Parsers.Audio/Rag.NET.Parsers.Audio.csproj \
        tests/Rag.NET.Parsers.Audio.Tests/Rag.NET.Parsers.Audio.Tests.csproj \
        Rag.NET.sln
git commit -m "feat: scaffold Rag.NET.Parsers.Audio project and test project"
```

---

### Task 2: Implement `AudioParserOptions`

**Files:**
- Create: `src/Rag.NET.Parsers.Audio/AudioParserOptions.cs`
- Test: `tests/Rag.NET.Parsers.Audio.Tests/AudioDocumentParserTests.cs` (scaffold `CanParse` tests only)

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Parsers.Audio.Tests/AudioDocumentParserTests.cs`:

```csharp
using Rag.NET.Parsers.Audio;
using Whisper.net.Ggml;
using Xunit;

namespace Rag.NET.Parsers.Audio.Tests;

public class AudioDocumentParserTests
{
    [Fact]
    public void AudioParserOptions_Defaults_AreCorrect()
    {
        var opts = new AudioParserOptions();
        Assert.Equal(GgmlType.Base, opts.ModelType);
        Assert.Null(opts.Language);
        Assert.Equal(Path.GetTempPath(), opts.ModelCacheDirectory);
    }

    [Theory]
    [InlineData("audio/wav",  true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("audio/flac", true)]
    [InlineData("audio/ogg",  true)]
    [InlineData("audio/mp4",  true)]
    [InlineData("application/pdf",  false)]
    [InlineData("text/plain",       false)]
    [InlineData("application/json", false)]
    public void CanParse_VariousContentTypes_ReturnsExpected(string contentType, bool expected)
    {
        var opts = new AudioParserOptions();
        var sut  = new AudioDocumentParser(opts);
        Assert.Equal(expected, sut.CanParse(contentType));
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Parsers.Audio.Tests --filter "AudioDocumentParserTests"
```
Expected: FAIL — `AudioParserOptions` and `AudioDocumentParser` types not found.

**Step 3: Create `AudioParserOptions.cs`**

```csharp
using Whisper.net.Ggml;

namespace Rag.NET.Parsers.Audio;

public sealed class AudioParserOptions
{
    public GgmlType ModelType         { get; init; } = GgmlType.Base;
    public string?  Language          { get; init; }
    public string   ModelCacheDirectory { get; init; } = Path.GetTempPath();
}
```

**Step 4: Create a stub `AudioDocumentParser.cs` to satisfy compilation**

(Full implementation comes in Task 3.)

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Audio;

public sealed class AudioDocumentParser(AudioParserOptions options) : IDocumentParser
{
    private static readonly HashSet<string> SupportedTypes =
    [
        "audio/wav",
        "audio/mpeg",
        "audio/flac",
        "audio/ogg",
        "audio/mp4",
    ];

    public bool CanParse(string contentType) => SupportedTypes.Contains(contentType);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Full implementation in Task 3.");
}
```

**Step 5: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Parsers.Audio.Tests --filter "AudioDocumentParserTests"
```
Expected: PASS (all `CanParse` tests + options defaults test).

**Step 6: Commit**

```bash
git add src/Rag.NET.Parsers.Audio/AudioParserOptions.cs \
        src/Rag.NET.Parsers.Audio/AudioDocumentParser.cs \
        tests/Rag.NET.Parsers.Audio.Tests/AudioDocumentParserTests.cs
git commit -m "feat: add AudioParserOptions and AudioDocumentParser stub with CanParse"
```

---

### Task 3: Implement `AudioDocumentParser.ParseAsync`

**Files:**
- Modify: `src/Rag.NET.Parsers.Audio/AudioDocumentParser.cs`
- Test: `tests/Rag.NET.Parsers.Audio.Tests/AudioDocumentParserTests.cs` (add segment → section tests)

> **Key constraint:** Whisper.net cannot be unit-tested without a native binary and real audio. The tests mock the segment boundary by subclassing or wrapping the parser. The approach here is to extract the segment-processing logic into an overridable method so tests can inject fake segments without actually running Whisper.

**Step 1: Write the failing tests**

Add to `AudioDocumentParserTests.cs`:

```csharp
using Rag.NET.Models;
using Whisper.net;
using Whisper.net.Ggml;

// Testable subclass that bypasses Whisper by overriding segment enumeration
file sealed class FakeAudioDocumentParser(AudioParserOptions options, IReadOnlyList<SegmentData> fakeSegments)
    : AudioDocumentParser(options)
{
    protected override IAsyncEnumerable<SegmentData> TranscribeAsync(
        string audioFilePath, CancellationToken ct) =>
        fakeSegments.ToAsyncEnumerable();
}

// ... in test class:

[Fact]
public async Task ParseAsync_Segments_YieldsOneSectionPerSegment()
{
    var ct = TestContext.Current.CancellationToken;
    var opts = new AudioParserOptions();
    var segments = new[]
    {
        new SegmentData { Text = "Hello world.", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2) },
        new SegmentData { Text = "  Second segment.  ", Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(4) },
    };
    var sut = new FakeAudioDocumentParser(opts, segments);
    var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.wav" };

    var sections = await sut.ParseAsync(Stream.Null, metadata, ct)
        .ToListAsync(ct);

    Assert.Equal(2, sections.Count);
    Assert.Equal("Hello world.", sections[0].Text);
    Assert.Equal("Second segment.", sections[1].Text);  // trimmed
    Assert.Equal("doc-1", sections[0].DocumentId);
    Assert.Equal(0, sections[0].SectionIndex);
    Assert.Equal(1, sections[1].SectionIndex);
    Assert.Equal("0", sections[0].Metadata!["start_ms"]);
    Assert.Equal("2000", sections[0].Metadata!["end_ms"]);
}

[Fact]
public async Task ParseAsync_WhitespaceOnlySegment_IsSkipped()
{
    var ct = TestContext.Current.CancellationToken;
    var opts = new AudioParserOptions();
    var segments = new[]
    {
        new SegmentData { Text = "   ", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) },
        new SegmentData { Text = "Real content.", Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3) },
    };
    var sut = new FakeAudioDocumentParser(opts, segments);
    var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.wav" };

    var sections = await sut.ParseAsync(Stream.Null, metadata, ct)
        .ToListAsync(ct);

    Assert.Single(sections);
    Assert.Equal("Real content.", sections[0].Text);
    Assert.Equal(0, sections[0].SectionIndex);  // skipped segment doesn't count
}

[Fact]
public async Task ParseAsync_EmptySegments_ReturnsNoSections()
{
    var ct = TestContext.Current.CancellationToken;
    var opts = new AudioParserOptions();
    var sut = new FakeAudioDocumentParser(opts, []);
    var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.wav" };

    var sections = await sut.ParseAsync(Stream.Null, metadata, ct)
        .ToListAsync(ct);

    Assert.Empty(sections);
}

[Fact]
public async Task ParseAsync_SetsTimestampMetadata()
{
    var ct = TestContext.Current.CancellationToken;
    var opts = new AudioParserOptions();
    var segments = new[]
    {
        new SegmentData
        {
            Text = "Timed segment.",
            Start = TimeSpan.FromMilliseconds(1500),
            End = TimeSpan.FromMilliseconds(3750)
        },
    };
    var sut = new FakeAudioDocumentParser(opts, segments);
    var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.wav" };

    var sections = await sut.ParseAsync(Stream.Null, metadata, ct)
        .ToListAsync(ct);

    Assert.Single(sections);
    Assert.Equal("1500", sections[0].Metadata!["start_ms"]);
    Assert.Equal("3750", sections[0].Metadata!["end_ms"]);
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Parsers.Audio.Tests --filter "AudioDocumentParserTests"
```
Expected: FAIL — `TranscribeAsync` protected method not present; `SegmentData` shape unclear.

**Step 3: Implement `AudioDocumentParser.ParseAsync` with `TranscribeAsync` hook**

Replace the stub in `AudioDocumentParser.cs` with the full implementation:

```csharp
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Whisper.net;
using Whisper.net.Ggml;

namespace Rag.NET.Parsers.Audio;

public class AudioDocumentParser(AudioParserOptions options) : IDocumentParser
{
    private static readonly HashSet<string> SupportedTypes =
    [
        "audio/wav",
        "audio/mpeg",
        "audio/flac",
        "audio/ogg",
        "audio/mp4",
    ];

    public bool CanParse(string contentType) => SupportedTypes.Contains(contentType);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");
        try
        {
            await using (var fs = File.Create(tempFile))
                await stream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);

            var sectionIndex = 0;
            await foreach (var segment in TranscribeAsync(tempFile, cancellationToken).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                yield return new DocumentSection
                {
                    Text         = text,
                    DocumentId   = metadata.DocumentId,
                    SectionIndex = sectionIndex++,
                    Metadata     = new Dictionary<string, string>
                    {
                        ["start_ms"] = ((long)segment.Start.TotalMilliseconds).ToString(),
                        ["end_ms"]   = ((long)segment.End.TotalMilliseconds).ToString(),
                    }
                };
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    protected virtual async IAsyncEnumerable<SegmentData> TranscribeAsync(
        string audioFilePath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var modelFileName = $"ggml-{options.ModelType.ToString().ToLowerInvariant()}.bin";
        var modelPath = Path.Combine(options.ModelCacheDirectory, modelFileName);

        if (!File.Exists(modelPath))
        {
            await using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(options.ModelType, cancellationToken: ct)
                .ConfigureAwait(false);
            await using var fileStream = File.Create(modelPath);
            await modelStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
        }

        using var factory = WhisperFactory.FromPath(modelPath);
        var processorBuilder = factory.CreateBuilder();

        if (options.Language is not null)
            processorBuilder = processorBuilder.WithLanguage(options.Language);

        using var processor = processorBuilder.Build();

        await foreach (var segment in processor.ProcessAsync(audioFilePath, ct).ConfigureAwait(false))
            yield return segment;
    }
}
```

> **Note on `sealed`:** The class is changed from `sealed` to non-sealed (no modifier, so `public class`) to allow the test subclass. The design doc said `sealed` but testability requires it be open for override.

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Parsers.Audio.Tests --filter "AudioDocumentParserTests"
```
Expected: PASS (all 8 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.Audio/AudioDocumentParser.cs \
        tests/Rag.NET.Parsers.Audio.Tests/AudioDocumentParserTests.cs
git commit -m "feat: implement AudioDocumentParser.ParseAsync with Whisper.net transcription"
```

---

### Task 4: Add `AudioParserBuilderExtensions` and verify DI wiring

**Files:**
- Create: `src/Rag.NET.Parsers.Audio/AudioParserBuilderExtensions.cs`
- Test: `tests/Rag.NET.Parsers.Audio.Tests/AudioDocumentParserTests.cs` (add DI wiring test)

**Step 1: Write the failing test**

Add to `AudioDocumentParserTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

[Fact]
public void AddAudioParser_RegistersParserAndOptions()
{
    var services = new ServiceCollection();
    // AddRagNet requires an IChatClient and IEmbeddingGenerator — use stubs
    services.AddSingleton(Substitute.For<IChatClient>());
    services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
    services.AddSingleton(Substitute.For<IVectorStore>());

    services.AddRagNet(rag => rag.AddAudioParser());

    var provider = services.BuildServiceProvider();
    var parsers = provider.GetServices<IDocumentParser>();

    Assert.Contains(parsers, p => p is AudioDocumentParser);
    var opts = provider.GetService<AudioParserOptions>();
    Assert.NotNull(opts);
}

[Fact]
public void AddAudioParser_WithConfigure_AppliesOptions()
{
    var services = new ServiceCollection();
    services.AddSingleton(Substitute.For<IChatClient>());
    services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
    services.AddSingleton(Substitute.For<IVectorStore>());

    services.AddRagNet(rag => rag.AddAudioParser(o =>
    {
        // Use 'with' on the options record — but AudioParserOptions uses init-only setters,
        // so the configure delegate receives a default instance; use object initializer pattern below
    }));

    // AudioParserOptions uses init-only properties — configure via 'new AudioParserOptions { ... }'
    // The extension supports factory-style override: see implementation note below
    var provider = services.BuildServiceProvider();
    Assert.NotNull(provider.GetService<AudioParserOptions>());
}
```

> **Note on `configure` delegate:** `AudioParserOptions` has `init`-only properties, so a standard `Action<AudioParserOptions>` configure pattern won't work with a plain `new()` + `configure(opts)`. The extension should accept `AudioParserOptions? options = null` directly, or a `Func<AudioParserOptions>` factory, rather than `Action<AudioParserOptions>`. See Step 3 for the actual API.

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Parsers.Audio.Tests --filter "AddAudioParser"
```
Expected: FAIL — `AddAudioParser` extension not found.

**Step 3: Create `AudioParserBuilderExtensions.cs`**

```csharp
using Rag.NET.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Parsers.Audio;

public static class AudioParserBuilderExtensions
{
    public static RagBuilder AddAudioParser(this RagBuilder builder,
        AudioParserOptions? options = null)
    {
        builder.Services.AddSingleton(options ?? new AudioParserOptions());
        builder.AddParser<AudioDocumentParser>();
        return builder;
    }
}
```

Usage examples:
```csharp
// Default options
rag.AddAudioParser();

// Custom options
rag.AddAudioParser(new AudioParserOptions { Language = "en", ModelType = GgmlType.Small });
```

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Parsers.Audio.Tests
```
Expected: PASS (all tests).

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.Audio/AudioParserBuilderExtensions.cs \
        tests/Rag.NET.Parsers.Audio.Tests/AudioDocumentParserTests.cs
git commit -m "feat: add AudioParserBuilderExtensions for DI registration"
```

---

### Task 5: Verify temp file cleanup and run full test suite

**Files:**
- Test: `tests/Rag.NET.Parsers.Audio.Tests/AudioDocumentParserTests.cs` (add cleanup tests)

**Step 1: Write the failing tests**

Add to `AudioDocumentParserTests.cs`:

```csharp
[Fact]
public async Task ParseAsync_TempFileDeletedAfterSuccess()
{
    var ct = TestContext.Current.CancellationToken;
    string? capturedTempPath = null;

    var segments = new[]
    {
        new SegmentData { Text = "Hello.", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) },
    };

    // Subclass that also captures the temp file path used during transcription
    var sut = new CapturingFakeParser(new AudioParserOptions(), segments, path => capturedTempPath = path);
    var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.wav" };

    using var stream = new MemoryStream([0x00, 0x01]);
    _ = await sut.ParseAsync(stream, metadata, ct).ToListAsync(ct);

    Assert.NotNull(capturedTempPath);
    Assert.False(File.Exists(capturedTempPath), "Temp file should be deleted after successful parse");
}

[Fact]
public async Task ParseAsync_TempFileDeletedAfterException()
{
    var ct = TestContext.Current.CancellationToken;
    string? capturedTempPath = null;

    var sut = new ThrowingFakeParser(new AudioParserOptions(), path => capturedTempPath = path);
    var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.wav" };

    using var stream = new MemoryStream([0x00]);
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => sut.ParseAsync(stream, metadata, ct).ToListAsync(ct).AsTask());

    Assert.NotNull(capturedTempPath);
    Assert.False(File.Exists(capturedTempPath), "Temp file should be deleted even when transcription throws");
}
```

Add these helpers at the file level (alongside `FakeAudioDocumentParser`):

```csharp
file sealed class CapturingFakeParser(
    AudioParserOptions options,
    IReadOnlyList<SegmentData> fakeSegments,
    Action<string> onPath) : AudioDocumentParser(options)
{
    protected override IAsyncEnumerable<SegmentData> TranscribeAsync(string audioFilePath, CancellationToken ct)
    {
        onPath(audioFilePath);
        return fakeSegments.ToAsyncEnumerable();
    }
}

file sealed class ThrowingFakeParser(AudioParserOptions options, Action<string> onPath) : AudioDocumentParser(options)
{
    protected override IAsyncEnumerable<SegmentData> TranscribeAsync(string audioFilePath, CancellationToken ct)
    {
        onPath(audioFilePath);
        throw new InvalidOperationException("Whisper exploded");
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Parsers.Audio.Tests --filter "TempFile"
```
Expected: FAIL — `CapturingFakeParser` / `ThrowingFakeParser` don't exist yet.

**Step 3: Add helpers to test file and run again**

These are `file`-scoped classes so just add them to the existing test file. Then:

```
dotnet test tests/Rag.NET.Parsers.Audio.Tests --filter "TempFile"
```
Expected: PASS — `finally` block in `ParseAsync` ensures deletion.

**Step 4: Run full solution test suite**

```
dotnet test
```
Expected: All tests pass (487+ across 12 projects, now plus new audio tests).

**Step 5: Commit**

```bash
git add tests/Rag.NET.Parsers.Audio.Tests/AudioDocumentParserTests.cs
git commit -m "test: add temp file cleanup tests for AudioDocumentParser"
```

---

## Summary

| Task | Key Change |
|------|-----------|
| 1 | Scaffold both projects; add to solution |
| 2 | `AudioParserOptions` + `AudioDocumentParser` stub with `CanParse` |
| 3 | Full `ParseAsync` with `TranscribeAsync` virtual hook for testability |
| 4 | `AudioParserBuilderExtensions` DI registration; `AddAudioParser(options?)` API |
| 5 | Temp file cleanup tests; full suite green |

## Notes for implementer

- **Whisper.net API:** Check current NuGet version — API surface may differ from design doc. Key types: `WhisperGgmlDownloader.GetGgmlModelAsync`, `WhisperFactory.FromPath`, `WhisperProcessorBuilder`, `SegmentData` (has `.Text`, `.Start`, `.End`). If `processor.ProcessAsync` doesn't return `IAsyncEnumerable<SegmentData>`, check for `ProcessAsync` that accepts a stream instead of a file path, or a synchronous `Process` method.
- **Model file naming:** `WhisperGgmlDownloader` downloads to a stream — check actual filename convention in Whisper.net docs. The pattern `ggml-{type}.bin` is an approximation; verify the actual cached filename to avoid re-downloading on each call.
- **`TranscribeAsync` signature:** `SegmentData` must be the actual type from `Whisper.net`. Adjust if the library uses a different name.
- **`file` keyword on test helpers:** Requires C# 11+. The solution targets .NET 10 so this is fine.
