# Pipeline Fortification Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add resiliency (Polly retry), observability (ActivitySource + ILogger with [LoggerMessage]), and idempotent ingestion (Overwrite flag) to `RagPipeline` without changing any vector store provider.

**Architecture:** Inline `ActivitySource` (static `"Rag.NET"` source) and `ILogger<RagPipeline>` into `RagPipeline`; add optional `ResiliencePipeline?` constructor param wired via `RagBuilder.ConfigureResilience`; new `IngestionOptions` class with `Overwrite` flag passed to `IngestAsync`.

**Tech Stack:** `Microsoft.Extensions.Resilience` (Polly v8), `System.Diagnostics.ActivitySource` (OTEL), `[LoggerMessage]` source generator, xUnit v3, NSubstitute.

---

### Task 1: Add `IngestionOptions` + update `IRagPipeline` + `RagPipeline` signature

**Files:**
- Create: `src/Rag.NET/Models/Options/IngestionOptions.cs`
- Modify: `src/Rag.NET/Abstractions/IRagPipeline.cs`
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs` (signature only, no behavior change)
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Write the failing test**

Add this test to `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs` inside `RagPipelineTests`:

```csharp
[Fact]
public async Task IngestAsync_WithNullOptions_SkipsDeleteAndStoresChunks()
{
    var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
    var section = new DocumentSection { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
    var chunk = new TextChunk { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
    var result = await _sut.IngestAsync(stream, metadata, options: null, TestContext.Current.CancellationToken);

    Assert.Equal(1, result.ChunksStored);
    await _vectorStore.DidNotReceive().DeleteByDocumentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "IngestAsync_WithNullOptions_SkipsDeleteAndStoresChunks" -v n
```

Expected: FAIL — `IngestAsync` has no `options` parameter, does not compile.

**Step 3: Create `IngestionOptions`**

Create `src/Rag.NET/Models/Options/IngestionOptions.cs`:

```csharp
namespace Rag.NET.Models.Options;

public sealed class IngestionOptions
{
    public bool Overwrite { get; set; }
}
```

**Step 4: Update `IRagPipeline.IngestAsync` signature**

In `src/Rag.NET/Abstractions/IRagPipeline.cs`, change `IngestAsync` to:

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

public interface IRagPipeline
{
    Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string documentId, CancellationToken cancellationToken = default);
}
```

**Step 5: Update `RagPipeline.IngestAsync` signature only**

In `src/Rag.NET/Pipeline/RagPipeline.cs`, change the `IngestAsync` method signature from:

```csharp
public async Task<IngestionResult> IngestAsync(
    Stream document,
    DocumentMetadata metadata,
    CancellationToken cancellationToken = default)
```

to:

```csharp
public async Task<IngestionResult> IngestAsync(
    Stream document,
    DocumentMetadata metadata,
    IngestionOptions? options = null,
    CancellationToken cancellationToken = default)
```

Also add `using Rag.NET.Models.Options;` at the top if not already present (it already is).

**Step 6: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v n
```

Expected: All tests pass (including the new one).

**Step 7: Commit**

```bash
git add src/Rag.NET/Models/Options/IngestionOptions.cs src/Rag.NET/Abstractions/IRagPipeline.cs src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: add IngestionOptions and update IngestAsync signature"
```

---

### Task 2: Telemetry infrastructure — `RagActivitySource` + `RagPipelineLog`

These are internal helpers with no isolated tests — they compile-verify when used by `RagPipeline` in later tasks.

**Files:**
- Create: `src/Rag.NET/Telemetry/RagActivitySource.cs`
- Create: `src/Rag.NET/Logging/RagPipelineLog.cs`

**Step 1: Create `RagActivitySource`**

Create `src/Rag.NET/Telemetry/RagActivitySource.cs`:

```csharp
using System.Diagnostics;
using System.Reflection;

namespace Rag.NET.Telemetry;

internal static class RagActivitySource
{
    internal static readonly ActivitySource Source = new(
        "Rag.NET",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

    internal const string IngestActivity = "ingest";
    internal const string RetrieveActivity = "retrieve";
    internal const string AskActivity = "ask";
}
```

**Step 2: Create `RagPipelineLog`**

Create `src/Rag.NET/Logging/RagPipelineLog.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Rag.NET.Logging;

internal static partial class RagPipelineLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Ingesting document {DocumentId} ({ContentType})")]
    internal static partial void IngestStarted(ILogger logger, string documentId, string? contentType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingested document {DocumentId}: {ChunksStored} chunk(s) stored")]
    internal static partial void IngestCompleted(ILogger logger, string documentId, int chunksStored);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to ingest document {DocumentId}")]
    internal static partial void IngestFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Retrieving chunks (TopK={TopK})")]
    internal static partial void RetrieveStarted(ILogger logger, int topK);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Retrieved {ResultCount} chunk(s)")]
    internal static partial void RetrieveCompleted(ILogger logger, int resultCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Asking with query (TopK={TopK})")]
    internal static partial void AskStarted(ILogger logger, int topK);
}
```

**Step 3: Verify it builds**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```

Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/Rag.NET/Telemetry/RagActivitySource.cs src/Rag.NET/Logging/RagPipelineLog.cs
git commit -m "feat: add RagActivitySource and RagPipelineLog telemetry helpers"
```

---

### Task 3: Add resilience package + `ConfigureResilience` + update `ServiceCollectionExtensions`

**Files:**
- Modify: `src/Rag.NET/Rag.NET.csproj`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs` (add constructor params + `_logger` field)

**Step 1: Add package reference**

In `src/Rag.NET/Rag.NET.csproj`, add inside `<ItemGroup>`:

```xml
<PackageReference Include="Microsoft.Extensions.Resilience" Version="9.*" />
```

**Step 2: Add `_logger` field and new constructor params to `RagPipeline`**

At the top of `src/Rag.NET/Pipeline/RagPipeline.cs`, add these usings:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Rag.NET.Logging;
using Rag.NET.Telemetry;
```

Change the class declaration from:

```csharp
public sealed class RagPipeline(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IChatClient? chatClient,
    ChunkingOptions chunkingOptions) : IRagPipeline
{
    private const string DefaultSystemPrompt = ...
```

to:

```csharp
public sealed class RagPipeline(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IChatClient? chatClient,
    ChunkingOptions chunkingOptions,
    ILogger<RagPipeline>? logger = null,
    ResiliencePipeline? resiliencePipeline = null) : IRagPipeline
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    private const string DefaultSystemPrompt = ...
```

**Step 3: Update `RagBuilder` — add `ConfigureResilience`**

Replace all of `src/Rag.NET/DependencyInjection/RagBuilder.cs` with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Resilience;
using Polly.Retry;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

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

    public RagBuilder ConfigureResilience(Action<ResiliencePipelineBuilder>? configure = null)
    {
        Services.AddResiliencePipeline("rag-net", builder =>
        {
            if (configure is not null)
            {
                configure(builder);
            }
            else
            {
                builder.AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                });
            }
        });

        return this;
    }
}
```

**Step 4: Update `ServiceCollectionExtensions` to resolve logger + resilience**

Replace all of `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` with:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Resilience;
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
            var logger = sp.GetService<ILogger<RagPipeline>>();
            var resilienceProvider = sp.GetService<ResiliencePipelineProvider<string>>();
            var resilience = resilienceProvider?.GetPipeline("rag-net");

            return new RagPipeline(parsers, chunker, store, embedder, chatClient, options, logger, resilience);
        });

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);

        return services;
    }
}
```

**Step 5: Build + run tests**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v n
```

Expected: Build succeeded, all tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/Rag.NET.csproj src/Rag.NET/DependencyInjection/RagBuilder.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs src/Rag.NET/Pipeline/RagPipeline.cs
git commit -m "feat: add resilience pipeline support and wire ILogger into RagPipeline"
```

---

### Task 4: Idempotent ingestion — `Overwrite` behavior

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Write the failing tests**

Add to `RagPipelineTests` in `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`:

```csharp
[Fact]
public async Task IngestAsync_WithOverwriteTrue_DeletesBeforeStoring()
{
    var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
    var section = new DocumentSection { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
    var chunk = new TextChunk { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
    await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = true }, TestContext.Current.CancellationToken);

    await _vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", Arg.Any<CancellationToken>());
    await _vectorStore.Received(1).StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task IngestAsync_WithOverwriteFalse_SkipsDelete()
{
    var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
    var section = new DocumentSection { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
    var chunk = new TextChunk { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
    await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = false }, TestContext.Current.CancellationToken);

    await _vectorStore.DidNotReceive().DeleteByDocumentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "IngestAsync_WithOverwrite" -v n
```

Expected: `IngestAsync_WithOverwriteTrue_DeletesBeforeStoring` fails — delete not called.

**Step 3: Implement in `IngestAsync`**

In `src/Rag.NET/Pipeline/RagPipeline.cs`, inside `IngestAsync`, immediately after resolving `parser` and before `var chunks = new List<TextChunk>();`, add:

```csharp
if (options?.Overwrite == true)
{
    await vectorStore.DeleteByDocumentIdAsync(metadata.DocumentId, cancellationToken).ConfigureAwait(false);
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v n
```

Expected: All tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: add idempotent ingestion via IngestionOptions.Overwrite"
```

---

### Task 5: Observability on `IngestAsync` + `RetrieveAsync`

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Create: `tests/Rag.NET.Tests/Pipeline/RagPipelineObservabilityTests.cs`

**Step 1: Write failing tests**

Create `tests/Rag.NET.Tests/Pipeline/RagPipelineObservabilityTests.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Pipeline;

public class RagPipelineObservabilityTests : IDisposable
{
    private readonly IDocumentParser _parser = Substitute.For<IDocumentParser>();
    private readonly IChunkingStrategy _chunker = Substitute.For<IChunkingStrategy>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly List<Activity> _stopped = [];
    private readonly ActivityListener _listener;

    public RagPipelineObservabilityTests()
    {
        _parser.CanParse(Arg.Any<string>()).Returns(true);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Rag.NET",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Add,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private RagPipeline CreateSut() =>
        new([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions());

    [Fact]
    public async Task IngestAsync_CreatesActivityWithTags()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
        await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        var activity = Assert.Single(_stopped, a => a.OperationName == "ingest");
        Assert.Equal("doc-1", activity.GetTagItem("document_id"));
        Assert.Equal("text/plain", activity.GetTagItem("content_type"));
        Assert.Equal("1", activity.GetTagItem("chunks_stored"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
    }

    [Fact]
    public async Task IngestAsync_SetsErrorStatusOnException()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns<IAsyncEnumerable<DocumentSection>>(_ => throw new InvalidOperationException("parse error"));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken));

        var activity = Assert.Single(_stopped, a => a.OperationName == "ingest");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    public async Task RetrieveAsync_CreatesActivityWithTags()
    {
        var sut = CreateSut();
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        await sut.RetrieveAsync("test query", new RetrievalOptions { TopK = 3 }, TestContext.Current.CancellationToken);

        var activity = Assert.Single(_stopped, a => a.OperationName == "retrieve");
        Assert.Equal("3", activity.GetTagItem("top_k"));
        Assert.Equal("0", activity.GetTagItem("results_count"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagPipelineObservabilityTests" -v n
```

Expected: All 3 tests fail — no activities are created.

**Step 3: Add observability to `IngestAsync`**

Replace the body of `IngestAsync` in `src/Rag.NET/Pipeline/RagPipeline.cs` with:

```csharp
public async Task<IngestionResult> IngestAsync(
    Stream document,
    DocumentMetadata metadata,
    IngestionOptions? options = null,
    CancellationToken cancellationToken = default)
{
    using var activity = RagActivitySource.Source.StartActivity(RagActivitySource.IngestActivity);
    activity?.SetTag("document_id", metadata.DocumentId);
    activity?.SetTag("content_type", metadata.ContentType);
    RagPipelineLog.IngestStarted(_logger, metadata.DocumentId, metadata.ContentType);

    try
    {
        var parser = parsers.FirstOrDefault(p => p.CanParse(metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{metadata.ContentType}'.");

        if (options?.Overwrite == true)
        {
            await vectorStore.DeleteByDocumentIdAsync(metadata.DocumentId, cancellationToken).ConfigureAwait(false);
        }

        var chunks = new List<TextChunk>();

        await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken).ConfigureAwait(false))
        {
            await foreach (var chunk in chunkingStrategy.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
            {
                chunks.Add(chunk);
            }
        }

        foreach (ref var chunk in CollectionsMarshal.AsSpan(chunks))
        {
            foreach (var tag in metadata.Tags)
            {
                chunk.Metadata.TryAdd(tag.Key, tag.Value);
            }
            chunk.Metadata.TryAdd("document_id", metadata.DocumentId);
            chunk.Metadata.TryAdd("file_name", metadata.FileName);
        }

        if (chunks.Count == 0)
        {
            activity?.SetTag("chunks_stored", "0");
            return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 0 };
        }

        var texts = chunks.Select(c => c.Text).ToList();
        var embeddings = await embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);

        var embeddedChunks = chunks
            .Zip(embeddings, (chunk, embedding) => new EmbeddedChunk
            {
                Chunk = chunk,
                Embedding = embedding.Vector,
            })
            .ToList();

        await vectorStore.StoreAsync(embeddedChunks, cancellationToken).ConfigureAwait(false);

        activity?.SetTag("chunks_stored", embeddedChunks.Count.ToString());
        RagPipelineLog.IngestCompleted(_logger, metadata.DocumentId, embeddedChunks.Count);

        return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = embeddedChunks.Count };
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        RagPipelineLog.IngestFailed(_logger, metadata.DocumentId, ex);
        throw;
    }
}
```

Also add `using System.Diagnostics;` at the top of `RagPipeline.cs`.

**Step 4: Add observability to `RetrieveAsync`**

Replace the body of `RetrieveAsync` with:

```csharp
public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
    string query,
    RetrievalOptions? options = null,
    CancellationToken cancellationToken = default)
{
    var opts = options ?? new RetrievalOptions();

    using var activity = RagActivitySource.Source.StartActivity(RagActivitySource.RetrieveActivity);
    activity?.SetTag("top_k", opts.TopK.ToString());
    activity?.SetTag("use_hybrid_search", opts.UseHybridSearch.ToString());
    RagPipelineLog.RetrieveStarted(_logger, opts.TopK);

    try
    {
        var queryEmbeddings = await embeddingGenerator.GenerateAsync(
            [query], cancellationToken: cancellationToken).ConfigureAwait(false);

        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
        };

        IReadOnlyList<SearchResult> results;

        if (opts.UseHybridSearch)
        {
            if (vectorStore is not IHybridSearchable hybrid)
            {
                throw new InvalidOperationException(
                    "The registered IVectorStore does not implement IHybridSearchable. " +
                    "Use a vector store that supports hybrid search, such as AzureAISearchVectorStore.");
            }

            results = await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            results = await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken).ConfigureAwait(false);
        }

        activity?.SetTag("results_count", results.Count.ToString());
        RagPipelineLog.RetrieveCompleted(_logger, results.Count);

        return results;
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        throw;
    }
}
```

**Step 5: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v n
```

Expected: All tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineObservabilityTests.cs
git commit -m "feat: add ActivitySource + ILogger observability to IngestAsync and RetrieveAsync"
```

---

### Task 6: Observability on `AskAsync` + `AskStreamingAsync`

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Modify: `tests/Rag.NET.Tests/Pipeline/RagPipelineObservabilityTests.cs`

**Step 1: Write failing test**

Add to `RagPipelineObservabilityTests`:

```csharp
[Fact]
public async Task AskAsync_CreatesActivityWithTopKTag()
{
    var chatClient = Substitute.For<IChatClient>();
    var sut = new RagPipeline(
        [_parser], _chunker, _vectorStore, _embedder, chatClient, new ChunkingOptions());

    var embedding = new Embedding<float>(new float[] { 0.1f });
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));
    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(new List<SearchResult>());
    chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));

    await sut.AskAsync("question", new RagOptions { TopK = 2 }, TestContext.Current.CancellationToken);

    var activity = Assert.Single(_stopped, a => a.OperationName == "ask");
    Assert.Equal("2", activity.GetTagItem("top_k"));
    Assert.Equal(ActivityStatusCode.Unset, activity.Status);
}
```

**Step 2: Run to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "AskAsync_CreatesActivityWithTopKTag" -v n
```

Expected: FAIL — no "ask" activity created.

**Step 3: Add observability to `AskAsync`**

Replace the body of `AskAsync` in `RagPipeline.cs` with:

```csharp
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

    using var activity = RagActivitySource.Source.StartActivity(RagActivitySource.AskActivity);
    activity?.SetTag("top_k", opts.TopK.ToString());
    RagPipelineLog.AskStarted(_logger, opts.TopK);

    try
    {
        var retrievalOptions = new RetrievalOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
        };
        var sources = await RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);

        var (messages, chatOptions) = BuildRagMessages(sources, query, opts);

        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);

        return new RagResponse
        {
            Answer = response.Text ?? string.Empty,
            Sources = sources,
        };
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        throw;
    }
}
```

**Step 4: Add observability to `AskStreamingAsync`**

Replace the body of `AskStreamingAsync` with:

```csharp
public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
    string query,
    RagOptions? options = null,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    if (chatClient is null)
    {
        throw new InvalidOperationException(
            "IChatClient is not registered. Register an IChatClient in DI to use AskStreamingAsync.");
    }

    var opts = options ?? new RagOptions();

    using var activity = RagActivitySource.Source.StartActivity(RagActivitySource.AskActivity);
    activity?.SetTag("top_k", opts.TopK.ToString());
    RagPipelineLog.AskStarted(_logger, opts.TopK);

    var retrievalOptions = new RetrievalOptions
    {
        TopK = opts.TopK,
        MinScore = opts.MinScore,
        MetadataFilter = opts.MetadataFilter,
        UseHybridSearch = opts.UseHybridSearch,
    };

    IReadOnlyList<SearchResult> sources;
    try
    {
        sources = await RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        throw;
    }

    yield return new RagStreamingUpdate { Sources = sources };

    var (messages, chatOptions) = BuildRagMessages(sources, query, opts);

    await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
    {
        if (update.Text is not null)
        {
            yield return new RagStreamingUpdate { TextDelta = update.Text };
        }
    }
}
```

Note: `try/catch` cannot wrap `yield return` statements. The activity is disposed via `using` when the iterator method exits naturally.

**Step 5: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v n
```

Expected: All tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineObservabilityTests.cs
git commit -m "feat: add observability to AskAsync and AskStreamingAsync"
```

---

### Task 7: Resilience — wrap embedding + vector store calls

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Create: `tests/Rag.NET.Tests/Pipeline/RagPipelineResilienceTests.cs`

Note: The test project uses `Polly` types. These are available transitively via `Rag.NET.csproj` → `Microsoft.Extensions.Resilience` → `Polly`. If the compiler can't resolve them, add `<PackageReference Include="Polly" Version="8.*" />` to `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`.

**Step 1: Write failing tests**

Create `tests/Rag.NET.Tests/Pipeline/RagPipelineResilienceTests.cs`:

```csharp
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.AI;
using NSubstitute;
using Polly;
using Polly.Retry;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Pipeline;

public class RagPipelineResilienceTests
{
    private readonly IDocumentParser _parser = Substitute.For<IDocumentParser>();
    private readonly IChunkingStrategy _chunker = Substitute.For<IChunkingStrategy>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    private static ResiliencePipeline BuildInstantRetryPipeline(int maxAttempts = 3) =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxAttempts,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
            })
            .Build();

    [Fact]
    public async Task IngestAsync_RetriesEmbeddingOnTransientFailure()
    {
        _parser.CanParse(Arg.Any<string>()).Returns(true);
        var sut = new RagPipeline(
            [_parser], _chunker, _vectorStore, _embedder, chatClient: null,
            new ChunkingOptions(), resiliencePipeline: BuildInstantRetryPipeline());

        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));

        var attempt = 0;
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                attempt++;
                if (attempt < 3)
                    throw new HttpRequestException("transient");
                return new GeneratedEmbeddings<Embedding<float>>([embedding]);
            });

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
        var result = await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ChunksStored);
        Assert.Equal(3, attempt);
    }

    [Fact]
    public async Task IngestAsync_ThrowsAfterAllRetriesExhausted()
    {
        _parser.CanParse(Arg.Any<string>()).Returns(true);
        var sut = new RagPipeline(
            [_parser], _chunker, _vectorStore, _embedder, chatClient: null,
            new ChunkingOptions(), resiliencePipeline: BuildInstantRetryPipeline(maxAttempts: 2));

        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<GeneratedEmbeddings<Embedding<float>>>(_ => throw new HttpRequestException("always fails"));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetrieveAsync_RetriesSearchOnTransientFailure()
    {
        _parser.CanParse(Arg.Any<string>()).Returns(true);
        var sut = new RagPipeline(
            [_parser], _chunker, _vectorStore, _embedder, chatClient: null,
            new ChunkingOptions(), resiliencePipeline: BuildInstantRetryPipeline());

        var embedding = new Embedding<float>(new float[] { 0.1f });
        var searchResult = new SearchResult
        {
            Chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 },
            Score = 0.9,
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        var searchAttempt = 0;
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                searchAttempt++;
                if (searchAttempt < 2)
                    throw new HttpRequestException("transient");
                return (IReadOnlyList<SearchResult>)[searchResult];
            });

        var results = await sut.RetrieveAsync("test query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(2, searchAttempt);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagPipelineResilienceTests" -v n
```

Expected: `IngestAsync_RetriesEmbeddingOnTransientFailure` and `RetrieveAsync_RetriesSearchOnTransientFailure` fail — no retries happen, exception thrown immediately.

**Step 3: Wrap embedding call in `IngestAsync`**

In `RagPipeline.IngestAsync`, replace:

```csharp
var embeddings = await embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);
```

with:

```csharp
var embeddings = resiliencePipeline is not null
    ? await resiliencePipeline.ExecuteAsync(
        async ct => await embeddingGenerator.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false),
        cancellationToken).ConfigureAwait(false)
    : await embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);
```

**Step 4: Wrap store call in `IngestAsync`**

Replace:

```csharp
await vectorStore.StoreAsync(embeddedChunks, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
if (resiliencePipeline is not null)
{
    await resiliencePipeline.ExecuteAsync(
        async ct => await vectorStore.StoreAsync(embeddedChunks, ct).ConfigureAwait(false),
        cancellationToken).ConfigureAwait(false);
}
else
{
    await vectorStore.StoreAsync(embeddedChunks, cancellationToken).ConfigureAwait(false);
}
```

**Step 5: Wrap embedding call in `RetrieveAsync`**

Replace:

```csharp
var queryEmbeddings = await embeddingGenerator.GenerateAsync(
    [query], cancellationToken: cancellationToken).ConfigureAwait(false);
```

with:

```csharp
var queryEmbeddings = resiliencePipeline is not null
    ? await resiliencePipeline.ExecuteAsync(
        async ct => await embeddingGenerator.GenerateAsync([query], cancellationToken: ct).ConfigureAwait(false),
        cancellationToken).ConfigureAwait(false)
    : await embeddingGenerator.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false);
```

**Step 6: Wrap search calls in `RetrieveAsync`**

Replace:

```csharp
results = await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
    .ConfigureAwait(false);
```

with:

```csharp
results = resiliencePipeline is not null
    ? await resiliencePipeline.ExecuteAsync(
        async ct => await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, ct).ConfigureAwait(false),
        cancellationToken).ConfigureAwait(false)
    : await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken).ConfigureAwait(false);
```

Replace:

```csharp
results = await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
results = resiliencePipeline is not null
    ? await resiliencePipeline.ExecuteAsync(
        async ct => await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct).ConfigureAwait(false),
        cancellationToken).ConfigureAwait(false)
    : await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken).ConfigureAwait(false);
```

**Step 7: Run all tests**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v n
```

Expected: All tests pass.

**Step 8: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineResilienceTests.cs
git commit -m "feat: wrap embedding and vector store calls in ResiliencePipeline for retry support"
```

---

### Final: run all unit + integration tests

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v n
```

Expected: All tests green.
