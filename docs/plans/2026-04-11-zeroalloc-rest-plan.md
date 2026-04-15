# ZeroAlloc.Rest Migration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Status:** ✅ Done

**Goal:** Replace Refit with ZeroAlloc.Rest across 7 data provider packages, adopting `Result<T, HttpError>` on every API method and propagating typed HTTP errors through `IFileContentProvider` → `RagPipelineExtensions` → `ProviderIngestionResult`.

**Architecture:** Three layers change. First, `RagError` gains `HttpFailed(StatusCode, Content)`. Then the core pipeline interfaces (`IFileContentProvider`, `FileContentProviderBase`, `ProviderIngestionResult`, `RagPipelineExtensions`) are updated to carry `Result<FileEntry, RagError>`. Finally, each of the 7 data providers is migrated one at a time: swap Refit for ZeroAlloc.Rest in the csproj, update the API interface, update the DI registration, and update the provider implementation to unwrap `Result<T, HttpError>` from API calls.

**Tech Stack:** ZeroAlloc.Rest 0.* (source-generated REST client), ZeroAlloc.Rest.Generator 0.* (Roslyn generator, PrivateAssets), ZeroAlloc.Results (already in use), `System.Net.HttpStatusCode` (no extra dependency needed for `RagError.HttpFailed`).

**Important:** ZeroAlloc.Rest is new (v0.1.x). After adding it to a csproj, **build the project and inspect the generated code** to verify the exact generated extension method name (e.g. `AddIConfluenceApi`) and the `HttpError` type shape. The README is at https://github.com/ZeroAlloc-Net/ZeroAlloc.Rest.

**Test strategy:** All 7 data provider test projects use custom `HttpMessageHandler` subclasses (not mocks of the API interfaces). These tests survive the migration unchanged — they exercise the real HTTP layer beneath the generated client. The only test changes are: update `RagError`/`ProviderIngestionResult`/`IFileContentProvider` tests to match new signatures, and update the Confluence stale-delta-token test to expect `Result.Failure` instead of a caught exception.

**Run all tests with:** `dotnet test --no-build -v quiet`

---

### Task 1: Add `RagError.HttpFailed`

**Design:** `RagError.HttpFailed` stores `StatusCode` and optional response `Content`. No dependency on ZeroAlloc.Rest — uses `System.Net.HttpStatusCode` which is already in scope. Data providers convert `HttpError → RagError.HttpFailed` at their boundary.

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/RagError.cs`
- Modify: `tests/Rag.NET.Tests/Models/RagErrorTests.cs`

**Step 1: Write the failing test**

Open `tests/Rag.NET.Tests/Models/RagErrorTests.cs` — read its current content, then add:

```csharp
[Fact]
public void RagError_HttpFailed_CanBeConstructedAndPatternMatched()
{
    RagError error = new RagError.HttpFailed(System.Net.HttpStatusCode.NotFound, "Not Found");

    var message = error switch
    {
        RagError.HttpFailed { StatusCode: System.Net.HttpStatusCode.NotFound } e => $"HTTP {(int)e.StatusCode}",
        _ => "other",
    };

    Assert.Equal("HTTP 404", message);
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagErrorTests" -v quiet
```

Expected: compile error — `RagError.HttpFailed` does not exist.

**Step 3: Add the new case to `RagError.cs`**

Add after the existing `NonSeekableStream` case:

```csharp
/// <summary>An HTTP call to an external data provider failed.</summary>
/// <param name="StatusCode">The HTTP status code returned by the server.</param>
/// <param name="Content">The response body, if any.</param>
public sealed record HttpFailed(System.Net.HttpStatusCode StatusCode, string? Content) : RagError;
```

**Step 4: Run test to verify it passes**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagErrorTests" -v quiet
```

Expected: PASS.

**Step 5: Commit**

```
git add src/Rag.NET.Abstractions/Models/RagError.cs tests/Rag.NET.Tests/Models/RagErrorTests.cs
git commit -m "feat(errors): add RagError.HttpFailed(StatusCode, Content)"
```

---

### Task 2: Update Core Pipeline Infrastructure

**Design:** Change `IFileContentProvider.GetFilesAsync` to yield `Result<FileEntry, RagError>`. Update `FileContentProviderBase.GetFileHandlesAsync` (abstract method) and `GetFilesAsync` (base implementation) to propagate failures. Change `ProviderIngestionResult.Errors` from `IReadOnlyList<string>` to `IReadOnlyList<RagError>`. Update `RagPipelineExtensions` to iterate `Result<FileEntry, RagError>` and collect typed errors.

This task will cause many compile errors — that is expected. Fix them all before committing.

**Files:**
- Modify: `src/Rag.NET/DataProviders/IFileContentProvider.cs`
- Modify: `src/Rag.NET.DataProviders/FileContentProviderBase.cs`
- Modify: `src/Rag.NET/DataProviders/ProviderIngestionResult.cs`
- Modify: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs`
- Modify: `tests/Rag.NET.DataProviders.Tests/FileContentProviderBaseTests.cs`
- Modify: `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`

**Step 1: Change `IFileContentProvider`**

```csharp
using ZeroAlloc.Results;
using Rag.NET.Models;

namespace Rag.NET.DataProviders;

/// <summary>
/// Provides a stream of file entries from an arbitrary source (local disk, web, GitHub, etc.).
/// </summary>
public interface IFileContentProvider
{
    IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
        CancellationToken cancellationToken = default);
}
```

**Step 2: Change `FileContentProviderBase`**

The abstract method `GetFileHandlesAsync` now returns `Result<FileHandle, RagError>`. The base `GetFilesAsync` propagates failures through and applies filtering only to successes.

```csharp
using System.Runtime.CompilerServices;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders;

/// <summary>
/// Base class for cloud storage data providers.
/// Handles extension filtering and <see cref="CloudStorageOptions.Filter"/> application.
/// Connectors only need to implement <see cref="GetFileHandlesAsync"/>.
/// </summary>
public abstract class FileContentProviderBase : IFileContentProvider
{
    private readonly CloudStorageOptions _options;

    protected FileContentProviderBase(CloudStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Enumerate raw file handles from the vendor SDK.
    /// Yield <see cref="Result{TValue,TError}.Failure"/> on HTTP errors.
    /// No filtering required — the base class handles it.
    /// </summary>
    protected abstract IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var result in GetFileHandlesAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (result.IsFailure)
            {
                yield return Result<FileEntry, RagError>.Failure(result.Error);
                continue;
            }

            var handle = result.Value;
            if (!MatchesExtension(handle.FileName)) continue;
            if (_options.Filter is not null && !_options.Filter(handle.Id)) continue;

            yield return Result<FileEntry, RagError>.Success(new FileEntry(
                Id:               new EntryId(handle.Id),
                FileName:         handle.FileName,
                OpenContentAsync: handle.OpenContentAsync,
                ETag:             handle.ETag));
        }
    }

    private bool MatchesExtension(string fileName)
        => FileExtensionMatcher.Matches(fileName, _options.Extensions);
}
```

**Step 3: Change `ProviderIngestionResult`**

```csharp
using Rag.NET.Models;

namespace Rag.NET.DataProviders;

/// <summary>Summary of a completed <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> run.</summary>
public sealed record ProviderIngestionResult(
    int Ingested,
    int Skipped,
    int Deleted,
    IReadOnlyList<RagError> Errors);
```

**Step 4: Update `RagPipelineExtensions`**

The `entries` collection loop now handles `Result<FileEntry, RagError>`. Failures go directly to the errors bag; only successes go into `entries` for processing. The `errors` bag type changes from `ConcurrentBag<string>` to `ConcurrentBag<RagError>`.

Key changes in `IngestFromProviderAsync`:

```csharp
// Change errors bag type
var errors = new ConcurrentBag<RagError>();

// Change the entry-collection loop
var entries = new List<FileEntry>();
await foreach (var result in provider.GetFilesAsync(cancellationToken).ConfigureAwait(false))
{
    if (result.IsFailure) { errors.Add(result.Error); continue; }
    entries.Add(result.Value);
}
```

In `ProcessEntryAsync`, the catch block changes from string to `RagError`:

```csharp
// Before:
// errors.Add($"{entry.Id}: {ex.Message}");

// After:
errors.Add(new RagError.StorageFailed(ex));
```

In `CleanupDisappearedAsync`, same change:
```csharp
// Before:
// errors.Add($"delete {id}: {ex.Message}");

// After:
errors.Add(new RagError.StorageFailed(ex));
```

The return statement:
```csharp
return new ProviderIngestionResult(ingested, skipped, deleted, errors.ToList());
```
(no change here — `errors.ToList()` still works, just typed differently)

**Step 5: Fix `FileContentProviderBaseTests`**

The `StubProvider` inner class must update `GetFileHandlesAsync` to return `Result<FileHandle, RagError>`:

```csharp
private sealed class StubProvider(
    CloudStorageOptions options,
    params FileHandle[] handles) : FileContentProviderBase(options)
{
    protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var h in handles)
            yield return Result<FileHandle, RagError>.Success(h);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
```

Also update all `GetFilesAsync` call-sites in the tests — `results` is now `IReadOnlyList<Result<FileEntry, RagError>>`. Unwrap each item:

```csharp
// Before:
var results = await sut.GetFilesAsync(...).ToListAsync(...);
Assert.Equal(2, results.Count);
Assert.Equal("readme.md", results[0].Id);

// After:
var results = await sut.GetFilesAsync(...).ToListAsync(...);
var entries = results.Select(r => r.Value).ToList(); // all succeed in these tests
Assert.Equal(2, entries.Count);
Assert.Equal("readme.md", entries[0].Id);
```

Add a new test for failure propagation:

```csharp
[Fact]
public async Task GetFilesAsync_HandleFailure_PropagatesAsFailureResult()
{
    var failingProvider = new FailingStubProvider(new TestOptions());

    var results = await failingProvider.GetFilesAsync(TestContext.Current.CancellationToken)
        .ToListAsync(TestContext.Current.CancellationToken);

    Assert.Single(results);
    Assert.True(results[0].IsFailure);
    Assert.IsType<RagError.HttpFailed>(results[0].Error);
}

private sealed class FailingStubProvider(CloudStorageOptions options) : FileContentProviderBase(options)
{
    protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return Result<FileHandle, RagError>.Failure(
            new RagError.HttpFailed(System.Net.HttpStatusCode.ServiceUnavailable, null));
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
```

**Step 6: Fix `IngestFromProviderTests`**

The `MakeProvider` helper now yields `Result<FileEntry, RagError>`:

```csharp
private static IFileContentProvider MakeProvider(params (string id, string fileName, string content, string? etag)[] entries)
{
    var provider = Substitute.For<IFileContentProvider>();
    provider.GetFilesAsync(Arg.Any<CancellationToken>())
        .Returns(entries.Select(e => Result<FileEntry, RagError>.Success(new FileEntry(
            Id: new EntryId(e.id),
            FileName: e.fileName,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(e.content))),
            ETag: e.etag))).ToAsyncEnumerable());
    return provider;
}
```

The error-related assertions change:

```csharp
// Before:
Assert.Single(result.Errors);
Assert.Contains("id-1", result.Errors[0], StringComparison.Ordinal);

// After:
Assert.Single(result.Errors);
Assert.IsType<RagError.StorageFailed>(result.Errors[0]); // ingestion failures become StorageFailed
```

Add a new test for HTTP failure propagation from the provider:

```csharp
[Fact]
public async Task IngestFromProviderAsync_ProviderHttpFailure_AppearsInErrors()
{
    var provider = Substitute.For<IFileContentProvider>();
    provider.GetFilesAsync(Arg.Any<CancellationToken>())
        .Returns(new[]
        {
            Result<FileEntry, RagError>.Failure(
                new RagError.HttpFailed(System.Net.HttpStatusCode.Unauthorized, "Unauthorized")),
        }.ToAsyncEnumerable());

    var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
        cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal(0, result.Ingested);
    Assert.Single(result.Errors);
    var error = Assert.IsType<RagError.HttpFailed>(result.Errors[0]);
    Assert.Equal(System.Net.HttpStatusCode.Unauthorized, error.StatusCode);
}
```

**Step 7: Build and fix all remaining compile errors**

```
dotnet build --no-incremental 2>&1 | grep error
```

Work through each compile error. Most will be in data provider implementations that override `GetFileHandlesAsync` with the old signature. Change each to `IAsyncEnumerable<Result<FileHandle, RagError>>` and wrap each `yield return fileHandle` in `Result<FileHandle, RagError>.Success(fileHandle)`. At this point, these providers still use Refit — leave the API call return types as `Task<T>` for now (they will be fixed in Tasks 3–9).

**Step 8: Run all tests**

```
dotnet test --no-build -v quiet
```

Expected: all tests pass (the providers that haven't been migrated yet still use Refit returning `Task<T>` — the base class wraps their results in `Success`).

**Step 9: Commit**

```
git add src/Rag.NET/DataProviders/IFileContentProvider.cs
git add src/Rag.NET.DataProviders/FileContentProviderBase.cs
git add src/Rag.NET/DataProviders/ProviderIngestionResult.cs
git add src/Rag.NET/DataProviders/RagPipelineExtensions.cs
git add tests/Rag.NET.DataProviders.Tests/FileContentProviderBaseTests.cs
git add tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs
git commit -m "feat(providers): propagate Result<FileEntry, RagError> through IFileContentProvider pipeline"
```

---

### Task 3: Migrate Confluence

**Files:**
- Modify: `src/Rag.NET.DataProviders.Confluence/Rag.NET.DataProviders.Confluence.csproj`
- Modify: `src/Rag.NET.DataProviders.Confluence/IConfluenceApi.cs`
- Modify: `src/Rag.NET.DataProviders.Confluence/ConfluenceDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Confluence/ConfluenceDataProvider.cs`
- Modify: `tests/Rag.NET.DataProviders.Confluence.Tests/ConfluenceDataProviderTests.cs`

**Step 1: Update csproj**

Replace:
```xml
<PackageReference Include="Refit" Version="8.*" />
<PackageReference Include="Refit.HttpClientFactory" Version="8.*" />
```

With:
```xml
<PackageReference Include="ZeroAlloc.Rest" Version="0.*" />
<PackageReference Include="ZeroAlloc.Rest.Generator" Version="0.*" PrivateAssets="all" ExcludeAssets="runtime" />
```

**Step 2: Build and inspect generated output**

```
dotnet build src/Rag.NET.DataProviders.Confluence/Rag.NET.DataProviders.Confluence.csproj
```

After building, check what the generator produced. Look in the `obj/` folder or use IDE navigation to find the generated extension method name. It should be something like `AddIConfluenceApi`. Verify the `HttpError` type's properties (likely `StatusCode`, `Content`).

**Step 3: Update `IConfluenceApi`**

```csharp
using System.Net.Http;
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Confluence;

[ZeroAllocRestClient]
[Headers("Accept: application/json")]
internal interface IConfluenceApi
{
    [Get("/wiki/rest/api/content")]
    Task<Result<ConfluencePageList, HttpError>> GetPagesAsync(
        [Query("spaceKey")] string? spaceKey,
        [Query] int limit,
        [Query] string? cursor,
        [Query("expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);

    [Get("/wiki/rest/api/content/search")]
    Task<Result<ConfluencePageList, HttpError>> SearchPagesAsync(
        [Query] string cql,
        [Query] int limit,
        [Query] string? cursor,
        [Query("expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);
}
```

**Step 4: Update `ConfluenceDataProviderExtensions`**

Remove `using Refit;`. Use the generated `AddIConfluenceApi` extension (verify exact name from Step 2):

```csharp
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using ZeroAlloc.Rest.Serialization; // verify exact namespace from package

namespace Rag.NET.DataProviders.Confluence;

public static class ConfluenceDataProviderExtensions
{
    public static IServiceCollection AddConfluenceDataProvider(
        this IServiceCollection services,
        string baseUrl,
        string email,
        string apiToken,
        Action<ConfluenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var opts = new ConfluenceOptions { BaseUrl = baseUrl, Email = email };
        configure?.Invoke(opts);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));

        services.AddIConfluenceApi(options =>  // generated extension — verify name
        {
            options.BaseAddress = new Uri(baseUrl);
            options.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            options.UseSerializer<SystemTextJsonSerializer>(); // verify type name
        }).AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
            new ConfluenceDataProvider(sp.GetRequiredService<IConfluenceApi>(), opts));
    }
}
```

**Step 5: Update `ConfluenceDataProvider`**

Remove `using Refit;`. Update `GetFileHandlesAsync` return type. Unwrap `Result<ConfluencePageList, HttpError>` from each API call. Convert `HttpError` to `RagError.HttpFailed`.

The delta-token stale fallback changes from `catch (ApiException ex) when (ex.StatusCode == BadRequest)` to checking `result.IsFailure && result.Error.StatusCode == HttpStatusCode.BadRequest`:

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Confluence;

public sealed partial class ConfluenceDataProvider : FileContentProviderBase
{
    // ... (GeneratedRegex attributes unchanged) ...

    private readonly IConfluenceApi _api;
    private readonly ConfluenceOptions _options;

    internal ConfluenceDataProvider(IConfluenceApi api, ConfluenceOptions options)
        : base(options) { /* ... unchanged ... */ }

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var result = await _api.GetPagesAsync(
                _options.SpaceKey, limit: 50, cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(result.Error.StatusCode, result.Error.Content));
                yield break;
            }

            var page = result.Value;
            for (int i = 0; i < page.Results.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Result<FileHandle, RagError>.Success(ToHandle(page.Results[i]));
            }

            cursor = ExtractCursor(page.Links.Next);
        }
        while (cursor is not null);
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cql = _options.SpaceKey is not null
            ? $"space=\"{_options.SpaceKey}\" AND lastModified>\"{_options.DeltaToken}\""
            : $"lastModified>\"{_options.DeltaToken}\"";

        var firstResult = await _api.SearchPagesAsync(
            cql, limit: 50, cursor: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Stale delta token — fall back to full traversal
        if (firstResult.IsFailure &&
            firstResult.Error.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            await foreach (var h in GetFullHandlesAsync(cancellationToken).ConfigureAwait(false))
                yield return h;
            yield break;
        }

        if (firstResult.IsFailure)
        {
            yield return Result<FileHandle, RagError>.Failure(
                new RagError.HttpFailed(firstResult.Error.StatusCode, firstResult.Error.Content));
            yield break;
        }

        var firstPage = firstResult.Value;
        string? cursor = ExtractCursor(firstPage.Links.Next);
        for (int i = 0; i < firstPage.Results.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Result<FileHandle, RagError>.Success(ToHandle(firstPage.Results[i]));
        }

        while (cursor is not null)
        {
            var result = await _api.SearchPagesAsync(
                cql, limit: 50, cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(result.Error.StatusCode, result.Error.Content));
                yield break;
            }

            var page = result.Value;
            for (int i = 0; i < page.Results.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Result<FileHandle, RagError>.Success(ToHandle(page.Results[i]));
            }

            cursor = ExtractCursor(page.Links.Next);
        }
    }

    // ToHandle, ToMarkdown, ExtractCursor — unchanged
}
```

**Step 6: Update `ConfluenceDataProviderTests`**

The tests use `FakeStaleDeltaHandler` which returns HTTP 400 — this still works with ZeroAlloc.Rest since it receives the HTTP response and wraps it in `Result.Failure`. No handler changes needed.

The only change: remove any `using Refit;` in the test file. If `ApiException` was used in test assertions, replace with result-based checks. Check if the test file references `ApiException` directly — if so, remove those references.

**Step 7: Build and test**

```
dotnet build src/Rag.NET.DataProviders.Confluence/ --no-incremental
dotnet test tests/Rag.NET.DataProviders.Confluence.Tests/ -v quiet
```

Expected: all tests pass.

**Step 8: Commit**

```
git add src/Rag.NET.DataProviders.Confluence/
git add tests/Rag.NET.DataProviders.Confluence.Tests/
git commit -m "feat(confluence): migrate from Refit to ZeroAlloc.Rest"
```

---

### Task 4: Migrate Jira

**Files:**
- Modify: `src/Rag.NET.DataProviders.Jira/Rag.NET.DataProviders.Jira.csproj`
- Modify: `src/Rag.NET.DataProviders.Jira/IJiraApi.cs`
- Modify: `src/Rag.NET.DataProviders.Jira/JiraDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Jira/JiraDataProvider.cs`
- Modify: `tests/Rag.NET.DataProviders.Jira.Tests/JiraDataProviderTests.cs`

**Step 1: Update csproj** — same swap as Task 3.

**Step 2: Update `IJiraApi`**

```csharp
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Jira;

[ZeroAllocRestClient]
[Headers("Accept: application/json")]
internal interface IJiraApi
{
    [Get("/rest/api/3/search")]
    Task<Result<JiraSearchResult, HttpError>> SearchAsync(
        [Query] string jql,
        [Query] int maxResults,
        [Query] int startAt,
        [Query] string fields = "summary,description,status,priority,assignee,comment,updated",
        CancellationToken cancellationToken = default);
}
```

**Step 3: Update `JiraDataProviderExtensions`** — same pattern as Confluence (Basic auth, `AddIJiraApi`).

**Step 4: Update `JiraDataProvider`**

`GetFileHandlesAsync` now returns `IAsyncEnumerable<Result<FileHandle, RagError>>`. Unwrap each `SearchAsync` result:

```csharp
var result = await _api.SearchAsync(jql, maxResults: 50, startAt: offset,
    cancellationToken: cancellationToken).ConfigureAwait(false);

if (result.IsFailure)
{
    yield return Result<FileHandle, RagError>.Failure(
        new RagError.HttpFailed(result.Error.StatusCode, result.Error.Content));
    yield break;
}

var page = result.Value;
// ... process page.Issues ...
```

**Step 5: Build and test**

```
dotnet build src/Rag.NET.DataProviders.Jira/ --no-incremental
dotnet test tests/Rag.NET.DataProviders.Jira.Tests/ -v quiet
```

**Step 6: Commit**

```
git add src/Rag.NET.DataProviders.Jira/ tests/Rag.NET.DataProviders.Jira.Tests/
git commit -m "feat(jira): migrate from Refit to ZeroAlloc.Rest"
```

---

### Task 5: Migrate Notion

**Files:**
- Modify: `src/Rag.NET.DataProviders.Notion/Rag.NET.DataProviders.Notion.csproj`
- Modify: `src/Rag.NET.DataProviders.Notion/INotionApi.cs`
- Modify: `src/Rag.NET.DataProviders.Notion/NotionDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Notion/NotionDataProvider.cs`
- Modify: `tests/Rag.NET.DataProviders.Notion.Tests/NotionDataProviderTests.cs`

**Step 1: Update csproj** — same swap.

**Step 2: Update `INotionApi`**

```csharp
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Notion;

[ZeroAllocRestClient]
[Headers("Accept: application/json", "Notion-Version: 2022-06-28")]
internal interface INotionApi
{
    [Post("/v1/search")]
    Task<Result<NotionSearchResult, HttpError>> SearchAsync(
        [Body] NotionSearchRequest request,
        CancellationToken cancellationToken = default);

    [Get("/v1/blocks/{blockId}/children")]
    Task<Result<NotionBlockList, HttpError>> GetBlockChildrenAsync(
        string blockId,
        [Query] int page_size = 100,
        [Query] string? start_cursor = null,
        CancellationToken cancellationToken = default);
}
```

**Step 3: Update `NotionDataProviderExtensions`** — Bearer auth, `AddINotionApi`, base URL `https://api.notion.com`.

**Step 4: Update `NotionDataProvider`** — same unwrap pattern. Both `SearchAsync` and `GetBlockChildrenAsync` calls need unwrapping.

**Step 5: Build and test**

```
dotnet build src/Rag.NET.DataProviders.Notion/ --no-incremental
dotnet test tests/Rag.NET.DataProviders.Notion.Tests/ -v quiet
```

**Step 6: Commit**

```
git add src/Rag.NET.DataProviders.Notion/ tests/Rag.NET.DataProviders.Notion.Tests/
git commit -m "feat(notion): migrate from Refit to ZeroAlloc.Rest"
```

---

### Task 6: Migrate Slack

**Files:**
- Modify: `src/Rag.NET.DataProviders.Slack/Rag.NET.DataProviders.Slack.csproj`
- Modify: `src/Rag.NET.DataProviders.Slack/ISlackApi.cs`
- Modify: `src/Rag.NET.DataProviders.Slack/SlackDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Slack/SlackDataProvider.cs`
- Modify: `tests/Rag.NET.DataProviders.Slack.Tests/SlackDataProviderTests.cs`

**Step 1: Update csproj** — same swap.

**Step 2: Update `ISlackApi`**

```csharp
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Slack;

[ZeroAllocRestClient]
[Headers("Accept: application/json")]
internal interface ISlackApi
{
    [Get("/api/conversations.list")]
    Task<Result<SlackChannelList, HttpError>> ListChannelsAsync(
        [Query] int limit = 200,
        [Query] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/conversations.history")]
    Task<Result<SlackMessageList, HttpError>> GetHistoryAsync(
        [Query] string channel,
        [Query] int limit = 200,
        [Query] string? oldest = null,
        [Query] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/conversations.replies")]
    Task<Result<SlackMessageList, HttpError>> GetRepliesAsync(
        [Query] string channel,
        [Query] string ts,
        CancellationToken cancellationToken = default);

    [Get("/api/users.info")]
    Task<Result<SlackUserInfo, HttpError>> GetUserAsync(
        [Query] string user,
        CancellationToken cancellationToken = default);
}
```

**Step 3: Update `SlackDataProviderExtensions`** — Bearer auth (`botToken`), `AddISlackApi`, base URL `https://slack.com`.

**Step 4: Update `SlackDataProvider`** — unwrap each API call result.

**Step 5: Build and test**

```
dotnet build src/Rag.NET.DataProviders.Slack/ --no-incremental
dotnet test tests/Rag.NET.DataProviders.Slack.Tests/ -v quiet
```

**Step 6: Commit**

```
git add src/Rag.NET.DataProviders.Slack/ tests/Rag.NET.DataProviders.Slack.Tests/
git commit -m "feat(slack): migrate from Refit to ZeroAlloc.Rest"
```

---

### Task 7: Migrate Zendesk

Zendesk has **two DI registrations** (`AddZendeskTicketsDataProvider` and `AddZendeskArticlesDataProvider`) but a single `IZendeskApi`.

**Files:**
- Modify: `src/Rag.NET.DataProviders.Zendesk/Rag.NET.DataProviders.Zendesk.csproj`
- Modify: `src/Rag.NET.DataProviders.Zendesk/IZendeskApi.cs`
- Modify: `src/Rag.NET.DataProviders.Zendesk/ZendeskDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Zendesk/ZendeskTicketsDataProvider.cs`
- Modify: `src/Rag.NET.DataProviders.Zendesk/ZendeskArticlesDataProvider.cs`
- Modify: `tests/Rag.NET.DataProviders.Zendesk.Tests/ZendeskDataProviderTests.cs`

**Step 1: Update csproj** — same swap.

**Step 2: Update `IZendeskApi`**

```csharp
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Zendesk;

[ZeroAllocRestClient]
[Headers("Accept: application/json")]
internal interface IZendeskApi
{
    [Get("/api/v2/incremental/tickets/cursor.json")]
    Task<Result<ZendeskIncrementalTicketResult, HttpError>> GetIncrementalTicketsAsync(
        [Query("start_time")] long startTime,
        [Query("cursor")] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/v2/tickets/{ticketId}/comments")]
    Task<Result<ZendeskCommentPage, HttpError>> GetTicketCommentsAsync(
        long ticketId,
        CancellationToken cancellationToken = default);

    [Get("/api/v2/help_center/incremental/articles.json")]
    Task<Result<ZendeskIncrementalArticleResult, HttpError>> GetIncrementalArticlesAsync(
        [Query("start_time")] long startTime,
        CancellationToken cancellationToken = default);
}
```

**Step 3: Update `ZendeskDataProviderExtensions`**

Both `AddZendeskTicketsDataProvider` and `AddZendeskArticlesDataProvider` are in the same file. Each registers its own named `IZendeskApi` client. Since `IZendeskApi` is shared, the generated `AddIZendeskApi` is called twice with different `BaseAddress` settings — which requires two separate named registrations. Check if ZeroAlloc.Rest supports named clients; if not, register two separate HttpClients and resolve them manually (same pattern as the old `IHttpClientFactory` approach, but passing the `HttpClient` to the generated client constructor `new ZendeskApiClient(http)`).

**Step 4: Update both data provider implementations** — same unwrap pattern.

**Step 5: Build and test**

```
dotnet build src/Rag.NET.DataProviders.Zendesk/ --no-incremental
dotnet test tests/Rag.NET.DataProviders.Zendesk.Tests/ -v quiet
```

**Step 6: Commit**

```
git add src/Rag.NET.DataProviders.Zendesk/ tests/Rag.NET.DataProviders.Zendesk.Tests/
git commit -m "feat(zendesk): migrate from Refit to ZeroAlloc.Rest"
```

---

### Task 8: Migrate Bitbucket

Bitbucket has a special case: `GetRawFileAsync` returns `HttpResponseMessage` (not a model type) with a method-level `[Headers("Accept: application/octet-stream")]` override.

**Files:**
- Modify: `src/Rag.NET.DataProviders.Bitbucket/Rag.NET.DataProviders.Bitbucket.csproj`
- Modify: `src/Rag.NET.DataProviders.Bitbucket/IBitbucketApi.cs`
- Modify: `src/Rag.NET.DataProviders.Bitbucket/BitbucketDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Bitbucket/BitbucketDataProvider.cs`
- Modify: `tests/Rag.NET.DataProviders.Bitbucket.Tests/BitbucketDataProviderTests.cs`

**Step 1: Update csproj** — same swap.

**Step 2: Update `IBitbucketApi`**

```csharp
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Bitbucket;

[ZeroAllocRestClient]
[Headers("Accept: application/json")]
internal interface IBitbucketApi
{
    [Get("/repositories/{workspace}/{repo}/src/{commit}/{path}")]
    Task<Result<BitbucketSourcePage, HttpError>> GetSourceAsync(
        string workspace, string repo, string commit, string path,
        [Query] int? pagelen = null,
        [Query] string? page = null,
        CancellationToken cancellationToken = default);

    [Get("/repositories/{workspace}/{repo}/src/{commit}/{path}")]
    [Headers("Accept: application/octet-stream")]
    Task<Result<HttpResponseMessage, HttpError>> GetRawFileAsync(
        string workspace, string repo, string commit, string path,
        CancellationToken cancellationToken = default);

    [Get("/repositories/{workspace}/{repo}/diffstat/{spec}")]
    Task<Result<BitbucketDiffstatPage, HttpError>> GetDiffstatAsync(
        string workspace, string repo, string spec,
        [Query] int? pagelen = null,
        [Query] string? page = null,
        CancellationToken cancellationToken = default);
}
```

**Step 3: Update `BitbucketDataProviderExtensions`** — Basic auth (`username:appPassword`), `AddIBitbucketApi`, base URL `https://api.bitbucket.org/2.0/`.

**Step 4: Update `BitbucketDataProvider`**

For `GetRawFileAsync`, unwrap the `Result<HttpResponseMessage, HttpError>` and read the content stream from the `HttpResponseMessage`:

```csharp
var rawResult = await _api.GetRawFileAsync(
    _options.Workspace, _options.RepoSlug, commit, path,
    cancellationToken: cancellationToken).ConfigureAwait(false);

if (rawResult.IsFailure)
{
    yield return Result<FileHandle, RagError>.Failure(
        new RagError.HttpFailed(rawResult.Error.StatusCode, rawResult.Error.Content));
    yield break;
}

var content = await rawResult.Value.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
```

**Step 5: Build and test**

```
dotnet build src/Rag.NET.DataProviders.Bitbucket/ --no-incremental
dotnet test tests/Rag.NET.DataProviders.Bitbucket.Tests/ -v quiet
```

**Step 6: Commit**

```
git add src/Rag.NET.DataProviders.Bitbucket/ tests/Rag.NET.DataProviders.Bitbucket.Tests/
git commit -m "feat(bitbucket): migrate from Refit to ZeroAlloc.Rest"
```

---

### Task 9: Migrate Asana

Asana has a dynamic Bearer token resolved per-request via `ITokenProvider`. Currently, `AsanaDataProvider` receives a raw `HttpClient`, sets `Authorization` on it before each enumeration, then calls `RestService.For<IAsanaApi>(_http)`. With ZeroAlloc.Rest, token injection moves to a `DelegatingHandler`.

**Files:**
- Modify: `src/Rag.NET.DataProviders.Asana/Rag.NET.DataProviders.Asana.csproj`
- Modify: `src/Rag.NET.DataProviders.Asana/IAsanaApi.cs`
- Modify: `src/Rag.NET.DataProviders.Asana/AsanaDataProviderExtensions.cs`
- Modify: `src/Rag.NET.DataProviders.Asana/AsanaDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Asana/AsanaTokenHandler.cs`
- Modify: `tests/Rag.NET.DataProviders.Asana.Tests/AsanaDataProviderTests.cs`

**Step 1: Update csproj** — same swap.

**Step 2: Update `IAsanaApi`**

```csharp
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Asana;

[ZeroAllocRestClient]
[Headers("Accept: application/json")]
internal interface IAsanaApi
{
    [Get("/api/1.0/tasks")]
    Task<Result<AsanaTaskList, HttpError>> GetWorkspaceTasksAsync(
        [Query] string workspace,
        [Query] string opt_fields,
        [Query] int limit,
        [Query] string? offset = null,
        [Query] string? modified_since = null,
        CancellationToken cancellationToken = default);

    [Get("/api/1.0/projects/{projectGid}/tasks")]
    Task<Result<AsanaTaskList, HttpError>> GetProjectTasksAsync(
        string projectGid,
        [Query] string opt_fields,
        [Query] int limit,
        [Query] string? offset = null,
        [Query] string? modified_since = null,
        CancellationToken cancellationToken = default);

    [Get("/api/1.0/tasks/{taskGid}/subtasks")]
    Task<Result<AsanaTaskList, HttpError>> GetSubtasksAsync(
        string taskGid,
        [Query] string opt_fields = "gid,name",
        CancellationToken cancellationToken = default);
}
```

**Step 3: Create `AsanaTokenHandler`**

This `DelegatingHandler` resolves the token per-request so the generated client stays stateless:

```csharp
using System.Net.Http.Headers;

namespace Rag.NET.DataProviders.Asana;

internal sealed class AsanaTokenHandler(ITokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
```

**Step 4: Update `AsanaDataProviderExtensions`**

Register the token handler as a transient, then chain it on the generated client:

```csharp
services.AddTransient(sp => new AsanaTokenHandler(tokenProvider));

services.AddIAsanaApi(options =>
{
    options.BaseAddress = new Uri("https://app.asana.com");
    options.UseSerializer<SystemTextJsonSerializer>();
})
.AddHttpMessageHandler<AsanaTokenHandler>()
.AddStandardResilienceHandler();

return services.AddSingleton<IFileContentProvider>(sp =>
    new AsanaDataProvider(sp.GetRequiredService<IAsanaApi>(), opts));
```

**Step 5: Update `AsanaDataProvider`**

`AsanaDataProvider` no longer receives `HttpClient` or `ITokenProvider` — it receives `IAsanaApi` directly. Remove the manual `Authorization` header setting. Update constructor and `GetFileHandlesAsync` to unwrap Results:

```csharp
internal AsanaDataProvider(IAsanaApi api, AsanaOptions options)
    : base(options)
{
    _api = api;
    _options = options;
}
```

**Step 6: Build and test**

```
dotnet build src/Rag.NET.DataProviders.Asana/ --no-incremental
dotnet test tests/Rag.NET.DataProviders.Asana.Tests/ -v quiet
```

Note: Asana tests use fake `HttpMessageHandler` subclasses and construct `AsanaDataProvider` directly. The constructor signature has changed (no longer takes `HttpClient`). Update the test constructor calls to pass an `IAsanaApi` instead. You can create a minimal test helper that builds an `IAsanaApi` from a fake handler:

```csharp
private static IAsanaApi MakeApi(HttpMessageHandler handler)
{
    var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.asana.com") };
    return new AsanaApiClient(http); // generated class — verify name
}
```

**Step 7: Commit**

```
git add src/Rag.NET.DataProviders.Asana/ tests/Rag.NET.DataProviders.Asana.Tests/
git commit -m "feat(asana): migrate from Refit to ZeroAlloc.Rest with DelegatingHandler token injection"
```

---

### Task 10: Final Verification

**Step 1: Build entire solution**

```
dotnet build --no-incremental 2>&1 | grep -E "error|warning" | grep -v "^$"
```

Expected: zero errors, only known warnings.

**Step 2: Run all tests**

```
dotnet test -v quiet
```

Expected: all tests pass.

**Step 3: Verify Refit is gone**

```
grep -r "using Refit" src/ tests/
grep -r "RestService.For" src/ tests/
```

Expected: no matches.

**Step 4: Final commit**

```
git commit --allow-empty -m "chore: complete ZeroAlloc.Rest migration — Refit removed from all data providers"
```
