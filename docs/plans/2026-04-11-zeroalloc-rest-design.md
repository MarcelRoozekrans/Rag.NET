# ZeroAlloc.Rest Migration — Design

## Goal

Replace Refit with ZeroAlloc.Rest across all 7 HTTP data providers. Adopt `Result<T, HttpError>` on every API method and propagate typed HTTP errors through `IFileContentProvider` → `RagPipelineExtensions` → `ProviderIngestionResult`, adding `RagError.HttpFailed` as a new error case.

## Scope

7 data provider packages currently on Refit 8.*:

| Package | API interface | Auth |
|---|---|---|
| `Rag.NET.DataProviders.Confluence` | `IConfluenceApi` | Basic |
| `Rag.NET.DataProviders.Jira` | `IJiraApi` | Basic |
| `Rag.NET.DataProviders.Notion` | `INotionApi` | Bearer |
| `Rag.NET.DataProviders.Slack` | `ISlackApi` | Bearer |
| `Rag.NET.DataProviders.Zendesk` | `IZendeskApi` | Basic |
| `Rag.NET.DataProviders.Bitbucket` | `IBitbucketApi` | Basic |
| `Rag.NET.DataProviders.Asana` | `IAsanaApi` | Bearer (dynamic) |

---

## Key Decisions

| Question | Decision |
|---|---|
| Replace Refit on all 7 providers at once or pilot? | Full migration — consistent, no partial state |
| Return `Task<T>` or `Task<Result<T, HttpError>>`? | `Result<T, HttpError>` on all methods |
| Where does `HttpError` surface in the public API? | As `RagError.HttpFailed(HttpError Error)` — new discriminated union case |
| Does `IFileContentProvider` signature change? | Yes — yields `Result<FileEntry, RagError>` |
| Does `ProviderIngestionResult.Errors` change type? | Yes — `IReadOnlyList<string>` → `IReadOnlyList<RagError>` |
| How is resilience (retry/circuit-breaker) preserved? | ZeroAlloc.Rest builder exposes `IHttpClientBuilder`; chain `.AddStandardResilienceHandler()` on it |
| Breaking changes? | Source-breaking on `IFileContentProvider`, `ProviderIngestionResult`, `RagError` — acceptable, not public yet |

---

## 1. NuGet Package Changes

Add to every data provider `.csproj`:
```xml
<PackageReference Include="ZeroAlloc.Rest" Version="0.*" />
<PackageReference Include="ZeroAlloc.Rest.Generator" Version="0.*" PrivateAssets="all" ExcludeAssets="runtime" />
```

Remove from every data provider `.csproj`:
```xml
<PackageReference Include="Refit" Version="8.*" />
<PackageReference Include="Refit.HttpClientFactory" Version="8.*" />
```

---

## 2. Interface Changes

Every API interface gets three changes:
1. Remove `using Refit;` → `using ZeroAlloc.Rest;`
2. Add `[ZeroAllocRestClient]` on the interface
3. All method return types `Task<T>` → `Task<Result<T, HttpError>>`

All `[Get]`, `[Post]`, `[Query]`, `[Body]`, `[Headers]`, `[Path]` attributes carry over unchanged — ZeroAlloc.Rest uses the same names. Bare path parameters (e.g. `string workspace`) work the same.

```csharp
// before
using Refit;

[Headers("Accept: application/json")]
internal interface IConfluenceApi
{
    [Get("/wiki/rest/api/content")]
    Task<ConfluencePageList> GetPagesAsync(
        [Query("spaceKey")] string? spaceKey,
        [Query] int limit,
        [Query] string? cursor,
        [Query("expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);
}

// after
using ZeroAlloc.Rest;

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
}
```

Bitbucket special case: `GetRawFileAsync` returns `Task<HttpResponseMessage>` → `Task<Result<HttpResponseMessage, HttpError>>`.

---

## 3. DI Registration Changes

ZeroAlloc.Rest generates `AddIXxxApi(options)` extension methods per interface. The pattern replaces `RestService.For<T>(http)` inside a singleton factory.

```csharp
// before
services.AddDataProviderHttpClient("Confluence");
services.AddSingleton<IFileContentProvider>(sp =>
{
    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Confluence");
    http.BaseAddress = new Uri(baseUrl);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    return new ConfluenceDataProvider(RestService.For<IConfluenceApi>(http), opts);
});

// after
services.AddIConfluenceApi(options =>
{
    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
    options.BaseAddress = new Uri(baseUrl);
    options.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    options.UseSerializer<SystemTextJsonSerializer>();
}).AddStandardResilienceHandler();

services.AddSingleton<IFileContentProvider>(sp =>
    new ConfluenceDataProvider(sp.GetRequiredService<IConfluenceApi>(), opts));
```

`AddDataProviderHttpClientExtensions.AddDataProviderHttpClient` is no longer called from individual providers — resilience is chained directly on the generated builder. The helper method itself remains for any non-Refit HTTP providers (Web crawler, etc.).

Asana is the only provider with a dynamic token (resolved per-request inside the provider). Its DI registration already sets up a plain `HttpClient` without `RestService.For<T>` — Asana gets its own `IAsanaApi` through the same generated pattern, but token injection moves to a `DelegatingHandler`.

---

## 4. `RagError` — New Case

```csharp
// in RagError.cs
/// <summary>An HTTP call to an external data provider failed.</summary>
public sealed record HttpFailed(HttpError Error) : RagError;
```

---

## 5. `IFileContentProvider` — New Signature

```csharp
// before
public interface IFileContentProvider
{
    IAsyncEnumerable<FileEntry> GetFilesAsync(CancellationToken cancellationToken = default);
}

// after
public interface IFileContentProvider
{
    IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(CancellationToken cancellationToken = default);
}
```

---

## 6. `FileContentProviderBase` — Internal Propagation

`GetFileHandlesAsync` (the abstract method subclasses implement) changes to yield `Result<FileHandle, RagError>`:

```csharp
// before
protected abstract IAsyncEnumerable<FileHandle> GetFileHandlesAsync(CancellationToken cancellationToken);

// after
protected abstract IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(CancellationToken cancellationToken);
```

`GetFilesAsync` propagates failures through unchanged:
```csharp
public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(...)
{
    await foreach (var result in GetFileHandlesAsync(cancellationToken).ConfigureAwait(false))
    {
        if (result.IsFailure) { yield return Result<FileEntry, RagError>.Failure(result.Error); continue; }
        var handle = result.Value;
        if (!MatchesExtension(handle.FileName)) continue;
        if (_options.Filter is not null && !_options.Filter(handle.Id)) continue;
        yield return Result<FileEntry, RagError>.Success(new FileEntry(...));
    }
}
```

---

## 7. Data Provider Implementation Changes

Every provider's `GetFileHandlesAsync` (or equivalent) unwraps `Result<T, HttpError>` from API calls. On failure, yield `Result<FileHandle, RagError>.Failure(new RagError.HttpFailed(error))`.

The Confluence delta-fallback currently catches `ApiException` with `StatusCode == BadRequest`. After migration it checks `result.Error.StatusCode == HttpStatusCode.BadRequest`:

```csharp
// before
catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
{
    staleDeltaToken = true;
}

// after
var result = await _api.SearchPagesAsync(...);
if (result.IsFailure && result.Error.StatusCode == HttpStatusCode.BadRequest)
{
    staleDeltaToken = true;
}
else if (result.IsFailure)
{
    yield return Result<FileHandle, RagError>.Failure(new RagError.HttpFailed(result.Error));
    yield break;
}
```

---

## 8. `ProviderIngestionResult` — Typed Errors

```csharp
// before
public sealed record ProviderIngestionResult(int Ingested, int Skipped, int Deleted, IReadOnlyList<string> Errors);

// after
public sealed record ProviderIngestionResult(int Ingested, int Skipped, int Deleted, IReadOnlyList<RagError> Errors);
```

`RagPipelineExtensions.IngestFromProviderAsync` iterates `Result<FileEntry, RagError>` items — on failure it adds the `RagError` to the errors bag instead of a string message:

```csharp
await foreach (var result in provider.GetFilesAsync(cancellationToken).ConfigureAwait(false))
{
    if (result.IsFailure)
    {
        errors.Add(result.Error);
        continue;
    }
    entries.Add(result.Value);
}
```

---

## 9. Testing

| Area | Change |
|---|---|
| `RagErrorTests` | Add test for `HttpFailed` construction and pattern-match |
| `FileContentProviderBaseTests` | `StubProvider.GetFileHandlesAsync` yields `Result<FileHandle, RagError>`; add test: failure from provider propagates through `GetFilesAsync` |
| `IngestFromProviderTests` | `MakeProvider` helper yields `Result<FileEntry, RagError>`; add test: HTTP failure from provider appears in `ProviderIngestionResult.Errors` as `RagError.HttpFailed` |
| Confluence/Jira/Notion/etc. tests | Mock API return types change from `Task<T>` → `Task<Result<T, HttpError>>`; assertions unchanged |
| `ConfluenceDataProviderTests` | Stale delta-token test: mock returns `Result.Failure(HttpError with BadRequest)` instead of throwing `ApiException` |

All 736 existing tests will compile-fail on signature changes — fixing them proves all callsites migrated.

---

## File Map

```
src/
  Rag.NET.Abstractions/
    Models/RagError.cs                          ← add HttpFailed case
  Rag.NET/
    DataProviders/IFileContentProvider.cs       ← Result<FileEntry, RagError>
    DataProviders/ProviderIngestionResult.cs    ← IReadOnlyList<RagError>
    DataProviders/RagPipelineExtensions.cs      ← iterate Result<FileEntry, RagError>
  Rag.NET.DataProviders/
    FileContentProviderBase.cs                  ← GetFileHandlesAsync returns Result; GetFilesAsync propagates
  Rag.NET.DataProviders.Confluence/
    IConfluenceApi.cs                           ← ZeroAllocRestClient + Result returns
    ConfluenceDataProvider.cs                   ← unwrap Results, new fallback pattern
    ConfluenceDataProviderExtensions.cs         ← AddIConfluenceApi + resilience
    Rag.NET.DataProviders.Confluence.csproj     ← swap Refit → ZeroAlloc.Rest
  Rag.NET.DataProviders.Jira/                   ← same pattern
  Rag.NET.DataProviders.Notion/                 ← same pattern
  Rag.NET.DataProviders.Slack/                  ← same pattern
  Rag.NET.DataProviders.Zendesk/               ← same pattern (2 DI registrations)
  Rag.NET.DataProviders.Bitbucket/             ← same pattern + HttpResponseMessage special case
  Rag.NET.DataProviders.Asana/                 ← same pattern + DelegatingHandler for dynamic token

tests/
  Rag.NET.Tests/
    Models/RagErrorTests.cs                     ← add HttpFailed test
    DataProviders/IngestFromProviderTests.cs    ← update MakeProvider + add HttpFailed test
  Rag.NET.DataProviders.Tests/
    FileContentProviderBaseTests.cs             ← update StubProvider + add failure test
  Rag.NET.DataProviders.Confluence.Tests/      ← update API mocks + delta fallback test
  (+ 6 other provider test projects)
```
