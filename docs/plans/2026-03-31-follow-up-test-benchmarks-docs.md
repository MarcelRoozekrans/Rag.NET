# Follow-up: Test Gaps, Benchmarks, Docs Update

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fill 9 missing DI test files, add 3 benchmark files, update docs/index.md and docs/getting-started.md with all new packages, and create answer-engines.md + query-techniques.md doc pages.

**Architecture:** Tests follow the existing UseXxx pattern in `tests/Rag.NET.Tests/DependencyInjection/`. Benchmarks follow `HydeBenchmarks.cs` (ServiceCollection + fake implementations). Docs update the Mermaid diagram + package table in index.md, then add new doc pages.

**Tech Stack:** xUnit, NSubstitute (for IChatClient tests), BenchmarkDotNet, Docusaurus Markdown.

---

### Task 1: UseTokenAwareChunking DI test

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseTokenAwareChunkingTests.cs`

**Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.TokenAware;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseTokenAwareChunkingTests
{
    [Fact]
    public void UseTokenAwareChunking_RegistersIChunkingStrategy()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseTokenAwareChunking())
            .BuildServiceProvider();

        Assert.IsType<TokenAwareChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseTokenAwareChunking_CustomModel_RegistersWithThatModel()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseTokenAwareChunking("gpt-3.5-turbo"))
            .BuildServiceProvider();

        var strategy = sp.GetRequiredService<IChunkingStrategy>();
        Assert.IsType<TokenAwareChunkingStrategy>(strategy);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseTokenAwareChunkingTests" -v minimal`
Expected: FAIL — type not found

**Step 3: Add missing `using Rag.NET.Chunking.TokenAware` and verify test project references `Rag.NET.Chunking.TokenAware`**

Check `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` — it should already reference `Rag.NET.Chunking.TokenAware` because `ChunkingBenchmarks.cs` already uses it. If not, add:
```xml
<ProjectReference Include="..\..\src\Rag.NET.Chunking.TokenAware\Rag.NET.Chunking.TokenAware.csproj" />
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseTokenAwareChunkingTests" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add tests/Rag.NET.Tests/DependencyInjection/UseTokenAwareChunkingTests.cs
git commit -m "test(di): add UseTokenAwareChunking DI registration tests"
```

---

### Task 2: UseMapReduceAnswerEngine DI test

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseMapReduceAnswerEngineTests.cs`

The test project already references `Rag.NET.AnswerEngines` (verify in csproj). `IChatClient` needs NSubstitute (already a test dep).

**Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.AnswerEngines;
using Rag.NET.AnswerGeneration;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseMapReduceAnswerEngineTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseMapReduceAnswerEngine_RegistersIAnswerEngine()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMapReduceAnswerEngine())
            .BuildServiceProvider();

        Assert.IsType<MapReduceAnswerEngine>(sp.GetRequiredService<IAnswerEngine>());
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseMapReduceAnswerEngineTests" -v minimal`
Expected: FAIL

**Step 3: Verify test project csproj has reference to `Rag.NET.AnswerEngines`**

If missing, add to `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`:
```xml
<ProjectReference Include="..\..\src\Rag.NET.AnswerEngines\Rag.NET.AnswerEngines.csproj" />
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseMapReduceAnswerEngineTests" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add tests/Rag.NET.Tests/DependencyInjection/UseMapReduceAnswerEngineTests.cs tests/Rag.NET.Tests/Rag.NET.Tests.csproj
git commit -m "test(di): add UseMapReduceAnswerEngine DI registration tests"
```

---

### Task 3: UseRefineAnswerEngine DI test

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseRefineAnswerEngineTests.cs`

**Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.AnswerEngines;
using Rag.NET.AnswerGeneration;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseRefineAnswerEngineTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseRefineAnswerEngine_RegistersIAnswerEngine()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseRefineAnswerEngine())
            .BuildServiceProvider();

        Assert.IsType<RefineAnswerEngine>(sp.GetRequiredService<IAnswerEngine>());
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseRefineAnswerEngineTests" -v minimal`
Expected: FAIL

**Step 3: Implement — no code changes needed, test project should already have the reference from Task 2**

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseRefineAnswerEngineTests" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add tests/Rag.NET.Tests/DependencyInjection/UseRefineAnswerEngineTests.cs
git commit -m "test(di): add UseRefineAnswerEngine DI registration tests"
```

---

### Task 4: UseDispatchingAnswerEngine DI test

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseDispatchingAnswerEngineTests.cs`

**Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.AnswerEngines;
using Rag.NET.AnswerGeneration;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseDispatchingAnswerEngineTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseDispatchingAnswerEngine_RegistersIAnswerEngine()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseDispatchingAnswerEngine())
            .BuildServiceProvider();

        Assert.IsType<DispatchingAnswerEngine>(sp.GetRequiredService<IAnswerEngine>());
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseDispatchingAnswerEngineTests" -v minimal`
Expected: FAIL

**Step 3: No new code needed — project reference from Task 2 covers it**

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseDispatchingAnswerEngineTests" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add tests/Rag.NET.Tests/DependencyInjection/UseDispatchingAnswerEngineTests.cs
git commit -m "test(di): add UseDispatchingAnswerEngine DI registration tests"
```

---

### Task 5: UseHyde DI test

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseHydeTests.cs`

**Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.HyDE;
using Rag.NET.QueryTechniques;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseHydeTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseHyde_RegistersIHypotheticalDocumentGenerator()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseHyde())
            .BuildServiceProvider();

        Assert.IsType<LlmHypotheticalDocumentGenerator>(
            sp.GetRequiredService<IHypotheticalDocumentGenerator>());
    }

    [Fact]
    public void UseHyde_DefaultOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseHyde())
            .BuildServiceProvider();

        var opts = sp.GetRequiredService<HydeOptions>();
        Assert.NotNull(opts);
    }

    [Fact]
    public void UseHyde_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseHyde(o => o.InstructionPrompt = "custom"))
            .BuildServiceProvider();

        Assert.Equal("custom", sp.GetRequiredService<HydeOptions>().InstructionPrompt);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseHydeTests" -v minimal`
Expected: FAIL

**Step 3: Verify test project csproj has reference to `Rag.NET.QueryTechniques`**

If missing, add to `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`:
```xml
<ProjectReference Include="..\..\src\Rag.NET.QueryTechniques\Rag.NET.QueryTechniques.csproj" />
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseHydeTests" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add tests/Rag.NET.Tests/DependencyInjection/UseHydeTests.cs tests/Rag.NET.Tests/Rag.NET.Tests.csproj
git commit -m "test(di): add UseHyde DI registration tests"
```

---

### Task 6: UseMultiQueryRetrieval DI test

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseMultiQueryRetrievalTests.cs`

**Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.MultiQuery;
using Rag.NET.QueryTechniques;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseMultiQueryRetrievalTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseMultiQueryRetrieval_RegistersIQueryExpander()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMultiQueryRetrieval())
            .BuildServiceProvider();

        Assert.IsType<LlmQueryExpander>(sp.GetRequiredService<IQueryExpander>());
    }

    [Fact]
    public void UseMultiQueryRetrieval_DefaultOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMultiQueryRetrieval())
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<MultiQueryOptions>());
    }

    [Fact]
    public void UseMultiQueryRetrieval_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMultiQueryRetrieval(o => o.VariantCount = 5))
            .BuildServiceProvider();

        Assert.Equal(5, sp.GetRequiredService<MultiQueryOptions>().VariantCount);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseMultiQueryRetrievalTests" -v minimal`
Expected: FAIL

**Step 3: No new csproj changes — QueryTechniques reference added in Task 5**

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseMultiQueryRetrievalTests" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add tests/Rag.NET.Tests/DependencyInjection/UseMultiQueryRetrievalTests.cs
git commit -m "test(di): add UseMultiQueryRetrieval DI registration tests"
```

---

### Task 7: PgVector builder extensions test

**Files:**
- Create: `tests/Rag.NET.VectorStores.PgVector.Tests/PgVectorBuilderExtensionsTests.cs`

These tests verify DI registration without connecting to a real database.

**Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.PgVector;
using Xunit;

namespace Rag.NET.VectorStores.PgVector.Tests;

public class PgVectorBuilderExtensionsTests
{
    [Fact]
    public void UsePgVector_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost;Database=test"))
            .BuildServiceProvider();

        Assert.IsType<PgVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UsePgVector_RegistersICollectionManageable()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost;Database=test"))
            .BuildServiceProvider();

        Assert.IsType<PgVectorStore>(sp.GetRequiredService<ICollectionManageable>());
    }

    [Fact]
    public void UsePgVector_BothInterfacesResolveSameInstance()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost;Database=test"))
            .BuildServiceProvider();

        var store = sp.GetRequiredService<IVectorStore>();
        var manageable = sp.GetRequiredService<ICollectionManageable>();
        Assert.Same(store, manageable);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.VectorStores.PgVector.Tests --filter "PgVectorBuilderExtensionsTests" -v minimal`
Expected: FAIL

**Step 3: No implementation needed — `UsePgVector` already exists**

Verify that `tests/Rag.NET.VectorStores.PgVector.Tests/Rag.NET.VectorStores.PgVector.Tests.csproj` references `Rag.NET` (for `AddRagNet`). If it only references `Rag.NET.VectorStores.PgVector`, add:
```xml
<ProjectReference Include="..\..\..\src\Rag.NET\Rag.NET.csproj" />
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.VectorStores.PgVector.Tests --filter "PgVectorBuilderExtensionsTests" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add tests/Rag.NET.VectorStores.PgVector.Tests/PgVectorBuilderExtensionsTests.cs
git commit -m "test(pgvector): add DI registration tests for UsePgVector"
```

---

### Task 8: Qdrant + AzureAISearch builder extensions tests

**Files:**
- Create: `tests/Rag.NET.VectorStores.Qdrant.Tests/QdrantBuilderExtensionsTests.cs`
- Create: `tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchBuilderExtensionsTests.cs`

First check what the Qdrant and AzureAISearch extension methods look like by reading:
- `src/Rag.NET.VectorStores.Qdrant/QdrantBuilderExtensions.cs`
- `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchBuilderExtensions.cs`

Then write tests mirroring the PgVector pattern: register via `AddRagNet(rag => rag.UseQdrant(...))`, verify `IVectorStore` resolves to the right type. Do not open a real connection — just verify DI resolution succeeds.

**Step 1: Read the extension files and write tests following PgVector pattern from Task 7**

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.VectorStores.Qdrant.Tests --filter "QdrantBuilderExtensionsTests" -v minimal`
Run: `dotnet test tests/Rag.NET.VectorStores.AzureAISearch.Tests --filter "AzureAISearchBuilderExtensionsTests" -v minimal`
Expected: FAIL

**Step 3: No implementation needed — extensions already exist**

**Step 4: Run tests to verify they pass**

Expected: PASS

**Step 5: Commit**

```bash
git add tests/Rag.NET.VectorStores.Qdrant.Tests/QdrantBuilderExtensionsTests.cs
git add tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchBuilderExtensionsTests.cs
git commit -m "test(vectorstores): add DI registration tests for UseQdrant and UseAzureAISearch"
```

---

### Task 9: Run full test suite — all green

**Step 1: Run all tests**

Run: `dotnet test --configuration Release -v minimal`
Expected: all pass

**Step 2: Fix any failures before proceeding**

**Step 3: Commit if any fixups were needed**

---

### Task 10: AnswerEngines benchmark

**Files:**
- Modify: `benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj` — add `Rag.NET.AnswerEngines` reference
- Create: `benchmarks/Rag.NET.Benchmarks/AnswerEngineBenchmarks.cs`

**Step 1: Add project reference to benchmarks csproj**

Add to `benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj`:
```xml
<ProjectReference Include="..\..\src\Rag.NET.AnswerEngines\Rag.NET.AnswerEngines.csproj" />
```

**Step 2: Write the benchmark**

```csharp
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU-only overhead of MapReduce and Refine answer engines.
/// The chat client is mocked (returns a fixed string) to isolate engine coordination cost.
/// Real-world cost is dominated by LLM calls.
/// </summary>
[MemoryDiagnoser]
public class AnswerEngineBenchmarks
{
    private IRagPipeline _pipeline = null!;
    private ServiceProvider _sp = null!;
    private byte[] _documentData = null!;

    private static readonly DocumentMetadata Metadata = new()
    {
        DocumentId = new DocumentId("bench-doc"),
        FileName    = "bench.txt",
        ContentType = "text/plain",
    };

    [GlobalSetup]
    public async Task Setup()
    {
        _documentData = Encoding.UTF8.GetBytes(GenerateText(20_000));

        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore, NoOpVectorStore>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new FakeEmbeddingGenerator(dimensions: 384));
        services.AddSingleton<IChatClient, FakeChatClient>();
        services.AddRagNet(rag => rag.UseMapReduceAnswerEngine());

        _sp = services.BuildServiceProvider();
        _pipeline = _sp.GetRequiredService<IRagPipeline>();

        using var stream = new MemoryStream(_documentData);
        _ = await _pipeline.IngestAsync(stream, Metadata);
    }

    [GlobalCleanup]
    public void Cleanup() => _sp?.Dispose();

    [Benchmark(Baseline = true)]
    public async Task<string> ChatAnswerEngine_Baseline()
    {
        var result = await _pipeline.AskAsync("What is this about?");
        return result.IsSuccess ? result.Value : string.Empty;
    }

    [Benchmark]
    public async Task<string> MapReduce()
    {
        var result = await _pipeline.AskAsync(
            "What is this about?",
            new RagOptions { SynthesisStrategy = SynthesisStrategy.MapReduce });
        return result.IsSuccess ? result.Value : string.Empty;
    }

    private static string GenerateText(int approximateLength)
    {
        const string paragraph =
            "The quick brown fox jumps over the lazy dog. " +
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
            "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.\n\n";

        var sb = new StringBuilder(approximateLength + paragraph.Length);
        while (sb.Length < approximateLength)
            sb.Append(paragraph);

        return sb.ToString();
    }

    private sealed class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fake");

        public Task<ChatCompletion> CompleteAsync(
            IList<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatCompletion(new ChatMessage(ChatRole.Assistant, "Benchmark answer.")));

        public IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(
            IList<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoOpVectorStore : IVectorStore
    {
        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>([]);

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeEmbeddingGenerator(int dimensions)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly float[] _fakeEmbedding = new float[dimensions];

        public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                embeddings.Add(new Embedding<float>(_fakeEmbedding));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
```

> **Note:** `SynthesisStrategy.MapReduce` — verify the enum name by checking `src/Rag.NET/Models/Options/RagOptions.cs`. If `UseMapReduceAnswerEngine` overrides `IAnswerEngine` directly (not via strategy dispatch), the `[Benchmark]` for MapReduce can just call `AskAsync` normally (the engine is already wired). In that case, benchmark both ChatAnswerEngine and MapReduceAnswerEngine by registering each in separate `ServiceProvider` instances in `[GlobalSetup]` — keep it simple.

**Step 3: Build benchmarks to verify no compile errors**

Run: `dotnet build benchmarks/Rag.NET.Benchmarks -v minimal`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj
git add benchmarks/Rag.NET.Benchmarks/AnswerEngineBenchmarks.cs
git commit -m "bench: add AnswerEngines benchmarks (MapReduce, Refine)"
```

---

### Task 11: SemanticChunking benchmark

**Files:**
- Modify: `benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj` — add `Rag.NET.Chunking.Semantic` reference
- Create: `benchmarks/Rag.NET.Benchmarks/SemanticChunkingBenchmarks.cs`

**Step 1: Add project reference**

Add to `benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj`:
```xml
<ProjectReference Include="..\..\src\Rag.NET.Chunking.Semantic\Rag.NET.Chunking.Semantic.csproj" />
```

**Step 2: Write the benchmark**

Follow `ChunkingBenchmarks.cs` pattern. `SemanticChunkingStrategy` requires `IEmbeddingGenerator` — use the same `FakeEmbeddingGenerator` from `HydeBenchmarks.cs` (copy into this file as a private nested class).

```csharp
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Chunking.Semantic;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Text;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks semantic chunking overhead with a fake embedding generator.
/// Real-world cost is dominated by embedding model calls.
/// </summary>
[MemoryDiagnoser]
public class SemanticChunkingBenchmarks
{
    private SemanticChunkingStrategy _strategy = null!;
    private DocumentSection _smallSection = null!;
    private DocumentSection _largeSection = null!;
    private readonly ChunkingOptions _options = new() { MaxChunkSize = 512, Overlap = 50 };

    [GlobalSetup]
    public void Setup()
    {
        _strategy = new SemanticChunkingStrategy(
            new FakeEmbeddingGenerator(dimensions: 384));
        _smallSection = CreateSection(GenerateText(500));
        _largeSection = CreateSection(GenerateText(10_000));
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Semantic_Small()
    {
        int count = 0;
        await foreach (var _ in _strategy.ChunkAsync(_smallSection, _options))
            count++;
        return count;
    }

    [Benchmark]
    public async Task<int> Semantic_Large()
    {
        int count = 0;
        await foreach (var _ in _strategy.ChunkAsync(_largeSection, _options))
            count++;
        return count;
    }

    private static DocumentSection CreateSection(string text) =>
        new(text, new DocumentMetadata
        {
            DocumentId = new DocumentId("bench"),
            FileName = "bench.txt",
            ContentType = "text/plain",
        });

    private static string GenerateText(int approximateLength)
    {
        const string paragraph =
            "The quick brown fox jumps over the lazy dog. " +
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit.\n\n";

        var sb = new StringBuilder(approximateLength + paragraph.Length);
        while (sb.Length < approximateLength)
            sb.Append(paragraph);

        return sb.ToString();
    }

    private sealed class FakeEmbeddingGenerator(int dimensions)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly float[] _fakeEmbedding = new float[dimensions];

        public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                embeddings.Add(new Embedding<float>(_fakeEmbedding));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
```

> **Note:** Check `SemanticChunkingStrategy`'s constructor signature before writing — it may take `IEmbeddingGenerator` directly, or via options. Read `src/Rag.NET.Chunking.Semantic/SemanticChunkingStrategy.cs` first.

**Step 3: Build benchmarks**

Run: `dotnet build benchmarks/Rag.NET.Benchmarks -v minimal`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj
git add benchmarks/Rag.NET.Benchmarks/SemanticChunkingBenchmarks.cs
git commit -m "bench: add SemanticChunking benchmarks"
```

---

### Task 12: Memory benchmark

**Files:**
- Modify: `benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj` — add `Rag.NET.Memory` reference
- Create: `benchmarks/Rag.NET.Benchmarks/MemoryBenchmarks.cs`

**Step 1: Add project reference**

Add to `benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj`:
```xml
<ProjectReference Include="..\..\src\Rag.NET.Memory\Rag.NET.Memory.csproj" />
```

**Step 2: Write the benchmark**

Follow `HydeBenchmarks.cs` pipeline pattern. Register `PersistentConversationMemory` using in-memory SQLite (`:memory:` connection string if the implementation supports it, or set up via `UsePersistentMemory`).

```csharp
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks persistent memory decorator overhead using an in-memory SQLite database.
/// </summary>
[MemoryDiagnoser]
public class MemoryBenchmarks
{
    private IRagPipeline _pipeline = null!;
    private ServiceProvider _sp = null!;
    private byte[] _documentData = null!;

    private static readonly DocumentMetadata Metadata = new()
    {
        DocumentId = new DocumentId("bench-doc"),
        FileName    = "bench.txt",
        ContentType = "text/plain",
    };

    [GlobalSetup]
    public async Task Setup()
    {
        _documentData = Encoding.UTF8.GetBytes(GenerateText(10_000));

        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore, NoOpVectorStore>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new FakeEmbeddingGenerator(dimensions: 384));
        services.AddSingleton<IChatClient, FakeChatClient>();
        services.AddRagNet(rag =>
            rag.UseConversationMemory(configure: mem =>
                mem.UsePersistentMemory(new PersistentMemoryOptions { DatabasePath = ":memory:" })));

        _sp = services.BuildServiceProvider();
        _pipeline = _sp.GetRequiredService<IRagPipeline>();

        using var stream = new MemoryStream(_documentData);
        _ = await _pipeline.IngestAsync(stream, Metadata);
    }

    [GlobalCleanup]
    public void Cleanup() => _sp?.Dispose();

    [Benchmark(Baseline = true)]
    public async Task<string> Ask_WithMemory()
    {
        var result = await _pipeline.AskAsync("What is this about?");
        return result.IsSuccess ? result.Value : string.Empty;
    }

    private static string GenerateText(int approximateLength)
    {
        const string paragraph =
            "The quick brown fox jumps over the lazy dog. " +
            "Lorem ipsum dolor sit amet.\n\n";

        var sb = new StringBuilder(approximateLength + paragraph.Length);
        while (sb.Length < approximateLength)
            sb.Append(paragraph);

        return sb.ToString();
    }

    private sealed class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fake");

        public Task<ChatCompletion> CompleteAsync(
            IList<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatCompletion(new ChatMessage(ChatRole.Assistant, "Benchmark answer.")));

        public IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(
            IList<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoOpVectorStore : IVectorStore
    {
        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>([]);

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeEmbeddingGenerator(int dimensions)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly float[] _fakeEmbedding = new float[dimensions];

        public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                embeddings.Add(new Embedding<float>(_fakeEmbedding));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
```

> **Note:** Check `PersistentMemoryOptions` for the SQLite path property name. Read `src/Rag.NET.Memory/PersistentMemoryOptions.cs` first. If the property is named differently (e.g. `ConnectionString`), adjust the benchmark accordingly.

**Step 3: Build benchmarks**

Run: `dotnet build benchmarks/Rag.NET.Benchmarks -v minimal`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj
git add benchmarks/Rag.NET.Benchmarks/MemoryBenchmarks.cs
git commit -m "bench: add Memory benchmarks (persistent memory decorator)"
```

---

### Task 13: Update docs/index.md

**Files:**
- Modify: `docs/index.md`

**Step 1: Add `Rag.NET.Abstractions` as top node to the Mermaid diagram**

Current diagram starts with `CORE["Rag.NET..."]`. Add `ABSTRACTIONS` above it and draw dependency arrows. Then add the 7 new packages as children of `ABSTRACTIONS`:

```
ABSTRACTIONS["Rag.NET.Abstractions<br>Interfaces · Models · Options · IRagBuilder"] --> CORE
ABSTRACTIONS --> CHUNKING["Rag.NET.Chunking<br>HierarchicalMerger · CodeChunking"]
ABSTRACTIONS --> CHUNKING_SEM["Rag.NET.Chunking.Semantic<br>Semantic chunking"]
ABSTRACTIONS --> CHUNKING_TOK["Rag.NET.Chunking.TokenAware<br>Token-count chunking"]
ABSTRACTIONS --> AE["Rag.NET.AnswerEngines<br>MapReduce · Refine · Dispatching"]
ABSTRACTIONS --> QT["Rag.NET.QueryTechniques<br>HyDE · MultiQuery"]
ABSTRACTIONS --> MEM["Rag.NET.Memory<br>Persistent cross-session memory"]
```

Keep the existing CORE → vector stores / parsers / data providers edges.

**Step 2: Add style lines for new nodes**

```
style ABSTRACTIONS fill:#fff3cd,stroke:#f0ad4e
style CHUNKING fill:#e8f4fd,stroke:#4a90d9
style CHUNKING_SEM fill:#e8f4fd,stroke:#4a90d9
style CHUNKING_TOK fill:#e8f4fd,stroke:#4a90d9
style AE fill:#e8f4fd,stroke:#4a90d9
style QT fill:#e8f4fd,stroke:#4a90d9
style MEM fill:#e8f4fd,stroke:#4a90d9
```

**Step 3: Add new rows to the package table**

After `| \`Rag.NET\` | Core pipeline... |`, add:

```
| `Rag.NET.Abstractions` | All 20+ interfaces, models, and options — no implementations, no heavy dependencies |
| `Rag.NET.Chunking` | `HierarchicalMergerChunkingStrategy`, `CodeChunkingStrategy` |
| `Rag.NET.Chunking.Semantic` | `SemanticChunkingStrategy` — splits at semantic boundaries using embeddings |
| `Rag.NET.Chunking.TokenAware` | `TokenAwareChunkingStrategy` — splits by token count rather than characters |
| `Rag.NET.AnswerEngines` | `MapReduceAnswerEngine`, `RefineAnswerEngine`, `DispatchingAnswerEngine` |
| `Rag.NET.QueryTechniques` | `LlmHypotheticalDocumentGenerator` (HyDE), `LlmQueryExpander` (MultiQuery) |
| `Rag.NET.Memory` | `PersistentConversationMemory` — SQLite-backed cross-session memory |
```

**Step 4: Update the Pages table to reference the two new doc pages**

Add rows:
```
| [Answer Engines](answer-engines.md) | MapReduce, Refine, and Dispatching answer engine strategies |
| [Query Techniques](query-techniques.md) | HyDE and Multi-Query retrieval expansion |
```

**Step 5: Commit**

```bash
git add docs/index.md
git commit -m "docs: update index.md with Abstractions + new extension packages"
```

---

### Task 14: Create docs/answer-engines.md

**Files:**
- Create: `docs/answer-engines.md`

**Step 1: Write the doc page**

Structure: Overview → ChatAnswerEngine (default, stays in core) → MapReduceAnswerEngine → RefineAnswerEngine → DispatchingAnswerEngine → When to use which → Registration examples.

```markdown
---
id: answer-engines
title: Answer Engines
sidebar_label: Answer Engines
sidebar_position: 6
---

# Answer Engines

Rag.NET ships with four answer engines. All implement `IAnswerEngine` and produce a string answer from the query and the retrieved source chunks.

## ChatAnswerEngine (default)

The default engine. Included in `Rag.NET` core, registered automatically when you call `AddRagNet()`. Builds a single prompt from all source chunks and sends one LLM call.

**Best for:** Queries with ≤ 10 source chunks and typical question-answering.

## MapReduceAnswerEngine

Included in `Rag.NET.AnswerEngines`.

Runs one LLM call per source chunk in parallel (map). Filters "not found" responses. Then combines surviving partial answers in a single reduce call.

**Best for:** Large document sets where each chunk may individually contain part of the answer. More LLM calls than Chat, but scales with number of chunks.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseMapReduceAnswerEngine());
```

## RefineAnswerEngine

Included in `Rag.NET.AnswerEngines`.

Generates an initial answer from the first source chunk, then iteratively refines it with each subsequent chunk. Sequential — not parallelised.

**Best for:** When answer coherence matters more than throughput, or when chunks must be incorporated in order.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseRefineAnswerEngine());
```

## DispatchingAnswerEngine

Included in `Rag.NET.AnswerEngines`.

Routes to MapReduce, Refine, or Chat at call time based on `RagOptions.SynthesisStrategy`. Allows runtime switching without re-registration.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseDispatchingAnswerEngine());
```

**Runtime selection:**
```csharp
var result = await pipeline.AskAsync(query, new RagOptions
{
    SynthesisStrategy = SynthesisStrategy.MapReduce
});
```

## Comparison

| Engine | LLM calls | Parallelism | Best for |
|--------|-----------|-------------|----------|
| Chat | 1 | — | Default, small source sets |
| MapReduce | N + 1 | Yes (map phase) | Large doc sets |
| Refine | N | No | Order-sensitive synthesis |
| Dispatching | Varies | Depends on strategy | Mixed workloads |
```

**Step 2: Commit**

```bash
git add docs/answer-engines.md
git commit -m "docs: add answer-engines.md"
```

---

### Task 15: Create docs/query-techniques.md

**Files:**
- Create: `docs/query-techniques.md`

**Step 1: Write the doc page**

Structure: Overview → HyDE → MultiQuery → When to combine → Registration examples.

```markdown
---
id: query-techniques
title: Query Techniques
sidebar_label: Query Techniques
sidebar_position: 7
---

# Query Techniques

`Rag.NET.QueryTechniques` provides two retrieval-expansion techniques that improve recall by transforming queries before embedding them.

## HyDE — Hypothetical Document Embedding

Generates a hypothetical document that _would_ answer the query using the LLM, then embeds that document instead of the raw query. This bridges the vocabulary gap between a short question and a long document passage.

**When to use:** Short queries against long technical documents. Expect +10-30% recall improvement in asymmetric retrieval.

**Cost:** One extra LLM call per retrieval. Dominated by LLM latency (~50-500 ms), not pipeline overhead.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseHyde());
```

**Per-call opt-out:**
```csharp
var result = await pipeline.RetrieveAsync(query, new RetrievalOptions { UseHyde = false });
```

**Options:**
```csharp
services.AddRagNet(rag => rag.UseHyde(o =>
{
    o.InstructionPrompt = "Generate a passage from a technical manual that answers: ";
}));
```

## MultiQuery — LLM Query Expander

Expands the query into `VariantCount` alternative phrasings using the LLM, fans out to the vector store in parallel for each variant, then merges and deduplicates results before returning the top-K.

**When to use:** Queries with ambiguous wording, or when users phrase things differently than the documents do.

**Cost:** `VariantCount` extra LLM calls + `VariantCount` extra vector store lookups per retrieval.

**Registration:**
```csharp
services.AddRagNet(rag => rag.UseMultiQueryRetrieval());
```

**Per-call opt-out:**
```csharp
var result = await pipeline.RetrieveAsync(query, new RetrievalOptions { UseMultiQuery = false });
```

**Options:**
```csharp
services.AddRagNet(rag => rag.UseMultiQueryRetrieval(o => o.VariantCount = 5));
```

## Using Both Together

HyDE and MultiQuery can be combined. MultiQuery first expands the query variants, then HyDE applies to each variant. Order matters: MultiQuery runs first, HyDE runs per expanded query.

```csharp
services.AddRagNet(rag => rag
    .UseMultiQueryRetrieval()
    .UseHyde());
```
```

**Step 2: Commit**

```bash
git add docs/query-techniques.md
git commit -m "docs: add query-techniques.md"
```

---

### Task 16: Update docs/getting-started.md

**Files:**
- Modify: `docs/getting-started.md`

**Step 1: Read the current getting-started.md to understand its structure**

Run: Read `docs/getting-started.md` to see what's there.

**Step 2: Add "Optional extensions" section**

After the basic setup section, add a section showing how to add extension packages:

```markdown
## Optional extension packages

The core `Rag.NET` package provides `RecursiveChunkingStrategy` and `ChatAnswerEngine` out of the box.
Install additional packages for more advanced capabilities:

### Semantic chunking
```bash
dotnet add package Rag.NET.Chunking.Semantic
```
```csharp
services.AddRagNet(rag => rag
    .UseSemanticChunking());
```

### HyDE query expansion
```bash
dotnet add package Rag.NET.QueryTechniques
```
```csharp
services.AddRagNet(rag => rag
    .UseHyde());
```

### MapReduce answer engine
```bash
dotnet add package Rag.NET.AnswerEngines
```
```csharp
services.AddRagNet(rag => rag
    .UseMapReduceAnswerEngine());
```

### Persistent cross-session memory
```bash
dotnet add package Rag.NET.Memory
```
```csharp
services.AddRagNet(rag => rag
    .UseConversationMemory(configure: mem => mem.UsePersistentMemory()));
```
```

**Step 3: Commit**

```bash
git add docs/getting-started.md
git commit -m "docs: add optional extension packages section to getting-started.md"
```

---

### Task 17: Final build + test run

**Step 1: Run full solution build**

Run: `dotnet build --configuration Release -v minimal`
Expected: Build succeeded, 0 errors

**Step 2: Run full test suite**

Run: `dotnet test --configuration Release -v minimal`
Expected: all pass

**Step 3: Run benchmark build (not execution)**

Run: `dotnet build benchmarks/Rag.NET.Benchmarks --configuration Release -v minimal`
Expected: Build succeeded

**Step 4: Commit any final fixups**
