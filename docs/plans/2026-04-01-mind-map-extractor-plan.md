# Mind-Map Extractor Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `MindMapExtractor` to `Rag.NET.GraphRag` that builds a hierarchical concept tree from document text via one LLM call, persists nodes+edges in `IGraphStore`, and optionally runs automatically at ingestion time.

**Architecture:** `MindMapExtractor` is a standalone service (injectable directly). `MindMapExtractionBehavior` is a thin `IIngestionBehavior` wrapper gated by `MindMapOptions.ExtractAtIngestion`. Nodes are `GraphEntity` with `Type = "mind_map_node"`; parent→child edges are `GraphRelationship` with `Description = "has_subtopic"`. No new query API — callers use `GetFullGraphAsync()` and filter. DI registered via `UseMindMapExtraction()` on `RagBuilder`.

**Tech Stack:** xUnit v3, NSubstitute, `Microsoft.Extensions.AI.Abstractions` (`IChatClient`), `System.Text.Json`, `Rag.NET.Graph` (`IGraphStore`, `SqliteGraphStore`, `GraphEntity`, `GraphRelationship`).

---

### Task 1: `MindMapNode` record and `MindMapOptions`

**Files:**
- Create: `src/Rag.NET.GraphRag/MindMapNode.cs`
- Create: `src/Rag.NET.GraphRag/MindMapOptions.cs`

**Step 1: Write the failing test**

Create `tests/Rag.NET.GraphRag.Tests/MindMapOptionsTests.cs`:

```csharp
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class MindMapOptionsTests
{
    [Fact]
    public void DefaultOptions_ExtractAtIngestionIsFalse()
    {
        var options = new MindMapOptions();
        Assert.False(options.ExtractAtIngestion);
    }

    [Fact]
    public void DefaultOptions_MaxDepthIsThree()
    {
        var options = new MindMapOptions();
        Assert.Equal(3, options.MaxDepth);
    }

    [Fact]
    public void DefaultOptions_PromptContainsDepthPlaceholder()
    {
        var options = new MindMapOptions();
        Assert.Contains("{depth}", options.Prompt);
    }

    [Fact]
    public void DefaultOptions_PromptContainsTextPlaceholder()
    {
        var options = new MindMapOptions();
        Assert.Contains("{text}", options.Prompt);
    }

    [Fact]
    public void MindMapNode_ChildrenAreEmpty_ByDefault()
    {
        var node = new MindMapNode("Root", "Summary", []);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void MindMapNode_ChildrenAreAccessible()
    {
        var child = new MindMapNode("Child", "Child summary", []);
        var root = new MindMapNode("Root", "Root summary", [child]);
        Assert.Single(root.Children);
        Assert.Equal("Child", root.Children[0].Title);
    }
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "MindMapOptionsTests" -v minimal
```

Expected: compile error — `MindMapOptions` and `MindMapNode` not defined.

**Step 3: Implement**

Create `src/Rag.NET.GraphRag/MindMapNode.cs`:

```csharp
namespace Rag.NET.GraphRag;

/// <summary>A node in a hierarchical mind-map tree extracted from document content.</summary>
public sealed record MindMapNode(string Title, string Summary, IReadOnlyList<MindMapNode> Children);
```

Create `src/Rag.NET.GraphRag/MindMapOptions.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.GraphRag;

/// <summary>Configuration for mind-map extraction.</summary>
public sealed class MindMapOptions
{
    /// <summary>Run extraction automatically at ingestion time. Default: false.</summary>
    public bool ExtractAtIngestion { get; set; } = false;

    /// <summary>Maximum depth of the generated concept tree. Default: 3.</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Optional cheaper model override. Null = use DI-registered IChatClient.</summary>
    public IChatClient? ChatClient { get; set; }

    /// <summary>LLM prompt template. {text} and {depth} are replaced at runtime.</summary>
    public string Prompt { get; set; } = """
        Analyze the following text and build a hierarchical mind-map of its key concepts.
        Return a JSON object representing the root node with this exact structure:
        {"title": "...", "summary": "...", "children": [...]}
        Each node has: title (short label), summary (1-2 sentence description), children (array of child nodes, same structure).
        Maximum depth: {depth} levels. Aim for 3-7 children per node. Be concise.
        Return only valid JSON, no markdown, no explanation.

        Text:
        {text}
        """;
}
```

**Step 4: Run test to verify it passes**

```
dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "MindMapOptionsTests" -v minimal
```

Expected: PASS (6 tests)

**Step 5: Commit**

```bash
git add src/Rag.NET.GraphRag/MindMapNode.cs src/Rag.NET.GraphRag/MindMapOptions.cs tests/Rag.NET.GraphRag.Tests/MindMapOptionsTests.cs
git commit -m "feat(mind-map): add MindMapNode record and MindMapOptions"
```

---

### Task 2: `MindMapExtractor` — LLM call + deserialization

**Files:**
- Create: `src/Rag.NET.GraphRag/MindMapExtractor.cs`
- Create: `tests/Rag.NET.GraphRag.Tests/MindMapExtractorTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.GraphRag.Tests/MindMapExtractorTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class MindMapExtractorTests
{
    private const string ValidJson = """
        {
          "title": "Machine Learning",
          "summary": "Overview of ML concepts.",
          "children": [
            {
              "title": "Supervised Learning",
              "summary": "Learning with labeled data.",
              "children": []
            },
            {
              "title": "Unsupervised Learning",
              "summary": "Learning without labels.",
              "children": []
            }
          ]
        }
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    private MindMapExtractor CreateSut(MindMapOptions? options = null) =>
        new(_chatClient, graphStore: null, options ?? new MindMapOptions());

    private void SetupChatClient(string response) =>
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

    [Fact]
    public async Task ExtractAsync_ValidJson_ReturnsParsedTree()
    {
        SetupChatClient(ValidJson);
        var sut = CreateSut();

        var result = await sut.ExtractAsync("Some text about ML.", "doc-1", TestContext.Current.CancellationToken);

        Assert.Equal("Machine Learning", result.Title);
        Assert.Equal(2, result.Children.Count);
        Assert.Equal("Supervised Learning", result.Children[0].Title);
    }

    [Fact]
    public async Task ExtractAsync_ValidJson_ChildrenHaveSummaries()
    {
        SetupChatClient(ValidJson);
        var sut = CreateSut();

        var result = await sut.ExtractAsync("Some text.", "doc-1", TestContext.Current.CancellationToken);

        Assert.Equal("Learning with labeled data.", result.Children[0].Summary);
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ReturnsEmptyRoot()
    {
        SetupChatClient("not valid json {{");
        var sut = CreateSut();

        var result = await sut.ExtractAsync("Some text.", "doc-1", TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result.Title);
        Assert.Empty(result.Children);
    }

    [Fact]
    public async Task ExtractAsync_SendsPromptWithTextAndDepth()
    {
        SetupChatClient(ValidJson);
        var options = new MindMapOptions { MaxDepth = 5 };
        var sut = CreateSut(options);

        await sut.ExtractAsync("My document text.", "doc-1", TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IEnumerable<ChatMessage>>(msgs =>
                msgs.Any(m => m.Text != null &&
                              m.Text.Contains("My document text.") &&
                              m.Text.Contains("5"))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_UsesCustomChatClientWhenProvided()
    {
        var customClient = Substitute.For<IChatClient>();
        customClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJson)]));

        var options = new MindMapOptions { ChatClient = customClient };
        var sut = new MindMapExtractor(_chatClient, graphStore: null, options);

        await sut.ExtractAsync("text", "doc-1", TestContext.Current.CancellationToken);

        await customClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "MindMapExtractorTests" -v minimal
```

Expected: compile error — `MindMapExtractor` not defined.

**Step 3: Implement**

Create `src/Rag.NET.GraphRag/MindMapExtractor.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Rag.NET.Graph;

namespace Rag.NET.GraphRag;

/// <summary>
/// Extracts a hierarchical mind-map tree from document text using an LLM.
/// Optionally persists nodes and edges to an IGraphStore.
/// </summary>
public sealed class MindMapExtractor(IChatClient chatClient, IGraphStore? graphStore, MindMapOptions options)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Extract a mind-map tree from <paramref name="text"/>. If an IGraphStore was provided,
    /// nodes are written as GraphEntity (Type = "mind_map_node") and edges as GraphRelationship
    /// (Description = "has_subtopic"), all tagged with <paramref name="documentId"/>.
    /// Returns an empty root node on LLM or parse failure (never throws).
    /// </summary>
    public async Task<MindMapNode> ExtractAsync(string text, string documentId, CancellationToken ct)
    {
        var client = options.ChatClient ?? chatClient;
        var prompt = options.Prompt
            .Replace("{text}", text)
            .Replace("{depth}", options.MaxDepth.ToString());

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
        }
        catch
        {
            return EmptyRoot();
        }

        var root = TryParse(response.Message.Text);
        if (root is null)
            return EmptyRoot();

        if (graphStore is not null)
            await PersistAsync(root, parentName: null, documentId, ct).ConfigureAwait(false);

        return root;
    }

    private async Task PersistAsync(MindMapNode node, string? parentName, string documentId, CancellationToken ct)
    {
        var entity = new GraphEntity(node.Title, "mind_map_node", node.Summary)
        {
            SourceDocumentId = documentId,
        };
        await graphStore!.AddEntitiesAsync([entity], ct).ConfigureAwait(false);

        if (parentName is not null)
        {
            var rel = new GraphRelationship(parentName, node.Title, "has_subtopic", Weight: 1.0);
            await graphStore.AddRelationshipsAsync([rel], ct).ConfigureAwait(false);
        }

        foreach (var child in node.Children)
            await PersistAsync(child, node.Title, documentId, ct).ConfigureAwait(false);
    }

    private static MindMapNode? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<MindMapNode>(json, s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static MindMapNode EmptyRoot() => new(string.Empty, string.Empty, []);
}
```

> **Note:** `MindMapNode` is a record with positional constructor `(Title, Summary, Children)`. `System.Text.Json` needs constructor parameter names to match JSON property names (case-insensitive). Since the record uses `IReadOnlyList<MindMapNode>`, add a `[JsonConstructor]` attribute or a `JsonSerializableAttribute` if deserialization fails in testing — but with `PropertyNameCaseInsensitive = true` it typically works for records with matching names.

**Step 4: Run test to verify it passes**

```
dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "MindMapExtractorTests" -v minimal
```

Expected: PASS (5 tests)

**Step 5: Commit**

```bash
git add src/Rag.NET.GraphRag/MindMapExtractor.cs tests/Rag.NET.GraphRag.Tests/MindMapExtractorTests.cs
git commit -m "feat(mind-map): add MindMapExtractor with LLM call and JSON deserialization"
```

---

### Task 3: Graph persistence — nodes and edges written to `IGraphStore`

**Files:**
- Modify: `tests/Rag.NET.GraphRag.Tests/MindMapExtractorTests.cs` (add graph storage tests)

**Step 1: Write the failing tests**

Add to `MindMapExtractorTests.cs` — append a new test class at the bottom:

```csharp
public class MindMapExtractorGraphPersistenceTests : IAsyncDisposable
{
    private const string NestedJson = """
        {
          "title": "Root",
          "summary": "Root summary.",
          "children": [
            {
              "title": "Child A",
              "summary": "Child A summary.",
              "children": [
                {
                  "title": "Grandchild",
                  "summary": "Grandchild summary.",
                  "children": []
                }
              ]
            },
            {
              "title": "Child B",
              "summary": "Child B summary.",
              "children": []
            }
          ]
        }
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly SqliteGraphStore _graphStore = new(":memory:");

    public ValueTask DisposeAsync() => _graphStore.DisposeAsync();

    private void SetupChatClient(string response) =>
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

    [Fact]
    public async Task ExtractAsync_PersistsAllNodesAsEntities()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, _graphStore, new MindMapOptions());

        await sut.ExtractAsync("text", "doc-1", TestContext.Current.CancellationToken);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var mindMapNodes = snapshot.Entities.Where(e => e.Type == "mind_map_node").ToList();
        Assert.Equal(4, mindMapNodes.Count); // Root + Child A + Child B + Grandchild
    }

    [Fact]
    public async Task ExtractAsync_PersistsEdgesAsHasSubtopicRelationships()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, _graphStore, new MindMapOptions());

        await sut.ExtractAsync("text", "doc-1", TestContext.Current.CancellationToken);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var edges = snapshot.Relationships.Where(r => r.Description == "has_subtopic").ToList();
        Assert.Equal(3, edges.Count); // Root→ChildA, Root→ChildB, ChildA→Grandchild
    }

    [Fact]
    public async Task ExtractAsync_TagsEntitiesWithDocumentId()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, _graphStore, new MindMapOptions());

        await sut.ExtractAsync("text", "my-doc", TestContext.Current.CancellationToken);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.All(
            snapshot.Entities.Where(e => e.Type == "mind_map_node"),
            e => Assert.Equal("my-doc", e.SourceDocumentId));
    }

    [Fact]
    public async Task ExtractAsync_NoGraphStore_DoesNotThrow()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, graphStore: null, new MindMapOptions());

        var result = await sut.ExtractAsync("text", "doc-1", TestContext.Current.CancellationToken);

        Assert.Equal("Root", result.Title);
    }
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "MindMapExtractorGraphPersistenceTests" -v minimal
```

Expected: FAIL — likely compile error if `SqliteGraphStore` isn't imported, or assertion failure if persistence isn't implemented yet (it is, from Task 2 — so these should pass after adding the `using` for `SqliteGraphStore`).

> `SqliteGraphStore` lives in `Rag.NET.Graph` which is already a project reference in the test csproj.

**Step 3: Run test to verify it passes**

```
dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "MindMapExtractorGraphPersistenceTests" -v minimal
```

Expected: PASS (4 tests)

**Step 4: Commit**

```bash
git add tests/Rag.NET.GraphRag.Tests/MindMapExtractorTests.cs
git commit -m "test(mind-map): add graph persistence tests for MindMapExtractor"
```

---

### Task 4: `MindMapExtractionBehavior`

**Files:**
- Create: `src/Rag.NET.GraphRag/MindMapExtractionBehavior.cs`
- Create: `tests/Rag.NET.GraphRag.Tests/MindMapExtractionBehaviorTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.GraphRag.Tests/MindMapExtractionBehaviorTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class MindMapExtractionBehaviorTests
{
    private const string ValidJson = """
        {"title":"Root","summary":"Root summary.","children":[]}
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    private static IngestionContext CreateContext(string docId = "doc-1", string chunkText = "Some chunk text.")
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId(docId), FileName = "test.txt" },
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk
        {
            Text = chunkText,
            DocumentId = new DocumentId(docId),
            ChunkIndex = 0,
        });
        return ctx;
    }

    private void SetupChatClient(string response) =>
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

    [Fact]
    public async Task HandleAsync_WhenExtractAtIngestionFalse_DoesNotCallLlm()
    {
        var options = new MindMapOptions { ExtractAtIngestion = false };
        var extractor = new MindMapExtractor(_chatClient, graphStore: null, options);
        var sut = new MindMapExtractionBehavior(extractor, options);
        var ctx = CreateContext();

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenExtractAtIngestionTrue_CallsLlmOnce()
    {
        SetupChatClient(ValidJson);
        var options = new MindMapOptions { ExtractAtIngestion = true };
        var extractor = new MindMapExtractor(_chatClient, graphStore: null, options);
        var sut = new MindMapExtractionBehavior(extractor, options);
        var ctx = CreateContext();

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AlwaysCallsNext()
    {
        var options = new MindMapOptions { ExtractAtIngestion = false };
        var extractor = new MindMapExtractor(_chatClient, graphStore: null, options);
        var sut = new MindMapExtractionBehavior(extractor, options);
        var ctx = CreateContext();
        var nextCalled = false;

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }); });

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task HandleAsync_ConcatenatesAllChunkTexts()
    {
        SetupChatClient(ValidJson);
        var options = new MindMapOptions { ExtractAtIngestion = true };
        var extractor = new MindMapExtractor(_chatClient, graphStore: null, options);
        var sut = new MindMapExtractionBehavior(extractor, options);

        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.txt" },
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk { Text = "First chunk.", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });
        ctx.Chunks.Add(new TextChunk { Text = "Second chunk.", DocumentId = new DocumentId("doc-1"), ChunkIndex = 1 });

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IEnumerable<ChatMessage>>(msgs =>
                msgs.Any(m => m.Text != null &&
                              m.Text.Contains("First chunk.") &&
                              m.Text.Contains("Second chunk."))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "MindMapExtractionBehaviorTests" -v minimal
```

Expected: compile error — `MindMapExtractionBehavior` not defined.

**Step 3: Implement**

Create `src/Rag.NET.GraphRag/MindMapExtractionBehavior.cs`:

```csharp
using Rag.NET.Ingestion;
using Rag.NET.Models;

namespace Rag.NET.GraphRag;

/// <summary>
/// Ingestion behavior that extracts a mind-map from the full document text.
/// Only runs when MindMapOptions.ExtractAtIngestion is true.
/// </summary>
public sealed class MindMapExtractionBehavior(MindMapExtractor extractor, MindMapOptions options)
    : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx,
        CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (options.ExtractAtIngestion && ctx.Chunks.Count > 0)
        {
            var fullText = string.Join("\n\n", ctx.Chunks.Select(c => c.Text));
            var documentId = ctx.Metadata.DocumentId.ToString();
            await extractor.ExtractAsync(fullText, documentId, ct).ConfigureAwait(false);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Run test to verify it passes**

```
dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "MindMapExtractionBehaviorTests" -v minimal
```

Expected: PASS (4 tests)

**Step 5: Commit**

```bash
git add src/Rag.NET.GraphRag/MindMapExtractionBehavior.cs tests/Rag.NET.GraphRag.Tests/MindMapExtractionBehaviorTests.cs
git commit -m "feat(mind-map): add MindMapExtractionBehavior ingestion hook"
```

---

### Task 5: DI registration — `UseMindMapExtraction`

**Files:**
- Modify: `src/Rag.NET.GraphRag/RagBuilderExtensions.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseMindMapExtractionTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/DependencyInjection/UseMindMapExtractionTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.DependencyInjection;
using Rag.NET.GraphRag;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseMindMapExtractionTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UseMindMapExtraction_RegistersMindMapExtractor()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction())
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<MindMapExtractor>());
    }

    [Fact]
    public void UseMindMapExtraction_RegistersMindMapExtractionBehavior()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction())
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<MindMapExtractionBehavior>());
    }

    [Fact]
    public void UseMindMapExtraction_DefaultOptions_ExtractAtIngestionIsFalse()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction())
            .BuildServiceProvider();

        Assert.False(sp.GetRequiredService<MindMapOptions>().ExtractAtIngestion);
    }

    [Fact]
    public void UseMindMapExtraction_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction(o =>
            {
                o.ExtractAtIngestion = true;
                o.MaxDepth = 5;
            }))
            .BuildServiceProvider();

        var opts = sp.GetRequiredService<MindMapOptions>();
        Assert.True(opts.ExtractAtIngestion);
        Assert.Equal(5, opts.MaxDepth);
    }

    [Fact]
    public void UseMindMapExtraction_WithoutGraphRag_DoesNotThrow()
    {
        // MindMapExtractor should resolve even without IGraphStore registered
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction())
            .BuildServiceProvider();

        var extractor = sp.GetRequiredService<MindMapExtractor>();
        Assert.NotNull(extractor);
    }
}
```

> **Note:** The test project (`Rag.NET.Tests`) references `Rag.NET` core but not `Rag.NET.GraphRag`. You need to add a project reference. Open `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` and add:
> ```xml
> <ProjectReference Include="..\..\src\Rag.NET.GraphRag\Rag.NET.GraphRag.csproj" />
> ```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "UseMindMapExtractionTests" -v minimal
```

Expected: compile error — `UseMindMapExtraction` not defined.

**Step 3: Implement**

Open `src/Rag.NET.GraphRag/RagBuilderExtensions.cs` and add a new extension method after `UseGraphRag`:

```csharp
/// <summary>
/// Enables mind-map extraction — builds a hierarchical concept tree from document content
/// via a single LLM call. Nodes are stored in IGraphStore (if registered) as GraphEntity
/// with Type = "mind_map_node".
/// </summary>
public static RagBuilder UseMindMapExtraction(
    this RagBuilder builder,
    Action<MindMapOptions>? configure = null)
{
    var options = new MindMapOptions();
    configure?.Invoke(options);
    builder.Services.AddSingleton(options);

    builder.Services.AddSingleton<MindMapExtractor>(sp =>
        new MindMapExtractor(
            options.ChatClient ?? sp.GetRequiredService<IChatClient>(),
            sp.GetService<IGraphStore>(),
            options));

    builder.Services.AddSingleton<MindMapExtractionBehavior>(sp =>
        new MindMapExtractionBehavior(
            sp.GetRequiredService<MindMapExtractor>(),
            options));

    return builder;
}
```

Also add `using Rag.NET.Graph;` to the top of `RagBuilderExtensions.cs` if not already present (needed for `IGraphStore`).

**Step 4: Run test to verify it passes**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "UseMindMapExtractionTests" -v minimal
```

Expected: PASS (5 tests)

**Step 5: Run all GraphRag tests**

```
dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj -v minimal
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v minimal
```

Expected: all pass, no regressions.

**Step 6: Commit**

```bash
git add src/Rag.NET.GraphRag/RagBuilderExtensions.cs tests/Rag.NET.Tests/Rag.NET.Tests.csproj tests/Rag.NET.Tests/DependencyInjection/UseMindMapExtractionTests.cs
git commit -m "feat(mind-map): register UseMindMapExtraction DI extension"
```

---

### Task 6: Update `features.md` to mark Mind-Map Extractor as done

**Files:**
- Modify: `docs/reference/features.md`

**Step 1: Add status and usage details**

In `docs/reference/features.md`, find the `### Mind-Map Extractor` section and update it to match the pattern of other completed features. Add:

```markdown
### Mind-Map Extractor
**Package:** `Rag.NET.GraphRag`

**Status:** ✅ Done

Build a hierarchical concept tree from document content using a single LLM call. Nodes are stored as `GraphEntity` (Type = `"mind_map_node"`) and parent→child edges as `GraphRelationship` (Description = `"has_subtopic"`) in the existing `IGraphStore`. Retrieve via `GetFullGraphAsync()` and filter on type. Optionally runs automatically at ingestion time.

**Usage**

```csharp
// Standalone (on-demand, no persistence):
services.AddRagNet(rag => rag.UseMindMapExtraction());
var extractor = sp.GetRequiredService<MindMapExtractor>();
var tree = await extractor.ExtractAsync(text, documentId, ct);

// With ingestion-time extraction + graph storage:
services.AddRagNet(rag => rag
    .UseGraphRag()
    .UseMindMapExtraction(o => {
        o.ExtractAtIngestion = true;
        o.MaxDepth = 3;
    }));
```
```

Also add `[x]` to the Mind-Map Extractor row in the priority table at the bottom.

**Step 2: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark Mind-Map Extractor as done in feature backlog"
```
