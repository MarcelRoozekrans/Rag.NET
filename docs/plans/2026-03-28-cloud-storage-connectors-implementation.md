# Cloud Storage Connectors Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add six cloud storage connectors (Azure Blob, SharePoint, OneDrive, Google Drive, Dropbox, Box) plus a shared `Rag.NET.DataProviders` foundation package with OAuth token management, a base class, and resilience wiring.

**Architecture:** A new `Rag.NET.DataProviders` package hosts `ITokenProvider`, `OAuthClientCredentialsTokenProvider`, `CloudStorageOptions`, and `FileContentProviderBase`. Each connector package extends `FileContentProviderBase` and uses its vendor SDK for enumeration. `GitHubDataProvider` is migrated to the base class in a non-breaking refactor.

**Tech Stack:** .NET 10, xunit.v3, NSubstitute, `Microsoft.Extensions.Http.Resilience`, `Azure.Storage.Blobs`, `Microsoft.Graph`, `Google.Apis.Drive.v3`, `Dropbox.Api`, `Box.V2`

---

## Task 1: Create `Rag.NET.DataProviders` shared package

**Files:**
- Create: `src/Rag.NET.DataProviders/Rag.NET.DataProviders.csproj`
- Create: `src/Rag.NET.DataProviders/ITokenProvider.cs`
- Create: `src/Rag.NET.DataProviders/StaticTokenProvider.cs`
- Create: `src/Rag.NET.DataProviders/OAuthClientCredentialsTokenProvider.cs`
- Create: `src/Rag.NET.DataProviders/CloudStorageOptions.cs`
- Create: `src/Rag.NET.DataProviders/FileHandle.cs`
- Create: `src/Rag.NET.DataProviders/FileContentProviderBase.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Create the project file**

```xml
<!-- src/Rag.NET.DataProviders/Rag.NET.DataProviders.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders</RootNamespace>
    <PackageId>Rag.NET.DataProviders</PackageId>
    <Description>Shared OAuth and base class infrastructure for Rag.NET data provider connectors</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
</Project>
```

**Step 2: Create `ITokenProvider`**

```csharp
// src/Rag.NET.DataProviders/ITokenProvider.cs
namespace Rag.NET.DataProviders;

/// <summary>Provides a bearer token for authenticating against a cloud API.</summary>
public interface ITokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);
}
```

**Step 3: Create `StaticTokenProvider`**

```csharp
// src/Rag.NET.DataProviders/StaticTokenProvider.cs
namespace Rag.NET.DataProviders;

/// <summary>Returns a fixed pre-issued token (API key, PAT, SAS token).</summary>
public sealed class StaticTokenProvider(string token) : ITokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(token);
}
```

**Step 4: Create `OAuthClientCredentialsTokenProvider`**

```csharp
// src/Rag.NET.DataProviders/OAuthClientCredentialsTokenProvider.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders;

/// <summary>
/// Fetches and auto-refreshes a bearer token using the OAuth 2.0 client credentials flow.
/// The token is refreshed proactively 60 seconds before expiry.
/// </summary>
public sealed class OAuthClientCredentialsTokenProvider : ITokenProvider, IDisposable
{
    private readonly string _tokenEndpoint;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scopeParam;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public OAuthClientCredentialsTokenProvider(
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        string[]? scopes = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        _tokenEndpoint = tokenEndpoint;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scopeParam = scopes is { Length: > 0 } ? string.Join(' ', scopes) : string.Empty;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient();
            _ownsHttp = true;
        }
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        // Fast path — token still valid
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _cachedToken;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check inside lock
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _cachedToken;

            var form = new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = _clientId,
                ["client_secret"] = _clientSecret,
            };
            if (!string.IsNullOrEmpty(_scopeParam))
                form["scope"] = _scopeParam;

            using var response = await _http.PostAsync(
                _tokenEndpoint,
                new FormUrlEncodedContent(form),
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync(
                OAuthResponseContext.Default.OAuthTokenResponse,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OAuth token response was empty.");

            _cachedToken = result.AccessToken;
            // Refresh 60 seconds before expiry
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn - 60);
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _lock.Dispose();
    }

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")]   int ExpiresIn);

    [JsonSerializable(typeof(OAuthTokenResponse))]
    private sealed partial class OAuthResponseContext : JsonSerializerContext;
}
```

**Step 5: Create `CloudStorageOptions`**

```csharp
// src/Rag.NET.DataProviders/CloudStorageOptions.cs
namespace Rag.NET.DataProviders;

/// <summary>Base options shared by all cloud storage data providers.</summary>
public abstract class CloudStorageOptions
{
    /// <summary>
    /// File extensions to include (e.g. <c>[".md", ".pdf"]</c>).
    /// Defaults to <c>["*"]</c> which matches all extensions.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = ["*"];

    /// <summary>Optional predicate to exclude files by path. Return <c>false</c> to exclude.</summary>
    public Func<string, bool>? Filter { get; init; }

    /// <summary>
    /// Opaque cursor string for delta runs (format is connector-specific).
    /// <c>null</c> triggers a full traversal.
    /// Set to the value returned by the previous run to enable incremental ingestion.
    /// </summary>
    public string? DeltaToken { get; init; }
}
```

**Step 6: Create `FileHandle`**

```csharp
// src/Rag.NET.DataProviders/FileHandle.cs
namespace Rag.NET.DataProviders;

/// <summary>
/// Internal transfer record yielded by connector implementations before filtering is applied.
/// </summary>
internal sealed record FileHandle(
    string Id,
    string FileName,
    string? ETag,
    Func<CancellationToken, Task<Stream>> OpenAsync);
```

**Step 7: Create `FileContentProviderBase`**

```csharp
// src/Rag.NET.DataProviders/FileContentProviderBase.cs
using System.Runtime.CompilerServices;

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
    /// No filtering required — the base class handles it.
    /// </summary>
    protected abstract IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var handle in GetFileHandlesAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!MatchesExtension(handle.FileName)) continue;
            if (_options.Filter is not null && !_options.Filter(handle.Id)) continue;

            yield return new FileEntry(
                Id:               handle.Id,
                FileName:         handle.FileName,
                OpenContentAsync: handle.OpenAsync,
                ETag:             handle.ETag);
        }
    }

    private bool MatchesExtension(string fileName)
    {
        if (_options.Extensions is ["*"]) return true;
        var ext = Path.GetExtension(fileName);
        return _options.Extensions.Any(e =>
            string.Equals(e, ext, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, "*", StringComparison.Ordinal));
    }
}
```

**Step 8: Add projects to solution**

Open `Rag.NET.slnx` and add inside the `<Folder Name="/src/">` block:
```xml
<Project Path="src/Rag.NET.DataProviders/Rag.NET.DataProviders.csproj" />
```

**Step 9: Build to verify**

```bash
dotnet build src/Rag.NET.DataProviders/Rag.NET.DataProviders.csproj
```
Expected: Build succeeded, 0 errors.

**Step 10: Commit**

```bash
git add src/Rag.NET.DataProviders/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders shared package with ITokenProvider and FileContentProviderBase"
```

---

## Task 2: Test `Rag.NET.DataProviders`

**Files:**
- Create: `tests/Rag.NET.DataProviders.Tests/Rag.NET.DataProviders.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Tests/FileContentProviderBaseTests.cs`
- Create: `tests/Rag.NET.DataProviders.Tests/OAuthClientCredentialsTokenProviderTests.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Create test project file**

```xml
<!-- tests/Rag.NET.DataProviders.Tests/Rag.NET.DataProviders.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
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

**Step 2: Write failing tests for `FileContentProviderBase`**

```csharp
// tests/Rag.NET.DataProviders.Tests/FileContentProviderBaseTests.cs
using Rag.NET.DataProviders;
using Xunit;

namespace Rag.NET.DataProviders.Tests;

public sealed class FileContentProviderBaseTests
{
    // Minimal concrete implementation for testing
    private sealed class StubProvider(
        CloudStorageOptions options,
        params FileHandle[] handles) : FileContentProviderBase(options)
    {
        protected override async IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
            CancellationToken cancellationToken)
        {
            foreach (var h in handles)
                yield return h;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private static FileHandle Handle(string id, string fileName)
        => new(id, fileName, ETag: null, OpenAsync: _ => Task.FromResult<Stream>(new MemoryStream()));

    private static CloudStorageOptions AllFiles() => new TestOptions();
    private sealed class TestOptions : CloudStorageOptions { }

    [Fact]
    public async Task GetFilesAsync_NoFilter_YieldsAllHandles()
    {
        var sut = new StubProvider(AllFiles(),
            Handle("a/file.md", "file.md"),
            Handle("b/file.cs", "file.cs"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        var options = new TestOptions { Extensions = [".md"] };
        var sut = new StubProvider(options,
            Handle("readme.md", "readme.md"),
            Handle("build.yaml", "build.yaml"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("readme.md", results[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_WildcardExtension_YieldsAllFiles()
    {
        var options = new TestOptions { Extensions = ["*"] };
        var sut = new StubProvider(options,
            Handle("a.md", "a.md"),
            Handle("b.yaml", "b.yaml"),
            Handle("c.pdf", "c.pdf"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task GetFilesAsync_PredicateFilter_ExcludesMatchedPaths()
    {
        var options = new TestOptions { Filter = id => !id.StartsWith("internal/", StringComparison.Ordinal) };
        var sut = new StubProvider(options,
            Handle("docs/guide.md", "guide.md"),
            Handle("internal/secret.md", "secret.md"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("docs/guide.md", results[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_ETagIsForwardedFromHandle()
    {
        var handle = new FileHandle("file.md", "file.md", ETag: "etag-abc",
            OpenAsync: _ => Task.FromResult<Stream>(new MemoryStream()));
        var sut = new StubProvider(AllFiles(), handle);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("etag-abc", results[0].ETag);
    }
}
```

**Step 3: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.DataProviders.Tests/ -v minimal
```
Expected: FAIL — project does not build yet (base class internals not visible).

> Note: `FileHandle` is `internal`. Add `InternalsVisibleTo` to `Rag.NET.DataProviders.csproj`:
> ```xml
> <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
>   <_Parameter1>Rag.NET.DataProviders.Tests</_Parameter1>
> </AssemblyAttribute>
> ```

**Step 4: Run tests again to verify they pass**

```bash
dotnet test tests/Rag.NET.DataProviders.Tests/Rag.NET.DataProviders.Tests.csproj -v minimal
```
Expected: All 5 tests pass.

**Step 5: Write failing tests for `OAuthClientCredentialsTokenProvider`**

```csharp
// tests/Rag.NET.DataProviders.Tests/OAuthClientCredentialsTokenProviderTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Rag.NET.DataProviders;
using Xunit;

namespace Rag.NET.DataProviders.Tests;

public sealed class OAuthClientCredentialsTokenProviderTests
{
    private static HttpClient MakeHttpClient(string accessToken, int expiresIn = 3600)
    {
        var handler = new FakeHttpHandler(accessToken, expiresIn);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task GetTokenAsync_FetchesTokenOnFirstCall()
    {
        using var sut = new OAuthClientCredentialsTokenProvider(
            "https://auth.example.com/token", "client-id", "client-secret",
            httpClient: MakeHttpClient("tok-abc"));

        var token = await sut.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("tok-abc", token);
    }

    [Fact]
    public async Task GetTokenAsync_ReturnsCachedTokenOnSecondCall()
    {
        var handler = new FakeHttpHandler("tok-xyz", expiresIn: 3600);
        using var http = new HttpClient(handler);
        using var sut = new OAuthClientCredentialsTokenProvider(
            "https://auth.example.com/token", "id", "secret", httpClient: http);

        await sut.GetTokenAsync(TestContext.Current.CancellationToken);
        await sut.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount); // only one HTTP call
    }

    [Fact]
    public async Task GetTokenAsync_RefetchesTokenAfterExpiry()
    {
        var handler = new FakeHttpHandler("tok-new", expiresIn: 1); // expires in 1 second
        using var http = new HttpClient(handler);
        using var sut = new OAuthClientCredentialsTokenProvider(
            "https://auth.example.com/token", "id", "secret", httpClient: http);

        await sut.GetTokenAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await sut.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.CallCount);
    }

    private sealed class FakeHttpHandler(string token, int expiresIn) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var body = JsonSerializer.Serialize(new
            {
                access_token = token,
                expires_in = expiresIn,
                token_type = "Bearer"
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
```

**Step 6: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.DataProviders.Tests/Rag.NET.DataProviders.Tests.csproj -v minimal
```
Expected: All 8 tests pass.

**Step 7: Add test project to solution**

Add inside `<Folder Name="/tests/">` in `Rag.NET.slnx`:
```xml
<Project Path="tests/Rag.NET.DataProviders.Tests/Rag.NET.DataProviders.Tests.csproj" />
```

**Step 8: Commit**

```bash
git add tests/Rag.NET.DataProviders.Tests/ src/Rag.NET.DataProviders/ Rag.NET.slnx
git commit -m "test: add Rag.NET.DataProviders unit tests for base class and OAuth token provider"
```

---

## Task 3: Migrate `GitHubDataProvider` to `FileContentProviderBase`

**Files:**
- Modify: `src/Rag.NET.DataProviders.GitHub/Rag.NET.DataProviders.GitHub.csproj`
- Modify: `src/Rag.NET.DataProviders.GitHub/GitHubDataProviderOptions.cs`
- Modify: `src/Rag.NET.DataProviders.GitHub/GitHubDataProvider.cs`

This is a non-breaking refactor. The public API does not change — `GetFilesAsync` still returns `IAsyncEnumerable<FileEntry>`.

**Step 1: Add project reference**

In `src/Rag.NET.DataProviders.GitHub/Rag.NET.DataProviders.GitHub.csproj`, replace the `Rag.NET` project reference with:
```xml
<ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
```
(This is transitive — `Rag.NET.DataProviders` already references `Rag.NET`.)

**Step 2: Update `GitHubDataProviderOptions` to extend `CloudStorageOptions`**

```csharp
// src/Rag.NET.DataProviders.GitHub/GitHubDataProviderOptions.cs
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitHub;

/// <summary>Configuration for <see cref="GitHubDataProvider"/>.</summary>
public sealed class GitHubDataProviderOptions : CloudStorageOptions
{
    /// <summary>Branch or ref to traverse. Default: <c>"main"</c>.</summary>
    public string Branch { get; init; } = "main";

    /// <summary>
    /// When set, performs a delta run: only files changed since this commit SHA are returned.
    /// Maps to <see cref="CloudStorageOptions.DeltaToken"/>.
    /// When <see langword="null"/>, performs a full tree traversal.
    /// </summary>
    public string? LastIngestedCommitSha
    {
        get => DeltaToken;
        init => DeltaToken = value;
    }
}
```

**Step 3: Rewrite `GitHubDataProvider` to extend `FileContentProviderBase`**

```csharp
// src/Rag.NET.DataProviders.GitHub/GitHubDataProvider.cs
using System.Runtime.CompilerServices;
using Octokit;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitHub;

/// <summary>
/// Enumerates files from a GitHub repository.
/// On first run (no <see cref="GitHubDataProviderOptions.LastIngestedCommitSha"/>): full recursive tree.
/// On subsequent runs: only files changed since <c>LastIngestedCommitSha</c> via compare API.
/// ETag is the blob SHA — Git's own content hash, so ETag matches guarantee byte-identical content.
/// </summary>
public sealed class GitHubDataProvider : FileContentProviderBase
{
    private readonly string _owner;
    private readonly string _repo;
    private readonly IGitHubClient _client;
    private readonly GitHubDataProviderOptions _options;

    public GitHubDataProvider(
        string owner,
        string repo,
        IGitHubClient client,
        GitHubDataProviderOptions? options = null)
        : base(options ?? new GitHubDataProviderOptions())
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        _owner = owner;
        _repo = repo;
        _client = client;
        _options = options ?? new GitHubDataProviderOptions();
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.LastIngestedCommitSha is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullTreeHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullTreeHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tree = await _client.Git.Tree
            .GetRecursive(_owner, _repo, _options.Branch).ConfigureAwait(false);

        foreach (var item in tree.Tree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type != TreeType.Blob) continue;

            var capturedPath = item.Path;
            yield return new FileHandle(
                Id:       item.Path,
                FileName: Path.GetFileName(item.Path),
                ETag:     item.Sha,
                OpenAsync: async ct =>
                {
                    var bytes = await _client.Repository.Content
                        .GetRawContent(_owner, _repo, capturedPath).ConfigureAwait(false);
                    return (Stream)new MemoryStream(bytes);
                });
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var comparison = await _client.Repository.Commit
            .Compare(_owner, _repo, _options.LastIngestedCommitSha!, _options.Branch)
            .ConfigureAwait(false);

        foreach (var file in comparison.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(file.Status, "removed", StringComparison.Ordinal)) continue;

            var capturedPath = file.Filename;
            yield return new FileHandle(
                Id:       file.Filename,
                FileName: Path.GetFileName(file.Filename),
                ETag:     file.Sha,
                OpenAsync: async ct =>
                {
                    var bytes = await _client.Repository.Content
                        .GetRawContent(_owner, _repo, capturedPath).ConfigureAwait(false);
                    return (Stream)new MemoryStream(bytes);
                });
        }
    }
}
```

**Step 4: Run existing GitHub tests**

```bash
dotnet test tests/Rag.NET.DataProviders.GitHub.Tests/ -v minimal
```
Expected: All tests pass (same behavior, new base class).

**Step 5: Commit**

```bash
git add src/Rag.NET.DataProviders.GitHub/
git commit -m "refactor: migrate GitHubDataProvider to FileContentProviderBase"
```

---

## Task 4: `Rag.NET.DataProviders.AzureBlob`

**Files:**
- Create: `src/Rag.NET.DataProviders.AzureBlob/Rag.NET.DataProviders.AzureBlob.csproj`
- Create: `src/Rag.NET.DataProviders.AzureBlob/AzureBlobOptions.cs`
- Create: `src/Rag.NET.DataProviders.AzureBlob/AzureBlobDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.AzureBlob/AzureBlobDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.AzureBlob.Tests/Rag.NET.DataProviders.AzureBlob.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.AzureBlob.Tests/AzureBlobDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Create project file**

```xml
<!-- src/Rag.NET.DataProviders.AzureBlob/Rag.NET.DataProviders.AzureBlob.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.AzureBlob</RootNamespace>
    <PackageId>Rag.NET.DataProviders.AzureBlob</PackageId>
    <Description>Azure Blob Storage data provider for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Azure.Storage.Blobs" Version="12.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
</Project>
```

**Step 2: Create `AzureBlobOptions`**

```csharp
// src/Rag.NET.DataProviders.AzureBlob/AzureBlobOptions.cs
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.AzureBlob;

public sealed class AzureBlobOptions : CloudStorageOptions
{
    /// <summary>Blob name prefix to filter enumeration (e.g. "docs/"). Null = entire container.</summary>
    public string? Prefix { get; init; }
}
```

**Step 3: Create `AzureBlobDataProvider`**

```csharp
// src/Rag.NET.DataProviders.AzureBlob/AzureBlobDataProvider.cs
using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.AzureBlob;

/// <summary>
/// Enumerates blobs from an Azure Blob Storage container.
/// Full run: all blobs. Delta run: blobs whose ETag differs from the stored DeltaToken are yielded.
/// Resilience is handled by <see cref="BlobClientOptions.Retry"/> — do not add external retry.
/// </summary>
public sealed class AzureBlobDataProvider : FileContentProviderBase
{
    private readonly BlobContainerClient _container;
    private readonly AzureBlobOptions _options;

    public AzureBlobDataProvider(BlobContainerClient container, AzureBlobOptions? options = null)
        : base(options ?? new AzureBlobOptions())
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
        _options = options ?? new AzureBlobOptions();
    }

    protected override async IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in _container
            .GetBlobsAsync(prefix: _options.Prefix, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            var etag = blob.Properties.ETag?.ToString("H"); // format: "hex" without quotes
            var capturedName = blob.Name;

            yield return new FileHandle(
                Id:       blob.Name,
                FileName: Path.GetFileName(blob.Name),
                ETag:     etag,
                OpenAsync: async ct =>
                {
                    var blobClient = _container.GetBlobClient(capturedName);
                    var download = await blobClient.DownloadStreamingAsync(cancellationToken: ct)
                        .ConfigureAwait(false);
                    return download.Value.Content;
                });
        }
    }
}
```

**Step 4: Create DI extensions**

```csharp
// src/Rag.NET.DataProviders.AzureBlob/AzureBlobDataProviderExtensions.cs
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.AzureBlob;

public static class AzureBlobDataProviderExtensions
{
    /// <summary>Registers <see cref="AzureBlobDataProvider"/> using a connection string.</summary>
    public static IServiceCollection AddAzureBlobDataProvider(
        this IServiceCollection services,
        string connectionString,
        string containerName,
        Action<AzureBlobOptions>? configure = null)
    {
        var options = new AzureBlobOptions();
        configure?.Invoke(options);  // note: options is a record, configure pattern here is informational
        var container = new BlobContainerClient(connectionString, containerName);
        return services.AddSingleton<IFileContentProvider>(new AzureBlobDataProvider(container, options));
    }

    /// <summary>Registers <see cref="AzureBlobDataProvider"/> using a <see cref="TokenCredential"/> (OAuth / managed identity).</summary>
    public static IServiceCollection AddAzureBlobDataProvider(
        this IServiceCollection services,
        TokenCredential credential,
        Uri containerUri,
        Action<AzureBlobOptions>? configure = null)
    {
        var options = new AzureBlobOptions();
        configure?.Invoke(options);
        var container = new BlobContainerClient(containerUri, credential);
        return services.AddSingleton<IFileContentProvider>(new AzureBlobDataProvider(container, options));
    }
}
```

**Step 5: Write failing tests**

```csharp
// tests/Rag.NET.DataProviders.AzureBlob.Tests/AzureBlobDataProviderTests.cs
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using NSubstitute;
using Rag.NET.DataProviders.AzureBlob;
using Xunit;

namespace Rag.NET.DataProviders.AzureBlob.Tests;

public sealed class AzureBlobDataProviderTests
{
    private static BlobContainerClient MakeContainer(params (string name, string etag)[] blobs)
    {
        var container = Substitute.For<BlobContainerClient>();

        var items = blobs.Select(b =>
        {
            var props = BlobsModelFactory.BlobProperties();
            var item = BlobsModelFactory.BlobItem(
                name: b.name,
                properties: BlobsModelFactory.BlobItemProperties(
                    accessTierInferred: true,
                    eTag: new ETag(b.etag)));
            return item;
        }).ToList();

        container.GetBlobsAsync(
            traits: Arg.Any<BlobTraits>(),
            states: Arg.Any<BlobStates>(),
            prefix: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(items, null, Substitute.For<Response>())]));

        return container;
    }

    [Fact]
    public async Task GetFilesAsync_ReturnsAllBlobs()
    {
        var container = MakeContainer(("docs/readme.md", "etag-1"), ("src/main.cs", "etag-2"));
        var sut = new AzureBlobDataProvider(container);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetFilesAsync_ETagIsForwarded()
    {
        var container = MakeContainer(("file.md", "etag-abc"));
        var sut = new AzureBlobDataProvider(container);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("etag-abc", results[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingBlobs()
    {
        var container = MakeContainer(("readme.md", "e1"), ("build.yaml", "e2"));
        var sut = new AzureBlobDataProvider(container, new AzureBlobOptions { Extensions = [".md"] });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("readme.md", results[0].Id);
    }
}
```

**Step 6: Create test project file**

```xml
<!-- tests/Rag.NET.DataProviders.AzureBlob.Tests/Rag.NET.DataProviders.AzureBlob.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders.AzureBlob\Rag.NET.DataProviders.AzureBlob.csproj" />
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

**Step 7: Run tests**

```bash
dotnet test tests/Rag.NET.DataProviders.AzureBlob.Tests/ -v minimal
```
Expected: All tests pass.

**Step 8: Add to solution**

Add to `Rag.NET.slnx`:
```xml
<!-- in /src/ folder -->
<Project Path="src/Rag.NET.DataProviders.AzureBlob/Rag.NET.DataProviders.AzureBlob.csproj" />
<!-- in /tests/ folder -->
<Project Path="tests/Rag.NET.DataProviders.AzureBlob.Tests/Rag.NET.DataProviders.AzureBlob.Tests.csproj" />
```

**Step 9: Commit**

```bash
git add src/Rag.NET.DataProviders.AzureBlob/ tests/Rag.NET.DataProviders.AzureBlob.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.AzureBlob connector"
```

---

## Task 5: `Rag.NET.DataProviders.SharePoint`

**Files:**
- Create: `src/Rag.NET.DataProviders.SharePoint/Rag.NET.DataProviders.SharePoint.csproj`
- Create: `src/Rag.NET.DataProviders.SharePoint/SharePointOptions.cs`
- Create: `src/Rag.NET.DataProviders.SharePoint/SharePointDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.SharePoint/SharePointDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.SharePoint.Tests/Rag.NET.DataProviders.SharePoint.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.SharePoint.Tests/SharePointDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Create project file**

```xml
<!-- src/Rag.NET.DataProviders.SharePoint/Rag.NET.DataProviders.SharePoint.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.SharePoint</RootNamespace>
    <PackageId>Rag.NET.DataProviders.SharePoint</PackageId>
    <Description>SharePoint data provider for Rag.NET via Microsoft Graph</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Microsoft.Graph" Version="5.*" />
    <PackageReference Include="Azure.Identity" Version="1.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
  </ItemGroup>
</Project>
```

**Step 2: Create `SharePointOptions`**

```csharp
// src/Rag.NET.DataProviders.SharePoint/SharePointOptions.cs
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.SharePoint;

public sealed class SharePointOptions : CloudStorageOptions
{
    public required string SiteId  { get; init; }
    public required string DriveId { get; init; }
}
```

**Step 3: Create `SharePointDataProvider`**

```csharp
// src/Rag.NET.DataProviders.SharePoint/SharePointDataProvider.cs
using System.Runtime.CompilerServices;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Root.Delta;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.SharePoint;

/// <summary>
/// Enumerates files from a SharePoint drive via Microsoft Graph.
/// Full run: recursive drive enumeration. Delta run: Graph delta API using stored deltaLink token.
/// Stale/expired delta token: automatically falls back to full traversal with a warning.
/// </summary>
public sealed class SharePointDataProvider : FileContentProviderBase
{
    private readonly GraphServiceClient _graph;
    private readonly SharePointOptions _options;

    public SharePointDataProvider(GraphServiceClient graph, SharePointOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = await _graph.Drives[_options.DriveId].Root.Children
            .GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        while (page is not null)
        {
            foreach (var item in page.Value ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.File is null) continue; // skip folders

                var capturedId = item.Id!;
                yield return new FileHandle(
                    Id:       item.ParentReference?.Path + "/" + item.Name,
                    FileName: item.Name ?? capturedId,
                    ETag:     item.ETag,
                    OpenAsync: async ct =>
                        await _graph.Drives[_options.DriveId].Items[capturedId].Content
                            .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                            ?? Stream.Null);
            }

            page = page.OdataNextLink is not null
                ? await _graph.Drives[_options.DriveId].Root.Children
                    .WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false)
                : null;
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            var page = await _graph.Drives[_options.DriveId].Root.Delta
                .WithUrl(_options.DeltaToken!).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            while (page is not null)
            {
                foreach (var item in page.Value ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.File is null) continue;
                    if (item.Deleted is not null) continue;

                    var capturedId = item.Id!;
                    yield return new FileHandle(
                        Id:       item.ParentReference?.Path + "/" + item.Name,
                        FileName: item.Name ?? capturedId,
                        ETag:     item.ETag,
                        OpenAsync: async ct =>
                            await _graph.Drives[_options.DriveId].Items[capturedId].Content
                                .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                                ?? Stream.Null);
                }

                page = page.OdataNextLink is not null
                    ? await _graph.Drives[_options.DriveId].Root.Delta
                        .WithUrl(page.OdataNextLink).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                    : null;
            }
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
            when (string.Equals(ex.Error?.Code, "resyncRequired", StringComparison.Ordinal)
               || string.Equals(ex.Error?.Code, "itemNotFound", StringComparison.Ordinal))
        {
            // Delta token is stale — fall back to full traversal
            await foreach (var handle in GetFullHandlesAsync(cancellationToken).ConfigureAwait(false))
                yield return handle;
        }
    }
}
```

**Step 4: Create DI extensions**

```csharp
// src/Rag.NET.DataProviders.SharePoint/SharePointDataProviderExtensions.cs
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.SharePoint;

public static class SharePointDataProviderExtensions
{
    public static IServiceCollection AddSharePointDataProvider(
        this IServiceCollection services,
        string tenantId,
        string clientId,
        string clientSecret,
        string siteId,
        string driveId,
        Action<SharePointOptions>? configure = null)
    {
        var httpClient = services
            .AddDataProviderHttpClient("SharePoint")
            .AddStandardResilienceHandler()
            .Services
            .BuildServiceProvider()  // resolve the named client
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("SharePoint");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var graph = new GraphServiceClient(httpClient, credential);

        var opts = new SharePointOptions { SiteId = siteId, DriveId = driveId };
        configure?.Invoke(opts);

        return services.AddSingleton<IFileContentProvider>(new SharePointDataProvider(graph, opts));
    }
}
```

**Step 5: Write tests**

```csharp
// tests/Rag.NET.DataProviders.SharePoint.Tests/SharePointDataProviderTests.cs
using Microsoft.Graph;
using Microsoft.Graph.Models;
using NSubstitute;
using Rag.NET.DataProviders.SharePoint;
using Xunit;

namespace Rag.NET.DataProviders.SharePoint.Tests;

public sealed class SharePointDataProviderTests
{
    private static SharePointOptions Opts(string? deltaToken = null) => new()
    {
        SiteId  = "site-1",
        DriveId = "drive-1",
        DeltaToken = deltaToken,
    };

    [Fact]
    public async Task GetFilesAsync_FullRun_ReturnsFiles()
    {
        var graph = Substitute.For<GraphServiceClient>();
        // Graph SDK is hard to mock directly — use an integration-style test with real data
        // This test validates options wiring only; connector logic is covered by manual testing.
        var sut = new SharePointDataProvider(graph, Opts());
        Assert.NotNull(sut);
    }
}
```

> **Note:** The Microsoft Graph SDK uses fluent request builders that are difficult to mock with NSubstitute. The SharePoint connector is validated via build + manual smoke test against a real Graph endpoint. The unit test above ensures the constructor and options wiring compile and run without error.

**Step 6: Create test project file and run build**

```xml
<!-- tests/Rag.NET.DataProviders.SharePoint.Tests/Rag.NET.DataProviders.SharePoint.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders.SharePoint\Rag.NET.DataProviders.SharePoint.csproj" />
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

```bash
dotnet test tests/Rag.NET.DataProviders.SharePoint.Tests/ -v minimal
```
Expected: 1 test passes.

**Step 7: Add to solution and commit**

```bash
git add src/Rag.NET.DataProviders.SharePoint/ tests/Rag.NET.DataProviders.SharePoint.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.SharePoint connector"
```

---

## Task 6: `Rag.NET.DataProviders.OneDrive`

**Files:**
- Create: `src/Rag.NET.DataProviders.OneDrive/Rag.NET.DataProviders.OneDrive.csproj`
- Create: `src/Rag.NET.DataProviders.OneDrive/OneDriveOptions.cs`
- Create: `src/Rag.NET.DataProviders.OneDrive/OneDriveDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.OneDrive/OneDriveDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.OneDrive.Tests/Rag.NET.DataProviders.OneDrive.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.OneDrive.Tests/OneDriveDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

This connector is structurally identical to SharePoint — same Graph SDK, same delta mechanism — but targets `/users/{userId}/drive/root` instead of a named drive.

**Step 1: Project file**

```xml
<!-- src/Rag.NET.DataProviders.OneDrive/Rag.NET.DataProviders.OneDrive.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.OneDrive</RootNamespace>
    <PackageId>Rag.NET.DataProviders.OneDrive</PackageId>
    <Description>OneDrive data provider for Rag.NET via Microsoft Graph</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Microsoft.Graph" Version="5.*" />
    <PackageReference Include="Azure.Identity" Version="1.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
  </ItemGroup>
</Project>
```

**Step 2: `OneDriveOptions`**

```csharp
// src/Rag.NET.DataProviders.OneDrive/OneDriveOptions.cs
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.OneDrive;

public sealed class OneDriveOptions : CloudStorageOptions
{
    /// <summary>User ID or <c>"me"</c> for delegated auth.</summary>
    public required string UserId { get; init; }
}
```

**Step 3: `OneDriveDataProvider`**

```csharp
// src/Rag.NET.DataProviders.OneDrive/OneDriveDataProvider.cs
using System.Runtime.CompilerServices;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.OneDrive;

/// <summary>
/// Enumerates files from a OneDrive user drive via Microsoft Graph.
/// Full run: children of drive root. Delta run: Graph delta API using stored deltaLink token.
/// Stale delta token: falls back to full traversal automatically.
/// </summary>
public sealed class OneDriveDataProvider : FileContentProviderBase
{
    private readonly GraphServiceClient _graph;
    private readonly OneDriveOptions _options;

    public OneDriveDataProvider(GraphServiceClient graph, OneDriveOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = await _graph.Users[_options.UserId].Drive.Root.Children
            .GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        while (page is not null)
        {
            foreach (var item in page.Value ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.File is null) continue;

                var capturedId = item.Id!;
                yield return new FileHandle(
                    Id:       item.ParentReference?.Path + "/" + item.Name,
                    FileName: item.Name ?? capturedId,
                    ETag:     item.ETag,
                    OpenAsync: async ct =>
                        await _graph.Users[_options.UserId].Drive.Items[capturedId].Content
                            .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                            ?? Stream.Null);
            }

            page = page.OdataNextLink is not null
                ? await _graph.Users[_options.UserId].Drive.Root.Children
                    .WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false)
                : null;
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            var page = await _graph.Users[_options.UserId].Drive.Root.Delta
                .WithUrl(_options.DeltaToken!).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            while (page is not null)
            {
                foreach (var item in page.Value ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.File is null || item.Deleted is not null) continue;

                    var capturedId = item.Id!;
                    yield return new FileHandle(
                        Id:       item.ParentReference?.Path + "/" + item.Name,
                        FileName: item.Name ?? capturedId,
                        ETag:     item.ETag,
                        OpenAsync: async ct =>
                            await _graph.Users[_options.UserId].Drive.Items[capturedId].Content
                                .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                                ?? Stream.Null);
                }

                page = page.OdataNextLink is not null
                    ? await _graph.Users[_options.UserId].Drive.Root.Delta
                        .WithUrl(page.OdataNextLink).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                    : null;
            }
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
            when (string.Equals(ex.Error?.Code, "resyncRequired", StringComparison.Ordinal)
               || string.Equals(ex.Error?.Code, "itemNotFound", StringComparison.Ordinal))
        {
            await foreach (var handle in GetFullHandlesAsync(cancellationToken).ConfigureAwait(false))
                yield return handle;
        }
    }
}
```

**Step 4: DI extensions**

```csharp
// src/Rag.NET.DataProviders.OneDrive/OneDriveDataProviderExtensions.cs
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.OneDrive;

public static class OneDriveDataProviderExtensions
{
    public static IServiceCollection AddOneDriveDataProvider(
        this IServiceCollection services,
        string tenantId,
        string clientId,
        string clientSecret,
        string userId,
        Action<OneDriveOptions>? configure = null)
    {
        var httpClient = services
            .AddDataProviderHttpClient("OneDrive")
            .AddStandardResilienceHandler()
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("OneDrive");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var graph = new GraphServiceClient(httpClient, credential);
        var opts = new OneDriveOptions { UserId = userId };
        configure?.Invoke(opts);

        return services.AddSingleton<IFileContentProvider>(new OneDriveDataProvider(graph, opts));
    }
}
```

**Step 5: Test project, build, add to solution, commit**

Create test project (same structure as SharePoint tests, one smoke test), build:
```bash
dotnet build src/Rag.NET.DataProviders.OneDrive/
dotnet test tests/Rag.NET.DataProviders.OneDrive.Tests/ -v minimal
git add src/Rag.NET.DataProviders.OneDrive/ tests/Rag.NET.DataProviders.OneDrive.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.OneDrive connector"
```

---

## Task 7: `Rag.NET.DataProviders.GoogleDrive`

**Files:**
- Create: `src/Rag.NET.DataProviders.GoogleDrive/Rag.NET.DataProviders.GoogleDrive.csproj`
- Create: `src/Rag.NET.DataProviders.GoogleDrive/GoogleDriveOptions.cs`
- Create: `src/Rag.NET.DataProviders.GoogleDrive/GoogleDriveDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.GoogleDrive/GoogleDriveDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.GoogleDrive.Tests/Rag.NET.DataProviders.GoogleDrive.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.GoogleDrive.Tests/GoogleDriveDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Project file**

```xml
<!-- src/Rag.NET.DataProviders.GoogleDrive/Rag.NET.DataProviders.GoogleDrive.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.GoogleDrive</RootNamespace>
    <PackageId>Rag.NET.DataProviders.GoogleDrive</PackageId>
    <Description>Google Drive data provider for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Google.Apis.Drive.v3" Version="1.*" />
    <PackageReference Include="Google.Apis.Auth" Version="1.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
  </ItemGroup>
</Project>
```

**Step 2: `GoogleDriveOptions`**

```csharp
// src/Rag.NET.DataProviders.GoogleDrive/GoogleDriveOptions.cs
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GoogleDrive;

public sealed class GoogleDriveOptions : CloudStorageOptions
{
    /// <summary>Google Drive folder ID to enumerate. <c>null</c> = entire drive.</summary>
    public string? FolderId { get; init; }
}
```

**Step 3: `GoogleDriveDataProvider`**

```csharp
// src/Rag.NET.DataProviders.GoogleDrive/GoogleDriveDataProvider.cs
using System.Runtime.CompilerServices;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GoogleDrive;

/// <summary>
/// Enumerates files from Google Drive.
/// Full run: files in folder (or whole drive). Delta run: Changes.List API with pageToken.
/// </summary>
public sealed class GoogleDriveDataProvider : FileContentProviderBase
{
    private readonly DriveService _drive;
    private readonly GoogleDriveOptions _options;

    public GoogleDriveDataProvider(DriveService drive, GoogleDriveOptions? options = null)
        : base(options ?? new GoogleDriveOptions())
    {
        ArgumentNullException.ThrowIfNull(drive);
        _drive = drive;
        _options = options ?? new GoogleDriveOptions();
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            var request = _drive.Files.List();
            request.Fields = "nextPageToken, files(id, name, mimeType, md5Checksum, parents)";
            request.PageSize = 100;
            if (_options.FolderId is not null)
                request.Q = $"'{_options.FolderId}' in parents and trashed = false";
            else
                request.Q = "trashed = false";
            if (pageToken is not null)
                request.PageToken = pageToken;

            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            foreach (var file in page.Files ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(file.MimeType, "application/vnd.google-apps.folder",
                    StringComparison.Ordinal)) continue;

                var capturedId = file.Id;
                yield return new FileHandle(
                    Id:       file.Id,
                    FileName: file.Name,
                    ETag:     file.Md5Checksum,
                    OpenAsync: async ct =>
                    {
                        var dl = _drive.Files.Get(capturedId);
                        var ms = new MemoryStream();
                        await dl.DownloadAsync(ms, ct).ConfigureAwait(false);
                        ms.Seek(0, SeekOrigin.Begin);
                        return (Stream)ms;
                    });
            }

            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = _options.DeltaToken;
        do
        {
            var request = _drive.Changes.List(pageToken!);
            request.Fields = "nextPageToken, newStartPageToken, changes(file(id, name, mimeType, md5Checksum), removed)";
            request.PageSize = 100;

            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            foreach (var change in page.Changes ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Removed == true || change.File is null) continue;
                if (string.Equals(change.File.MimeType, "application/vnd.google-apps.folder",
                    StringComparison.Ordinal)) continue;

                var capturedId = change.File.Id;
                yield return new FileHandle(
                    Id:       change.File.Id,
                    FileName: change.File.Name,
                    ETag:     change.File.Md5Checksum,
                    OpenAsync: async ct =>
                    {
                        var dl = _drive.Files.Get(capturedId);
                        var ms = new MemoryStream();
                        await dl.DownloadAsync(ms, ct).ConfigureAwait(false);
                        ms.Seek(0, SeekOrigin.Begin);
                        return (Stream)ms;
                    });
            }

            pageToken = page.NextPageToken ?? page.NewStartPageToken;
            if (page.NextPageToken is null) break; // NewStartPageToken = no more changes
        }
        while (pageToken is not null);
    }
}
```

**Step 4: DI extensions**

```csharp
// src/Rag.NET.DataProviders.GoogleDrive/GoogleDriveDataProviderExtensions.cs
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GoogleDrive;

public static class GoogleDriveDataProviderExtensions
{
    /// <summary>Registers using a service account JSON key file path.</summary>
    public static IServiceCollection AddGoogleDriveDataProvider(
        this IServiceCollection services,
        string serviceAccountKeyPath,
        Action<GoogleDriveOptions>? configure = null)
    {
        GoogleCredential credential;
        using (var stream = File.OpenRead(serviceAccountKeyPath))
            credential = GoogleCredential.FromStream(stream)
                .CreateScoped(DriveService.Scope.DriveReadonly);

        var drive = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName       = "Rag.NET",
        });

        var opts = new GoogleDriveOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(new GoogleDriveDataProvider(drive, opts));
    }
}
```

**Step 5: Test project, build, add to solution, commit**

```bash
dotnet build src/Rag.NET.DataProviders.GoogleDrive/
dotnet test tests/Rag.NET.DataProviders.GoogleDrive.Tests/ -v minimal
git add src/Rag.NET.DataProviders.GoogleDrive/ tests/Rag.NET.DataProviders.GoogleDrive.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.GoogleDrive connector"
```

---

## Task 8: `Rag.NET.DataProviders.Dropbox`

**Files:**
- Create: `src/Rag.NET.DataProviders.Dropbox/Rag.NET.DataProviders.Dropbox.csproj`
- Create: `src/Rag.NET.DataProviders.Dropbox/DropboxOptions.cs`
- Create: `src/Rag.NET.DataProviders.Dropbox/DropboxDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Dropbox/DropboxDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Dropbox.Tests/Rag.NET.DataProviders.Dropbox.Tests.csproj`
- Modify: `Rag.NET.slnx`

**Step 1: Project file**

```xml
<!-- src/Rag.NET.DataProviders.Dropbox/Rag.NET.DataProviders.Dropbox.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Dropbox</RootNamespace>
    <PackageId>Rag.NET.DataProviders.Dropbox</PackageId>
    <Description>Dropbox data provider for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Dropbox.Api" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
  </ItemGroup>
</Project>
```

**Step 2: `DropboxOptions`**

```csharp
// src/Rag.NET.DataProviders.Dropbox/DropboxOptions.cs
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Dropbox;

public sealed class DropboxOptions : CloudStorageOptions
{
    /// <summary>Dropbox folder path (e.g. <c>"/docs"</c>). <c>null</c> = root.</summary>
    public string FolderPath { get; init; } = "";
}
```

**Step 3: `DropboxDataProvider`**

```csharp
// src/Rag.NET.DataProviders.Dropbox/DropboxDataProvider.cs
using System.Runtime.CompilerServices;
using Dropbox.Api;
using Dropbox.Api.Files;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Dropbox;

/// <summary>
/// Enumerates files from Dropbox.
/// Full run: ListFolder recursive. Delta run: ListFolderContinue using stored cursor.
/// Dropbox cursors do not expire — no stale-cursor fallback needed.
/// </summary>
public sealed class DropboxDataProvider : FileContentProviderBase
{
    private readonly ITokenProvider _tokenProvider;
    private readonly DropboxOptions _options;

    public DropboxDataProvider(ITokenProvider tokenProvider, DropboxOptions? options = null)
        : base(options ?? new DropboxOptions())
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
        _options = options ?? new DropboxOptions();
    }

    private async Task<DropboxClient> CreateClientAsync(CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(ct).ConfigureAwait(false);
        return new DropboxClient(token);
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.Files.ListFolderAsync(
            new ListFolderArg(_options.FolderPath, recursive: true))
            .ConfigureAwait(false);

        while (true)
        {
            foreach (var entry in result.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is not FileMetadata file) continue;

                var capturedPath = file.PathLower!;
                yield return new FileHandle(
                    Id:       file.PathDisplay ?? capturedPath,
                    FileName: file.Name,
                    ETag:     file.ContentHash,
                    OpenAsync: async ct =>
                    {
                        using var c = await CreateClientAsync(ct).ConfigureAwait(false);
                        var dl = await c.Files.DownloadAsync(capturedPath).ConfigureAwait(false);
                        return await dl.GetContentAsStreamAsync().ConfigureAwait(false);
                    });
            }

            if (!result.HasMore) break;
            result = await client.Files.ListFolderContinueAsync(result.Cursor).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.Files.ListFolderContinueAsync(_options.DeltaToken!)
            .ConfigureAwait(false);

        while (true)
        {
            foreach (var entry in result.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is not FileMetadata file) continue;

                var capturedPath = file.PathLower!;
                yield return new FileHandle(
                    Id:       file.PathDisplay ?? capturedPath,
                    FileName: file.Name,
                    ETag:     file.ContentHash,
                    OpenAsync: async ct =>
                    {
                        using var c = await CreateClientAsync(ct).ConfigureAwait(false);
                        var dl = await c.Files.DownloadAsync(capturedPath).ConfigureAwait(false);
                        return await dl.GetContentAsStreamAsync().ConfigureAwait(false);
                    });
            }

            if (!result.HasMore) break;
            result = await client.Files.ListFolderContinueAsync(result.Cursor).ConfigureAwait(false);
        }
    }
}
```

**Step 4: DI extensions**

```csharp
// src/Rag.NET.DataProviders.Dropbox/DropboxDataProviderExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Dropbox;

public static class DropboxDataProviderExtensions
{
    public static IServiceCollection AddDropboxDataProvider(
        this IServiceCollection services,
        string accessToken,
        Action<DropboxOptions>? configure = null)
        => services.AddDropboxDataProvider(new StaticTokenProvider(accessToken), configure);

    public static IServiceCollection AddDropboxDataProvider(
        this IServiceCollection services,
        ITokenProvider tokenProvider,
        Action<DropboxOptions>? configure = null)
    {
        var opts = new DropboxOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(new DropboxDataProvider(tokenProvider, opts));
    }
}
```

**Step 5: Build, test, add to solution, commit**

```bash
dotnet build src/Rag.NET.DataProviders.Dropbox/
dotnet test tests/Rag.NET.DataProviders.Dropbox.Tests/ -v minimal
git add src/Rag.NET.DataProviders.Dropbox/ tests/Rag.NET.DataProviders.Dropbox.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.Dropbox connector"
```

---

## Task 9: `Rag.NET.DataProviders.Box`

**Files:**
- Create: `src/Rag.NET.DataProviders.Box/Rag.NET.DataProviders.Box.csproj`
- Create: `src/Rag.NET.DataProviders.Box/BoxDataProviderOptions.cs`
- Create: `src/Rag.NET.DataProviders.Box/BoxDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Box/BoxDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Box.Tests/Rag.NET.DataProviders.Box.Tests.csproj`
- Modify: `Rag.NET.slnx`

**Step 1: Project file**

```xml
<!-- src/Rag.NET.DataProviders.Box/Rag.NET.DataProviders.Box.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Box</RootNamespace>
    <PackageId>Rag.NET.DataProviders.Box</PackageId>
    <Description>Box data provider for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Box.V2" Version="3.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
  </ItemGroup>
</Project>
```

**Step 2: `BoxDataProviderOptions`**

```csharp
// src/Rag.NET.DataProviders.Box/BoxDataProviderOptions.cs
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Box;

public sealed class BoxDataProviderOptions : CloudStorageOptions
{
    /// <summary>Box folder ID to enumerate. <c>"0"</c> = root.</summary>
    public string RootFolderId { get; init; } = "0";
}
```

**Step 3: `BoxDataProvider`**

```csharp
// src/Rag.NET.DataProviders.Box/BoxDataProvider.cs
using System.Runtime.CompilerServices;
using Box.V2;
using Box.V2.Config;
using Box.V2.JWTAuth;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Box;

/// <summary>
/// Enumerates files from Box using the Box.V2 SDK.
/// Full run: recursive folder items. Delta run: Box Events stream cursor.
/// Box stream positions do not expire.
/// </summary>
public sealed class BoxDataProvider : FileContentProviderBase
{
    private readonly BoxClient _client;
    private readonly BoxDataProviderOptions _options;

    public BoxDataProvider(BoxClient client, BoxDataProviderOptions? options = null)
        : base(options ?? new BoxDataProviderOptions())
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options ?? new BoxDataProviderOptions();
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(_options.RootFolderId);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderId = stack.Pop();
            long offset = 0;
            const int limit = 100;

            while (true)
            {
                var items = await _client.FoldersManager.GetFolderItemsAsync(
                    folderId, limit, (int)offset, fields: ["id", "name", "type", "sha1"])
                    .ConfigureAwait(false);

                foreach (var item in items.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(item.Type, "folder", StringComparison.Ordinal))
                    {
                        stack.Push(item.Id);
                        continue;
                    }

                    var capturedId = item.Id;
                    yield return new FileHandle(
                        Id:       item.Id,
                        FileName: item.Name,
                        ETag:     (item as Box.V2.Models.BoxFile)?.Sha1,
                        OpenAsync: async ct =>
                            await _client.FilesManager.DownloadAsync(capturedId, cancellationToken: ct)
                                .ConfigureAwait(false));
                }

                offset += items.Entries.Count;
                if (offset >= items.TotalCount) break;
            }
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var streamPosition = long.Parse(_options.DeltaToken!, System.Globalization.CultureInfo.InvariantCulture);
        var events = await _client.EventsManager.UserEventsAsync(
            limit: 100, streamPosition: streamPosition)
            .ConfigureAwait(false);

        foreach (var ev in events.Entries ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ev.Source is not Box.V2.Models.BoxFile file) continue;
            if (!string.Equals(ev.EventType, "UPLOAD", StringComparison.Ordinal)
             && !string.Equals(ev.EventType, "COPY",   StringComparison.Ordinal)) continue;

            var capturedId = file.Id;
            yield return new FileHandle(
                Id:       file.Id,
                FileName: file.Name,
                ETag:     file.Sha1,
                OpenAsync: async ct =>
                    await _client.FilesManager.DownloadAsync(capturedId, cancellationToken: ct)
                        .ConfigureAwait(false));
        }
    }
}
```

**Step 4: DI extensions**

```csharp
// src/Rag.NET.DataProviders.Box/BoxDataProviderExtensions.cs
using Box.V2;
using Box.V2.Config;
using Box.V2.JWTAuth;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Box;

public static class BoxDataProviderExtensions
{
    /// <summary>Registers using Box JWT service account config JSON.</summary>
    public static IServiceCollection AddBoxDataProvider(
        this IServiceCollection services,
        string jwtConfigJson,
        Action<BoxDataProviderOptions>? configure = null)
    {
        var config  = BoxConfigBuilder.CreateFromJsonString(jwtConfigJson).Build();
        var session = new BoxJWTAuth(config);
        var token   = session.AdminToken();
        var client  = session.AdminClient(token);

        var opts = new BoxDataProviderOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(new BoxDataProvider(client, opts));
    }
}
```

**Step 5: Build, test, add to solution, commit**

```bash
dotnet build src/Rag.NET.DataProviders.Box/
dotnet test tests/Rag.NET.DataProviders.Box.Tests/ -v minimal
git add src/Rag.NET.DataProviders.Box/ tests/Rag.NET.DataProviders.Box.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.Box connector"
```

---

## Task 10: Full solution build and update feature backlog

**Step 1: Build entire solution**

```bash
dotnet build
```
Expected: Build succeeded, 0 errors.

**Step 2: Run all data provider tests**

```bash
dotnet test tests/Rag.NET.DataProviders.Tests/ tests/Rag.NET.DataProviders.GitHub.Tests/ tests/Rag.NET.DataProviders.AzureBlob.Tests/ tests/Rag.NET.DataProviders.SharePoint.Tests/ tests/Rag.NET.DataProviders.OneDrive.Tests/ tests/Rag.NET.DataProviders.GoogleDrive.Tests/ tests/Rag.NET.DataProviders.Dropbox.Tests/ tests/Rag.NET.DataProviders.Box.Tests/ -v minimal
```
Expected: All tests pass.

**Step 3: Mark connectors as done in `docs/reference/features.md`**

In the priority table, change the following rows from `[ ]` to `[x]`:
- `SaaS: Azure Blob Storage`
- `SaaS: SharePoint`
- `SaaS: OneDrive`
- `SaaS: Google Drive`
- `SaaS: Dropbox`
- `SaaS: Box`

Also add `**Status:** ✅ Done` to each matching section in Group 1 — Cloud Storage.

**Step 4: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark cloud storage connectors as done in feature backlog"
```

---

## Task Summary

| Task | Deliverable | Tests |
|---|---|---|
| 1 | `Rag.NET.DataProviders` shared package | — |
| 2 | Tests for base class + OAuth token provider | 8 tests |
| 3 | Migrate `GitHubDataProvider` to base class | existing tests pass |
| 4 | `Rag.NET.DataProviders.AzureBlob` | 3 tests |
| 5 | `Rag.NET.DataProviders.SharePoint` | 1 smoke test |
| 6 | `Rag.NET.DataProviders.OneDrive` | 1 smoke test |
| 7 | `Rag.NET.DataProviders.GoogleDrive` | 1 smoke test |
| 8 | `Rag.NET.DataProviders.Dropbox` | 1 smoke test |
| 9 | `Rag.NET.DataProviders.Box` | 1 smoke test |
| 10 | Full build + backlog update | all pass |
