# Cohere Rerank Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `Rag.NET.Reranking.Cohere` — an `IReranker` implementation that calls the Cohere `/rerank` API using the official `Cohere` NuGet package.

**Architecture:** Thin wrapper around `CohereClient` following the exact pattern of `OnnxReranker`. `CohereReranker` constructs a `CohereClient` from `CohereRerankerOptions` in its constructor, batches documents when >1000, calls `RerankAsync`, and maps results back to `RerankResult` sorted descending by score. Registered via `UseCohereReranking()` on `RagBuilder`.

**Tech Stack:** `Cohere` 1.0.0 NuGet, `WireMock.Net` for HTTP stub tests, xunit.v3, .NET 10.

---

## Cohere SDK Quick Reference

- Constructor (simple): `new CohereClient(string apiKey)`
- Constructor (with endpoint override): `new CohereClient(string apiKey, HttpClient httpClient, Uri baseUri, List<>? authorizations = null, bool disposeHttpClient = false)`
- Rerank call: `await client.RerankAsync(new RerankRequest { ... }, xClientName: "", cancellationToken)`
- `RerankRequest.Documents` type: `IList<OneOf<string, RerankDocument>>` — has implicit conversion from `string`
- `RerankResponse.Results`: `IList<RerankResponseResult>`
- `RerankResponseResult.Index` (int), `RerankResponseResult.RelevanceScore` (float)
- `RerankRequest.ReturnDocuments` — `bool?`
- `RerankRequest.TopN` — `int?`

---

## Task 1: Create the source project scaffold

**Files:**
- Create: `src/Rag.NET.Reranking.Cohere/Rag.NET.Reranking.Cohere.csproj`

**Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Reranking.Cohere</RootNamespace>
    <PackageId>Rag.NET.Reranking.Cohere</PackageId>
    <Description>Cohere Rerank API integration for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Cohere" Version="1.*" />
  </ItemGroup>

</Project>
```

**Step 2: Add project to solution**

Edit `Rag.NET.slnx` — add under the `/src/` folder, after the Onnx entry:
```xml
<Project Path="src/Rag.NET.Reranking.Cohere/Rag.NET.Reranking.Cohere.csproj" />
```

**Step 3: Verify build**

```bash
dotnet build src/Rag.NET.Reranking.Cohere/Rag.NET.Reranking.Cohere.csproj
```
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add src/Rag.NET.Reranking.Cohere/ Rag.NET.slnx
git commit -m "chore: scaffold Rag.NET.Reranking.Cohere project"
```

---

## Task 2: Implement `CohereRerankerOptions`

**Files:**
- Create: `src/Rag.NET.Reranking.Cohere/CohereRerankerOptions.cs`

**Step 1: Create the options class**

```csharp
namespace Rag.NET.Reranking.Cohere;

/// <summary>
/// Configuration options for <see cref="CohereReranker"/>.
/// </summary>
public sealed class CohereRerankerOptions
{
    /// <summary>
    /// Cohere API key. Required.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Reranking model. Default: <c>rerank-english-v3.0</c> (English-only, fast).
    /// Switch to <c>rerank-v3.5</c> for multilingual workloads.
    /// </summary>
    public string Model { get; init; } = "rerank-english-v3.0";

    /// <summary>
    /// Number of top results to return. Default: 5.
    /// </summary>
    public int TopN { get; init; } = 5;

    /// <summary>
    /// Whether to ask Cohere to echo back document text in the response. Default: <see langword="false"/>.
    /// </summary>
    public bool ReturnDocuments { get; init; }

    /// <summary>
    /// Maximum documents per API call. Cohere's hard limit is 1,000. Default: 1000.
    /// When <paramref name="results"/> exceeds this, calls are batched sequentially and merged.
    /// </summary>
    public int MaxDocumentsPerBatch { get; init; } = 1000;

    /// <summary>
    /// Optional API endpoint override. Useful for testing with a local stub server.
    /// When <see langword="null"/>, the Cohere SDK uses its default endpoint.
    /// </summary>
    public string? Endpoint { get; init; }
}
```

**Step 2: Build**

```bash
dotnet build src/Rag.NET.Reranking.Cohere/Rag.NET.Reranking.Cohere.csproj
```
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/Rag.NET.Reranking.Cohere/CohereRerankerOptions.cs
git commit -m "feat(cohere-rerank): add CohereRerankerOptions"
```

---

## Task 3: Implement `CohereReranker`

**Files:**
- Create: `src/Rag.NET.Reranking.Cohere/CohereReranker.cs`

**Step 1: Create the reranker**

```csharp
using Cohere;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Reranking.Cohere;

/// <summary>
/// Reranks search results using the Cohere Rerank API.
/// </summary>
public sealed class CohereReranker : IReranker, IDisposable
{
    private readonly CohereClient _client;
    private readonly CohereRerankerOptions _options;

    public CohereReranker(CohereRerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("ApiKey must not be null or whitespace.", nameof(options));

        _options = options;
        _client = options.Endpoint is { } endpoint
            ? new CohereClient(options.ApiKey, new HttpClient(), new Uri(endpoint))
            : new CohereClient(options.ApiKey);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Cohere caps individual document text at approximately 10,000 tokens.
    /// If a passage exceeds this limit, the Cohere SDK will throw. Chunk aggressively before reranking.
    /// </remarks>
    public async Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
            return [];

        var allRerankResults = new List<RerankResult>(results.Count);

        // Batch documents to respect Cohere's per-call limit
        for (var offset = 0; offset < results.Count; offset += _options.MaxDocumentsPerBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = results.Skip(offset).Take(_options.MaxDocumentsPerBatch).ToList();
            var documents = batch
                .Select(r => (OneOf<string, RerankDocument>)r.Chunk.Text)
                .ToList();

            var request = new RerankRequest
            {
                Query = query,
                Documents = documents,
                Model = _options.Model,
                TopN = _options.TopN,
                ReturnDocuments = _options.ReturnDocuments,
            };

            var response = await _client.RerankAsync(request, xClientName: "", cancellationToken)
                .ConfigureAwait(false);

            foreach (var result in response.Results)
            {
                allRerankResults.Add(new RerankResult
                {
                    SearchResult = batch[result.Index],
                    RelevanceScore = result.RelevanceScore,
                });
            }
        }

        // Sort descending by score (Cohere returns pre-sorted per batch; re-sort after merge)
        allRerankResults.Sort(static (a, b) => b.RelevanceScore.CompareTo(a.RelevanceScore));
        return allRerankResults;
    }

    public void Dispose() => _client.Dispose();
}
```

**Step 2: Build**

```bash
dotnet build src/Rag.NET.Reranking.Cohere/Rag.NET.Reranking.Cohere.csproj
```
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/Rag.NET.Reranking.Cohere/CohereReranker.cs
git commit -m "feat(cohere-rerank): implement CohereReranker"
```

---

## Task 4: Implement `RagBuilderExtensions`

**Files:**
- Create: `src/Rag.NET.Reranking.Cohere/RagBuilderExtensions.cs`

**Step 1: Create the extension**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Reranking.Cohere;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="CohereReranker"/> as the <see cref="IReranker"/>,
    /// using the Cohere Rerank API for reranking.
    /// Switch <see cref="CohereRerankerOptions.Model"/> to <c>rerank-v3.5</c> for multilingual workloads.
    /// </summary>
    public static RagBuilder UseCohereReranking(this RagBuilder builder, Action<CohereRerankerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CohereRerankerOptions { ApiKey = "" };
        configure(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IReranker, CohereReranker>();

        return builder;
    }
}
```

**Step 2: Build**

```bash
dotnet build src/Rag.NET.Reranking.Cohere/Rag.NET.Reranking.Cohere.csproj
```
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/Rag.NET.Reranking.Cohere/RagBuilderExtensions.cs
git commit -m "feat(cohere-rerank): add UseCohereReranking DI extension"
```

---

## Task 5: Create the test project scaffold

**Files:**
- Create: `tests/Rag.NET.Reranking.Cohere.Tests/Rag.NET.Reranking.Cohere.Tests.csproj`

**Step 1: Create the test csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Reranking.Cohere\Rag.NET.Reranking.Cohere.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
    <PackageReference Include="WireMock.Net" Version="1.*" />
  </ItemGroup>

</Project>
```

**Step 2: Add to solution**

Edit `Rag.NET.slnx` — add under the `/tests/` folder, after the Onnx tests entry:
```xml
<Project Path="tests/Rag.NET.Reranking.Cohere.Tests/Rag.NET.Reranking.Cohere.Tests.csproj" />
```

**Step 3: Build**

```bash
dotnet build tests/Rag.NET.Reranking.Cohere.Tests/Rag.NET.Reranking.Cohere.Tests.csproj
```
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add tests/Rag.NET.Reranking.Cohere.Tests/ Rag.NET.slnx
git commit -m "chore: scaffold Rag.NET.Reranking.Cohere.Tests project"
```

---

## Task 6: Write tests

**Files:**
- Create: `tests/Rag.NET.Reranking.Cohere.Tests/CohereRerankerTests.cs`

The tests use WireMock.Net to spin up a local HTTP server. The Cohere SDK's `/v1/rerank` endpoint is stubbed to return controlled JSON responses.

**Step 1: Write failing test for constructor guard**

```csharp
using Xunit;

namespace Rag.NET.Reranking.Cohere.Tests;

public class CohereRerankerTests
{
    [Fact]
    public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new CohereReranker(null!));
    }

    [Fact]
    public void Constructor_WhenApiKeyIsEmpty_ThrowsArgumentException()
    {
        var options = new CohereRerankerOptions { ApiKey = "" };
        Assert.Throws<ArgumentException>(() => new CohereReranker(options));
    }
}
```

**Step 2: Run to verify failures**

```bash
dotnet build tests/Rag.NET.Reranking.Cohere.Tests && dotnet test tests/Rag.NET.Reranking.Cohere.Tests --no-build
```
Expected: 2 PASS (constructor guards are already implemented).

**Step 3: Write WireMock-based tests**

Add the following tests to `CohereRerankerTests.cs`. These start a WireMock server and stub the `/v1/rerank` endpoint.

```csharp
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Rag.NET.Models;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Rag.NET.Reranking.Cohere.Tests;

public class CohereRerankerTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly CohereRerankerOptions _defaultOptions;

    public CohereRerankerTests()
    {
        _server = WireMockServer.Start();
        _defaultOptions = new CohereRerankerOptions
        {
            ApiKey = "test-key",
            Endpoint = _server.Url!,
        };
    }

    public void Dispose() => _server.Stop();

    [Fact]
    public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new CohereReranker(null!));
    }

    [Fact]
    public void Constructor_WhenApiKeyIsEmpty_ThrowsArgumentException()
    {
        var options = new CohereRerankerOptions { ApiKey = "", Endpoint = _server.Url };
        Assert.Throws<ArgumentException>(() => new CohereReranker(options));
    }

    [Fact]
    public async Task RerankAsync_WhenResultsEmpty_ReturnsEmptyWithoutCallingApi()
    {
        using var reranker = new CohereReranker(_defaultOptions);

        var result = await reranker.RerankAsync("query", []);

        Assert.Empty(result);
        Assert.Empty(_server.LogEntries); // no HTTP calls
    }

    [Fact]
    public async Task RerankAsync_SingleResult_ReturnsMappedScore()
    {
        StubRerank(_server, new[]
        {
            new { index = 0, relevance_score = 0.95f },
        });

        var searchResult = MakeSearchResult("doc A");
        using var reranker = new CohereReranker(_defaultOptions);

        var results = await reranker.RerankAsync("query", [searchResult]);

        Assert.Single(results);
        Assert.Equal(searchResult, results[0].SearchResult);
        Assert.Equal(0.95f, (float)results[0].RelevanceScore, precision: 3);
    }

    [Fact]
    public async Task RerankAsync_MultipleResults_ReturnsSortedDescending()
    {
        // Cohere returns index 1 as most relevant
        StubRerank(_server, new[]
        {
            new { index = 1, relevance_score = 0.9f },
            new { index = 0, relevance_score = 0.3f },
        });

        var r0 = MakeSearchResult("doc 0");
        var r1 = MakeSearchResult("doc 1");
        using var reranker = new CohereReranker(_defaultOptions);

        var results = await reranker.RerankAsync("query", [r0, r1]);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].RelevanceScore >= results[1].RelevanceScore);
        Assert.Equal(r1, results[0].SearchResult);
        Assert.Equal(r0, results[1].SearchResult);
    }

    [Fact]
    public async Task RerankAsync_IndexMappingIsCorrect()
    {
        // Cohere result index refers to position in the documents array
        StubRerank(_server, new[]
        {
            new { index = 2, relevance_score = 0.8f },
        });

        var docs = new[]
        {
            MakeSearchResult("doc 0"),
            MakeSearchResult("doc 1"),
            MakeSearchResult("doc 2"),
        };
        using var reranker = new CohereReranker(_defaultOptions);

        var results = await reranker.RerankAsync("query", docs);

        Assert.Single(results);
        Assert.Equal(docs[2], results[0].SearchResult);
    }

    [Fact]
    public async Task RerankAsync_WhenBatchingRequired_MergesAndSorts()
    {
        // Use MaxDocumentsPerBatch = 2 so 3 docs become 2 batches
        var opts = new CohereRerankerOptions
        {
            ApiKey = "test-key",
            Endpoint = _server.Url,
            MaxDocumentsPerBatch = 2,
        };

        // Batch 1 (docs 0,1): index 0 scores 0.6
        // Batch 2 (doc 2):    index 0 scores 0.9
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .InScenario("batching")
            .WillSetStateTo("batch2")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(RerankJson(new[] { new { index = 0, relevance_score = 0.6f } })));
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .InScenario("batching")
            .WhenStateIs("batch2")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(RerankJson(new[] { new { index = 0, relevance_score = 0.9f } })));

        var docs = new[]
        {
            MakeSearchResult("doc 0"),
            MakeSearchResult("doc 1"),
            MakeSearchResult("doc 2"),
        };
        using var reranker = new CohereReranker(opts);

        var results = await reranker.RerankAsync("query", docs);

        Assert.Equal(2, results.Count); // TopN=5 but only 2 results returned across batches
        Assert.True(results[0].RelevanceScore >= results[1].RelevanceScore);
        Assert.Equal(docs[2], results[0].SearchResult); // 0.9 from batch 2
        Assert.Equal(docs[0], results[1].SearchResult); // 0.6 from batch 1
    }

    [Fact]
    public async Task RerankAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var reranker = new CohereReranker(_defaultOptions);
        var docs = new[] { MakeSearchResult("doc") };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => reranker.RerankAsync("query", docs, cts.Token));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SearchResult MakeSearchResult(string text) =>
        new() { Chunk = new DocumentChunk { Text = text }, Score = 0.5 };

    private static void StubRerank(WireMockServer server, IEnumerable<object> results)
    {
        server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson(results)));
    }

    private static string RerankJson(IEnumerable<object> results) =>
        JsonSerializer.Serialize(new { id = "test-id", results });
}
```

**Step 4: Build and run**

```bash
dotnet build tests/Rag.NET.Reranking.Cohere.Tests && dotnet test tests/Rag.NET.Reranking.Cohere.Tests --no-build
```
Expected: All tests PASS.

> **Note on `SearchResult` / `DocumentChunk`:** Check the exact property names in `Rag.NET.Models`. If `DocumentChunk` uses a different property (e.g., `Content` vs `Text`), adjust `MakeSearchResult` accordingly. Look at `OnnxRerankerTests` or the `SearchResult` model for the correct shape.

**Step 5: Commit**

```bash
git add tests/Rag.NET.Reranking.Cohere.Tests/CohereRerankerTests.cs
git commit -m "test(cohere-rerank): add CohereReranker unit tests"
```

---

## Task 7: Update features backlog

**Files:**
- Modify: `docs/reference/features.md`

Mark the Cohere Rerank item as complete (`- [x]`).

**Step 1: Find and update the entry**

Search for `Cohere Rerank` in `docs/reference/features.md` and change `- [ ]` to `- [x]`.

**Step 2: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark Cohere Rerank as complete in features backlog"
```

---

## Final verification

```bash
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Reranking.Cohere.Tests
```
Expected: Solution builds clean, all tests pass.
