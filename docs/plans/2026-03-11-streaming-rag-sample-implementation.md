# Streaming RAG + Sample App Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add streaming response support (`AskStreamingAsync`) to the RAG pipeline and rewrite the sample app as an interactive console demo with Ollama/OpenAI provider switching.

**Architecture:** Add `RagStreamingUpdate` model and `AskStreamingAsync` to `IRagPipeline`. The method retrieves sources first, yields them, then streams LLM tokens. The sample app uses `Microsoft.Extensions.AI.OpenAI` for both OpenAI and Ollama (via OpenAI-compatible endpoint), PgVector with Testcontainers for storage.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI, Microsoft.Extensions.AI.OpenAI, Testcontainers.PostgreSql, PgVector, xunit.v3, NSubstitute

---

## Reference patterns

Before implementing, study these files:

- **Pipeline interface:** `src/Rag.NET/Abstractions/IRagPipeline.cs` — has `IngestAsync`, `RetrieveAsync`, `AskAsync`
- **Pipeline implementation:** `src/Rag.NET/Pipeline/RagPipeline.cs` — constructor takes `IChatClient?`, `AskAsync` builds prompt and calls `chatClient.GetResponseAsync`
- **Existing model:** `src/Rag.NET/Models/RagResponse.cs` — `Answer` (string) + `Sources` (IReadOnlyList<SearchResult>)
- **Pipeline tests:** `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs` — uses NSubstitute mocks, `ToAsyncEnumerable` helper
- **Current sample:** `samples/Rag.NET.Sample/Program.cs` — ASP.NET web app (will be replaced with console app)
- **DI registration:** `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` — `AddRagNet()` factory
- **Options:** `src/Rag.NET/Models/Options/RagOptions.cs` — `TopK`, `MinScore`, `SystemPrompt`, `Temperature`

**Key conventions:**
- .NET 10, `LangVersion preview`, `TreatWarningsAsErrors`, nullable enabled
- Library code: `ConfigureAwait(false)` on all awaits (MA0004)
- Samples: MA0004/MA0047/MA0048 suppressed via `samples/Directory.Build.props`
- Tests: MA0004 suppressed via `tests/Directory.Build.props`
- xunit.v3: `TestContext.Current.CancellationToken`, `Assert.SkipWhen`
- `[EnumeratorCancellation]` on CancellationToken in `async IAsyncEnumerable` methods
- `await Task.CompletedTask.ConfigureAwait(false)` at end of `async IAsyncEnumerable` methods that don't otherwise await
- M.E.AI streaming: `chatClient.GetStreamingResponseAsync(messages, options, ct)` returns `IAsyncEnumerable<ChatResponseUpdate>`, each has `.Text` property
- M.E.AI Ollama via OpenAI-compatible endpoint: `new OpenAIClientOptions { Endpoint = new Uri("http://localhost:11434/v1") }`

---

### Task 1: Add RagStreamingUpdate model

**Files:**
- Create: `src/Rag.NET/Models/RagStreamingUpdate.cs`

**Step 1: Create the model**

Create `src/Rag.NET/Models/RagStreamingUpdate.cs`:

```csharp
namespace Rag.NET.Models;

public sealed record RagStreamingUpdate
{
    public string? TextDelta { get; init; }
    public IReadOnlyList<SearchResult>? Sources { get; init; }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Rag.NET`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Rag.NET/Models/RagStreamingUpdate.cs
git commit -m "feat: add RagStreamingUpdate model for streaming responses"
```

---

### Task 2: Add AskStreamingAsync to IRagPipeline

**Files:**
- Modify: `src/Rag.NET/Abstractions/IRagPipeline.cs`

**Step 1: Add the method signature**

Add to `IRagPipeline` after the existing `AskAsync` method:

```csharp
IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
    string query,
    RagOptions? options = null,
    CancellationToken cancellationToken = default);
```

The full interface should now have 4 methods: `IngestAsync`, `RetrieveAsync`, `AskAsync`, `AskStreamingAsync`.

**Step 2: Verify build fails**

Run: `dotnet build src/Rag.NET`
Expected: Build FAILS — `RagPipeline` does not implement `AskStreamingAsync`

**Step 3: Commit**

```bash
git add src/Rag.NET/Abstractions/IRagPipeline.cs
git commit -m "feat: add AskStreamingAsync to IRagPipeline interface"
```

---

### Task 3: Implement AskStreamingAsync in RagPipeline

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`

**Step 1: Add the streaming implementation**

Add this method to `RagPipeline` after the existing `AskAsync` method:

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
    var retrievalOptions = new RetrievalOptions { TopK = opts.TopK, MinScore = opts.MinScore };
    var sources = await RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);

    yield return new RagStreamingUpdate { Sources = sources };

    var context = string.Join("\n\n---\n\n",
        sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}"));

    var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, systemPrompt),
        new(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"),
    };

    var chatOptions = new ChatOptions();
    if (opts.Temperature.HasValue)
    {
        chatOptions.Temperature = opts.Temperature.Value;
    }

    await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
    {
        if (update.Text is not null)
        {
            yield return new RagStreamingUpdate { TextDelta = update.Text };
        }
    }
}
```

**Important:** Add `using System.Runtime.CompilerServices;` to the top of the file if not already present (for `[EnumeratorCancellation]`).

**Step 2: Verify build succeeds**

Run: `dotnet build src/Rag.NET`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs
git commit -m "feat: implement AskStreamingAsync in RagPipeline"
```

---

### Task 4: Add streaming tests — Sources yielded first

**Files:**
- Modify: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Write the test**

Add to `RagPipelineTests` class. This test needs a `RagPipeline` with a chat client, so create a separate helper. Add these tests:

```csharp
[Fact]
public async Task AskStreamingAsync_WithoutChatClient_ThrowsInvalidOperation()
{
    var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(new List<SearchResult>());

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
    {
        await foreach (var _ in _sut.AskStreamingAsync("question", cancellationToken: TestContext.Current.CancellationToken))
        {
        }
    });
}

[Fact]
public async Task AskStreamingAsync_YieldsSourcesFirst_ThenTextDeltas()
{
    var chatClient = Substitute.For<IChatClient>();
    var sut = new RagPipeline(
        [_parser],
        _chunker,
        _vectorStore,
        _embedder,
        chatClient,
        new ChunkingOptions());

    var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
    var searchResult = new SearchResult
    {
        Chunk = new TextChunk { Text = "relevant context", DocumentId = "doc-1", ChunkIndex = 0 },
        Score = 0.9
    };

    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(new List<SearchResult> { searchResult });

    chatClient.GetStreamingResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(
            new ChatResponseUpdate { Text = "Hello" },
            new ChatResponseUpdate { Text = " World" }));

    var updates = new List<RagStreamingUpdate>();
    await foreach (var update in sut.AskStreamingAsync("test question", cancellationToken: TestContext.Current.CancellationToken))
    {
        updates.Add(update);
    }

    // First update has sources, no text
    Assert.NotNull(updates[0].Sources);
    Assert.Single(updates[0].Sources);
    Assert.Null(updates[0].TextDelta);

    // Subsequent updates have text, no sources
    Assert.Equal("Hello", updates[1].TextDelta);
    Assert.Null(updates[1].Sources);
    Assert.Equal(" World", updates[2].TextDelta);
    Assert.Null(updates[2].Sources);

    Assert.Equal(3, updates.Count);
}
```

**Note on `ChatResponseUpdate`:** This is from `Microsoft.Extensions.AI`. It has a parameterless constructor and a settable `Text` property. If the constructor or property doesn't work as shown, check the actual API — you may need `new ChatResponseUpdate { Text = "Hello" }` or use `ChatResponseUpdate.Create(role, "Hello")` or similar.

**Step 2: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RagPipelineTests"`
Expected: All tests pass (existing 3 + new 2 = 5 total)

**Step 3: Commit**

```bash
git add tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "test: add streaming pipeline tests"
```

---

### Task 5: Rewrite sample app as interactive console

**Files:**
- Modify: `samples/Rag.NET.Sample/Rag.NET.Sample.csproj`
- Rewrite: `samples/Rag.NET.Sample/Program.cs`
- Create: `samples/Rag.NET.Sample/documents/dotnet-overview.md` (sample document)
- Create: `samples/Rag.NET.Sample/documents/csharp-features.md` (sample document)

**Step 1: Update the project file**

Replace `samples/Rag.NET.Sample/Rag.NET.Sample.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET.PgVector\Rag.NET.PgVector.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET.Parsers.Pdf\Rag.NET.Parsers.Pdf.csproj" />
    <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
    <PackageReference Include="Testcontainers.PostgreSql" Version="4.*" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="documents\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

**Note:** Changed from `Sdk.Web` to plain `Sdk` with `OutputType Exe`. Added Testcontainers, Parsers.Pdf, and Microsoft.Extensions.Hosting. Added content copy for documents folder.

**Step 2: Create sample documents**

Create `samples/Rag.NET.Sample/documents/dotnet-overview.md`:

```markdown
# .NET Overview

.NET is a free, open-source development platform for building many kinds of applications. It supports multiple languages including C#, F#, and Visual Basic.

## Key Features

.NET provides automatic memory management through garbage collection. It includes a just-in-time compiler that converts IL code to native machine code at runtime.

## Cross-Platform

.NET runs on Windows, macOS, and Linux. Applications can be deployed as self-contained executables or framework-dependent.

## Performance

.NET is known for high performance, especially in web workloads. ASP.NET Core consistently ranks among the fastest web frameworks in TechEmpower benchmarks.
```

Create `samples/Rag.NET.Sample/documents/csharp-features.md`:

```markdown
# C# Language Features

C# is a modern, object-oriented programming language developed by Microsoft as part of the .NET platform.

## Pattern Matching

C# supports advanced pattern matching with switch expressions, property patterns, and relational patterns. This enables concise and readable code for complex conditional logic.

## Async/Await

C# has first-class support for asynchronous programming through async/await keywords. This makes it easy to write non-blocking code that scales well under load.

## Records

Records are reference types that provide value-based equality semantics. They are ideal for immutable data models and DTOs. Record structs provide similar functionality for value types.

## Nullable Reference Types

C# nullable reference types help prevent null reference exceptions at compile time. When enabled, the compiler tracks nullability and warns about potential null dereferences.
```

**Step 3: Rewrite Program.cs**

Replace `samples/Rag.NET.Sample/Program.cs` with:

```csharp
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Pdf;
using Rag.NET.PgVector;
using OpenAI;
using Testcontainers.PostgreSql;

// --- Start PostgreSQL container ---
Console.WriteLine("Starting PostgreSQL container...");
var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
await postgres.StartAsync();
var connectionString = postgres.GetConnectionString();
Console.WriteLine("PostgreSQL ready.");

try
{
    // --- Configure services ---
    var provider = Environment.GetEnvironmentVariable("RAG_PROVIDER") ?? "ollama";
    var services = new ServiceCollection();

    if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("Set OPENAI_API_KEY environment variable.");

        var chatModel = Environment.GetEnvironmentVariable("OPENAI_CHAT_MODEL") ?? "gpt-4o-mini";
        var embeddingModel = Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
        var vectorDimensions = 1536;

        services.AddChatClient(
            new OpenAI.Chat.ChatClient(chatModel, apiKey).AsIChatClient());
        services.AddEmbeddingGenerator(
            new OpenAI.Embeddings.EmbeddingClient(embeddingModel, apiKey).AsIEmbeddingGenerator());

        services.AddRagNet(rag => rag
            .UsePgVector(connectionString, vectorDimensions)
            .AddPdfParser());

        Console.WriteLine($"Using OpenAI (chat: {chatModel}, embeddings: {embeddingModel})");
    }
    else
    {
        var ollamaEndpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434/v1";
        var chatModel = Environment.GetEnvironmentVariable("OLLAMA_CHAT_MODEL") ?? "llama3.2";
        var embeddingModel = Environment.GetEnvironmentVariable("OLLAMA_EMBEDDING_MODEL") ?? "all-minilm";
        var vectorDimensions = 384;

        var ollamaOptions = new OpenAIClientOptions { Endpoint = new Uri(ollamaEndpoint) };
        var ollamaCredential = new ApiKeyCredential("ollama");

        services.AddChatClient(
            new OpenAI.Chat.ChatClient(chatModel, ollamaCredential, ollamaOptions).AsIChatClient());
        services.AddEmbeddingGenerator(
            new OpenAI.Embeddings.EmbeddingClient(embeddingModel, ollamaCredential, ollamaOptions).AsIEmbeddingGenerator());

        services.AddRagNet(rag => rag
            .UsePgVector(connectionString, vectorDimensions)
            .AddPdfParser());

        Console.WriteLine($"Using Ollama at {ollamaEndpoint} (chat: {chatModel}, embeddings: {embeddingModel})");
    }

    var serviceProvider = services.BuildServiceProvider();

    // --- Initialize vector store ---
    var vectorStore = serviceProvider.GetRequiredService<IVectorStore>() as PgVectorStore;
    if (vectorStore is not null)
    {
        await vectorStore.InitializeAsync();
    }

    // --- Ingest documents ---
    var pipeline = serviceProvider.GetRequiredService<IRagPipeline>();
    var documentsPath = Path.Combine(AppContext.BaseDirectory, "documents");

    if (Directory.Exists(documentsPath))
    {
        var files = Directory.GetFiles(documentsPath);
        Console.WriteLine($"\nIngesting {files.Length} documents...");

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var contentType = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".md" => "text/markdown",
                ".txt" => "text/plain",
                ".pdf" => "application/pdf",
                ".csv" => "text/csv",
                ".json" => "application/json",
                ".html" => "text/html",
                _ => "text/plain",
            };

            var metadata = new DocumentMetadata
            {
                DocumentId = fileName,
                FileName = fileName,
                ContentType = contentType,
            };

            using var stream = File.OpenRead(file);
            var result = await pipeline.IngestAsync(stream, metadata);
            Console.WriteLine($"  {fileName}: {result.ChunksStored} chunks stored");
        }
    }

    // --- Interactive Q&A loop ---
    Console.WriteLine("\nReady! Ask a question (or 'quit' to exit):\n");

    while (true)
    {
        Console.Write("> ");
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input) || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        await foreach (var update in pipeline.AskStreamingAsync(input))
        {
            if (update.Sources is { Count: > 0 })
            {
                Console.WriteLine($"\n[Found {update.Sources.Count} source(s)]");
            }

            if (update.TextDelta is not null)
            {
                Console.Write(update.TextDelta);
            }
        }

        Console.WriteLine("\n");
    }
}
finally
{
    Console.WriteLine("Stopping PostgreSQL container...");
    await postgres.DisposeAsync();
}
```

**Step 4: Verify build**

Run: `dotnet build samples/Rag.NET.Sample`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add samples/Rag.NET.Sample/Rag.NET.Sample.csproj samples/Rag.NET.Sample/Program.cs samples/Rag.NET.Sample/documents/
git commit -m "feat: rewrite sample as interactive console app with streaming"
```

---

### Task 6: Full build and test verification

**Step 1: Build entire solution**

Run: `dotnet build Rag.NET.slnx`
Expected: Build succeeded, 0 errors, 0 warnings

**Step 2: Run all unit tests**

Run: `dotnet test Rag.NET.slnx --filter "FullyQualifiedName~Parsers|FullyQualifiedName~Chunking|FullyQualifiedName~Pipeline"`
Expected: All tests pass

**Step 3: Commit if any solution file changes needed**

```bash
git add Rag.NET.slnx
git commit -m "chore: update solution file"
```
