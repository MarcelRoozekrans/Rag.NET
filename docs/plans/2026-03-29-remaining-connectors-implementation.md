# Remaining Connectors — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add 4 new connectors (GitLab, Bitbucket, Zendesk tickets+articles, Airtable rows+attachments) following established project patterns.

**Architecture:** Each connector extends `FileContentProviderBase`, uses either an SDK or Refit for HTTP, provides DI registration extensions, and includes ~5 unit tests. Zendesk is one package with two providers.

**Tech Stack:** NGitLab (GitLab), Refit (Bitbucket), ZendeskApi.Client + Refit (Zendesk), Airtable.NET (Airtable), xUnit, NSubstitute

---

## Task 1: GitLab Connector

**Files:**
- Create: `src/Rag.NET.DataProviders.GitLab/Rag.NET.DataProviders.GitLab.csproj`
- Create: `src/Rag.NET.DataProviders.GitLab/GitLabOptions.cs`
- Create: `src/Rag.NET.DataProviders.GitLab/GitLabDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.GitLab/GitLabDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.GitLab.Tests/Rag.NET.DataProviders.GitLab.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.GitLab.Tests/GitLabDataProviderTests.cs`
- Modify: `Rag.NET.slnx` (add both projects)

**Step 1: Create project file**

```xml
<!-- src/Rag.NET.DataProviders.GitLab/Rag.NET.DataProviders.GitLab.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.GitLab</RootNamespace>
    <PackageId>Rag.NET.DataProviders.GitLab</PackageId>
    <Description>GitLab data provider for Rag.NET using NGitLab</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="NGitLab" Version="11.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.DataProviders.GitLab.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

**Step 2: Create options class**

```csharp
// src/Rag.NET.DataProviders.GitLab/GitLabOptions.cs
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitLab;

/// <summary>Configuration for <see cref="GitLabDataProvider"/>.</summary>
public sealed class GitLabOptions : CloudStorageOptions
{
    /// <summary>GitLab instance base URL (e.g. <c>https://gitlab.com</c>).</summary>
    public required string BaseUrl { get; set; }

    /// <summary>Project ID (numeric) or path (<c>namespace/project</c>).</summary>
    public required string ProjectIdOrPath { get; set; }

    /// <summary>Branch or ref to traverse. Default: <c>"main"</c>.</summary>
    public string Ref { get; set; } = "main";

    /// <summary>
    /// When set, performs a delta run: only files changed since this commit SHA are returned.
    /// Maps to <see cref="CloudStorageOptions.DeltaToken"/>.
    /// </summary>
    public string? LastIngestedCommitSha
    {
        get => DeltaToken;
        init => DeltaToken = value;
    }
}
```

**Step 3: Create data provider**

```csharp
// src/Rag.NET.DataProviders.GitLab/GitLabDataProvider.cs
using System.Runtime.CompilerServices;
using NGitLab;
using NGitLab.Models;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitLab;

/// <summary>
/// Enumerates files from a GitLab repository using the NGitLab SDK.
/// <para>Full run: recursive tree traversal of all blobs.
/// Delta run: compare API between <see cref="GitLabOptions.LastIngestedCommitSha"/> and HEAD.
/// ETag is the blob SHA from the tree listing.</para>
/// </summary>
public sealed class GitLabDataProvider : FileContentProviderBase
{
    private readonly IGitLabClient _client;
    private readonly GitLabOptions _options;

    public GitLabDataProvider(IGitLabClient client, GitLabOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client  = client;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullTreeHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullTreeHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repoClient = _client.GetRepository(_options.ProjectIdOrPath);
        var tree = await Task.Run(
            () => repoClient.GetTreeAsync(new RepositoryGetTreeOptions
            {
                Ref = _options.Ref,
                Recursive = true,
                PerPage = 100,
            }),
            cancellationToken).ConfigureAwait(false);

        // NGitLab returns IEnumerable — iterate synchronously then yield
        var items = tree.ToList();
        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            if (!string.Equals(item.Type, "blob", StringComparison.Ordinal)) continue;

            var capturedPath = item.Path;
            yield return new FileHandle(
                Id:               item.Path,
                FileName:         Path.GetFileName(item.Path),
                ETag:             item.Id.ToString(),
                OpenContentAsync: async ct =>
                {
                    var fileClient = _client.GetRepository(_options.ProjectIdOrPath);
                    var bytes = await Task.Run(
                        () => fileClient.GetRawBlob(capturedPath, r => _options.Ref),
                        ct).ConfigureAwait(false);
                    return new MemoryStream(bytes);
                });
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repoClient = _client.GetRepository(_options.ProjectIdOrPath);
        var diff = await Task.Run(
            () => repoClient.Compare(new CompareQuery
            {
                Source = _options.DeltaToken!,
                Target = _options.Ref,
            }),
            cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < diff.Diffs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var d = diff.Diffs[i];
            if (d.IsDeleted) continue;

            var capturedPath = d.NewPath;
            yield return new FileHandle(
                Id:               d.NewPath,
                FileName:         Path.GetFileName(d.NewPath),
                ETag:             d.BlobId?.ToString() ?? string.Empty,
                OpenContentAsync: async ct =>
                {
                    var fileClient = _client.GetRepository(_options.ProjectIdOrPath);
                    var bytes = await Task.Run(
                        () => fileClient.GetRawBlob(capturedPath, r => _options.Ref),
                        ct).ConfigureAwait(false);
                    return new MemoryStream(bytes);
                });
        }
    }
}
```

**NOTE TO IMPLEMENTER:** The exact NGitLab API surface may differ from what's shown above. Read the NGitLab source/docs to find the correct method signatures for:
- Getting a recursive tree (`IRepositoryClient.GetTreeAsync` or `.Tree`)
- Getting raw blob content (`GetRawBlob` or `GetRawFile`)
- Comparing commits (`Compare` or `GetCompare`)
- Checking if a diff entry was deleted (`IsDeleted`, `DeletedFile`, or `Status`)

Adjust the code accordingly. The pattern is correct; the exact API calls may need minor adjustments.

**Step 4: Create DI extensions**

```csharp
// src/Rag.NET.DataProviders.GitLab/GitLabDataProviderExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using NGitLab;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitLab;

/// <summary>DI registration for <see cref="GitLabDataProvider"/>.</summary>
public static class GitLabDataProviderExtensions
{
    /// <summary>Registers a <see cref="GitLabDataProvider"/> with the DI container.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="baseUrl">GitLab instance URL (e.g. <c>https://gitlab.com</c>).</param>
    /// <param name="projectIdOrPath">Project numeric ID or <c>namespace/project</c> path.</param>
    /// <param name="token">Personal Access Token or Project Access Token.</param>
    /// <param name="configure">Optional callback to configure <see cref="GitLabOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddGitLabDataProvider(
        this IServiceCollection services,
        string baseUrl,
        string projectIdOrPath,
        string token,
        Action<GitLabOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new GitLabOptions
        {
            BaseUrl          = baseUrl,
            ProjectIdOrPath  = projectIdOrPath,
        };
        configure?.Invoke(options);

        var client = new GitLabClient(baseUrl, token);
        services.AddSingleton<IFileContentProvider>(
            _ => new GitLabDataProvider(client, options));
        return services;
    }
}
```

**Step 5: Create test project and tests**

```xml
<!-- tests/Rag.NET.DataProviders.GitLab.Tests/Rag.NET.DataProviders.GitLab.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders.GitLab\Rag.NET.DataProviders.GitLab.csproj" />
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>
</Project>
```

Tests (in `GitLabDataProviderTests.cs`):
1. **Constructor_NullClient_Throws** — `Assert.Throws<ArgumentNullException>`
2. **GetFilesAsync_FullTraversal_YieldsBlobs** — mock `IGitLabClient` returning tree with 2 blobs + 1 tree entry → 2 files
3. **GetFilesAsync_DeltaTraversal_SkipsDeletedFiles** — mock compare returning 2 diffs (1 modified, 1 deleted) → 1 file
4. **GetFilesAsync_ExtensionFilter_ExcludesNonMatching** — set Extensions=[".md"], tree has .md and .cs → only .md returned
5. **GetFilesAsync_CancellationRequested_Throws** — cancelled token

Use NSubstitute to mock `IGitLabClient` and its nested interfaces (`IRepositoryClient`).

**Step 6: Add projects to solution, build, test, commit**

```bash
# Add to Rag.NET.slnx
dotnet build src/Rag.NET.DataProviders.GitLab/
dotnet test tests/Rag.NET.DataProviders.GitLab.Tests/
git add src/Rag.NET.DataProviders.GitLab/ tests/Rag.NET.DataProviders.GitLab.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.GitLab connector"
```

---

## Task 2: Bitbucket Connector

**Files:**
- Create: `src/Rag.NET.DataProviders.Bitbucket/Rag.NET.DataProviders.Bitbucket.csproj`
- Create: `src/Rag.NET.DataProviders.Bitbucket/BitbucketOptions.cs`
- Create: `src/Rag.NET.DataProviders.Bitbucket/IBitbucketApi.cs`
- Create: `src/Rag.NET.DataProviders.Bitbucket/BitbucketSourceEntry.cs`
- Create: `src/Rag.NET.DataProviders.Bitbucket/BitbucketSourcePage.cs`
- Create: `src/Rag.NET.DataProviders.Bitbucket/BitbucketDiffstatEntry.cs`
- Create: `src/Rag.NET.DataProviders.Bitbucket/BitbucketDiffstatPage.cs`
- Create: `src/Rag.NET.DataProviders.Bitbucket/BitbucketDiffstatFile.cs`
- Create: `src/Rag.NET.DataProviders.Bitbucket/BitbucketCommit.cs`
- Create: `src/Rag.NET.DataProviders.Bitbucket/BitbucketDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Bitbucket/BitbucketDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Bitbucket.Tests/Rag.NET.DataProviders.Bitbucket.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Bitbucket.Tests/BitbucketDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Bitbucket</RootNamespace>
    <PackageId>Rag.NET.DataProviders.Bitbucket</PackageId>
    <Description>Bitbucket data provider for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Refit" Version="8.*" />
    <PackageReference Include="Refit.HttpClientFactory" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.DataProviders.Bitbucket.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

**Step 2: Create DTOs (one file each, MA0048 compliance)**

DTOs:
- `BitbucketSourceEntry` — `Path`, `Type` ("commit_file" or "commit_directory"), `Size`, `Hash`
- `BitbucketSourcePage` — `Values` (List), `Next` (string?)
- `BitbucketDiffstatEntry` — `Status` ("added"/"modified"/"removed"), `New` (BitbucketDiffstatFile?)
- `BitbucketDiffstatFile` — `Path` (string)
- `BitbucketDiffstatPage` — `Values` (List), `Next` (string?)
- `BitbucketCommit` — `Hash` (string)

**Step 3: Create Refit interface**

```csharp
// src/Rag.NET.DataProviders.Bitbucket/IBitbucketApi.cs
using Refit;

namespace Rag.NET.DataProviders.Bitbucket;

[Headers("Accept: application/json")]
internal interface IBitbucketApi
{
    [Get("/2.0/repositories/{workspace}/{repo}/src/{commit}/{path}")]
    Task<BitbucketSourcePage> GetSourceAsync(
        string workspace, string repo, string commit, string path,
        [Query] int pagelen = 100, [Query] string? page = null,
        CancellationToken cancellationToken = default);

    [Get("/2.0/repositories/{workspace}/{repo}/src/{commit}/{path}")]
    Task<HttpResponseMessage> GetRawFileAsync(
        string workspace, string repo, string commit, string path,
        CancellationToken cancellationToken = default);

    [Get("/2.0/repositories/{workspace}/{repo}/diffstat/{spec}")]
    Task<BitbucketDiffstatPage> GetDiffstatAsync(
        string workspace, string repo, string spec,
        [Query] int pagelen = 100, [Query] string? page = null,
        CancellationToken cancellationToken = default);
}
```

**Step 4: Create data provider**

Pattern:
- Full traversal: recursive call to `GetSourceAsync` on root `""` path, filtering `Type == "commit_file"`. For directories, recursively list.
  - Actually, Bitbucket's source endpoint with `?max_depth=100` or recursive listing returns all files in a flat list when called on root.
  - Paginate via `Next` URL.
- Delta: `GetDiffstatAsync` with `spec = "{deltaToken}..{ref}"`, filter out `Status == "removed"`.
- Content: `GetRawFileAsync` returns raw bytes.
- DeltaToken = commit hash.
- ETag = file `Hash` from source listing.

**Step 5: Create DI extensions**

`AddBitbucketDataProvider(workspace, repoSlug, username, appPassword, configure?)` — sets up Basic Auth header via `HttpClient.DefaultRequestHeaders.Authorization`.

**Step 6: Create test project and tests**

5 tests: full traversal, delta, extension filter, constructor null, removed files in delta. FakeHandler pattern.

**Step 7: Add to solution, build, test, commit**

```bash
git commit -m "feat: add Rag.NET.DataProviders.Bitbucket connector"
```

---

## Task 3: Zendesk Tickets Provider

**Files:**
- Create: `src/Rag.NET.DataProviders.Zendesk/Rag.NET.DataProviders.Zendesk.csproj`
- Create: `src/Rag.NET.DataProviders.Zendesk/ZendeskTicketsOptions.cs`
- Create: `src/Rag.NET.DataProviders.Zendesk/ZendeskArticlesOptions.cs`
- Create: `src/Rag.NET.DataProviders.Zendesk/IZendeskApi.cs`
- Create: DTOs: `ZendeskIncrementalTicketResult.cs`, `ZendeskTicket.cs`, `ZendeskComment.cs`, `ZendeskCommentPage.cs`, `ZendeskUser.cs`, `ZendeskIncrementalArticleResult.cs`, `ZendeskArticle.cs`
- Create: `src/Rag.NET.DataProviders.Zendesk/ZendeskTicketsDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Zendesk/ZendeskDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Zendesk.Tests/Rag.NET.DataProviders.Zendesk.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Zendesk.Tests/ZendeskTicketsDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Zendesk</RootNamespace>
    <PackageId>Rag.NET.DataProviders.Zendesk</PackageId>
    <Description>Zendesk data provider for Rag.NET (tickets and articles)</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Refit" Version="8.*" />
    <PackageReference Include="Refit.HttpClientFactory" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.DataProviders.Zendesk.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

**NOTE:** After researching, the ZendeskApi.Client SDK doesn't cover the incremental export endpoints, so use Refit only for consistency. Drop the SDK dependency.

**Step 2: Create Refit interface**

```csharp
// src/Rag.NET.DataProviders.Zendesk/IZendeskApi.cs
using Refit;

namespace Rag.NET.DataProviders.Zendesk;

[Headers("Accept: application/json")]
internal interface IZendeskApi
{
    [Get("/api/v2/incremental/tickets/cursor.json")]
    Task<ZendeskIncrementalTicketResult> GetIncrementalTicketsAsync(
        [Query("start_time")] long startTime,
        [Query] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/v2/tickets/{ticketId}/comments")]
    Task<ZendeskCommentPage> GetTicketCommentsAsync(
        long ticketId,
        CancellationToken cancellationToken = default);

    [Get("/api/v2/help_center/incremental/articles.json")]
    Task<ZendeskIncrementalArticleResult> GetIncrementalArticlesAsync(
        [Query("start_time")] long startTime,
        CancellationToken cancellationToken = default);
}
```

**Step 3: Create DTOs**

- `ZendeskIncrementalTicketResult` — `Tickets` (List), `AfterCursor` (string?), `EndOfStream` (bool), `EndTime` (long)
- `ZendeskTicket` — `Id` (long), `Subject` (string), `Description` (string?), `Status` (string), `Priority` (string?), `RequesterEmail` (string?), `UpdatedAt` (string)
- `ZendeskCommentPage` — `Comments` (List)
- `ZendeskComment` — `Id` (long), `Body` (string), `AuthorId` (long), `CreatedAt` (string)
- `ZendeskIncrementalArticleResult` — `Articles` (List), `EndTime` (long), `Count` (int)
- `ZendeskArticle` — `Id` (long), `Title` (string), `Body` (string?), `UpdatedAt` (string), `SectionId` (long?)

**Step 4: Create tickets provider**

```csharp
// src/Rag.NET.DataProviders.Zendesk/ZendeskTicketsDataProvider.cs
```

Pattern:
- Uses incremental export API: `start_time=0` for full, `start_time={deltaToken}` for delta
- Paginates via `AfterCursor` until `EndOfStream` is true
- Per ticket: fetch comments via `GetTicketCommentsAsync`
- Markdown: `# {Subject}`, status/priority/requester metadata, description, `## Comments` section
- DeltaToken = `EndTime.ToString()` (Unix epoch)
- ETag = `UpdatedAt`

**Step 5: Create DI extension for tickets**

`AddZendeskTicketsDataProvider(subdomain, email, apiToken, configure?)` — Basic Auth with `email/token:apiToken`.

**Step 6: Tests (5 tests)**

1. Constructor_NullApi_Throws
2. GetFilesAsync_FullTraversal_YieldsTickets
3. GetFilesAsync_DeltaTraversal_UsesStartTime
4. GetFilesAsync_WithComments_MarkdownRendersComments
5. GetFilesAsync_ExtensionFilter_ExcludesNonMd

FakeHandler pattern returning canned JSON.

**Step 7: Build, test, commit**

```bash
git commit -m "feat: add Rag.NET.DataProviders.Zendesk tickets provider"
```

---

## Task 4: Zendesk Articles Provider

**Files:**
- Create: `src/Rag.NET.DataProviders.Zendesk/ZendeskArticlesDataProvider.cs`
- Modify: `src/Rag.NET.DataProviders.Zendesk/ZendeskDataProviderExtensions.cs` (add articles DI method)
- Create: `tests/Rag.NET.DataProviders.Zendesk.Tests/ZendeskArticlesDataProviderTests.cs`

**Step 1: Create articles provider**

Pattern:
- Uses incremental articles API: `start_time=0` for full, `start_time={deltaToken}` for delta
- Each article → markdown FileHandle with `# {Title}` and HTML body stripped via `HtmlTagRegex`
- DeltaToken = `EndTime.ToString()`
- ETag = `UpdatedAt`
- Paginate: if `Count == 1000` (max per page), increment `start_time` and fetch again

**Step 2: Add DI extension**

`AddZendeskArticlesDataProvider(subdomain, email, apiToken, configure?)` — same auth as tickets.

**Step 3: Tests (5 tests)**

1. Constructor_NullApi_Throws
2. GetFilesAsync_FullTraversal_YieldsArticles
3. GetFilesAsync_HtmlBody_IsStrippedToMarkdown
4. GetFilesAsync_DeltaTraversal_UsesStartTime
5. GetFilesAsync_ExtensionFilter_ExcludesNonMd

**Step 4: Build, test, commit**

```bash
git commit -m "feat: add Rag.NET.DataProviders.Zendesk articles provider"
```

---

## Task 5: Airtable Connector

**Files:**
- Create: `src/Rag.NET.DataProviders.Airtable/Rag.NET.DataProviders.Airtable.csproj`
- Create: `src/Rag.NET.DataProviders.Airtable/AirtableOptions.cs`
- Create: `src/Rag.NET.DataProviders.Airtable/IAirtableClient.cs` (wrapper interface for testability)
- Create: `src/Rag.NET.DataProviders.Airtable/AirtableClientWrapper.cs`
- Create: `src/Rag.NET.DataProviders.Airtable/AirtableDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Airtable/AirtableDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Airtable.Tests/Rag.NET.DataProviders.Airtable.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Airtable.Tests/AirtableDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Airtable</RootNamespace>
    <PackageId>Rag.NET.DataProviders.Airtable</PackageId>
    <Description>Airtable data provider for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Airtable" Version="1.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.DataProviders.Airtable.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

**Step 2: Create options class**

```csharp
// src/Rag.NET.DataProviders.Airtable/AirtableOptions.cs
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Airtable;

/// <summary>Configuration for <see cref="AirtableDataProvider"/>.</summary>
public sealed class AirtableOptions : CloudStorageOptions
{
    /// <summary>Airtable base ID (e.g. <c>appXXXXXXXXXXXXXX</c>).</summary>
    public required string BaseId { get; set; }

    /// <summary>Table name or ID to enumerate.</summary>
    public required string TableName { get; set; }

    /// <summary>Optional view name to filter records.</summary>
    public string? View { get; set; }

    /// <summary>
    /// Name of a "Last modified time" field in the table, enabling delta sync.
    /// When set with <see cref="CloudStorageOptions.DeltaToken"/>, only records
    /// modified after the token timestamp are returned.
    /// </summary>
    public string? LastModifiedFieldName { get; set; }
}
```

**Step 3: Create wrapper interface for Airtable SDK**

```csharp
// src/Rag.NET.DataProviders.Airtable/IAirtableClient.cs
using AirtableApiClient;

namespace Rag.NET.DataProviders.Airtable;

/// <summary>Testable wrapper around <see cref="AirtableBase"/>.</summary>
internal interface IAirtableClient
{
    Task<AirtableListRecordsResponse> ListRecordsAsync(
        string tableName,
        string? offset = null,
        string? filterByFormula = null,
        string? view = null);
}
```

```csharp
// src/Rag.NET.DataProviders.Airtable/AirtableClientWrapper.cs
using AirtableApiClient;

namespace Rag.NET.DataProviders.Airtable;

internal sealed class AirtableClientWrapper(AirtableBase airtableBase) : IAirtableClient
{
    public async Task<AirtableListRecordsResponse> ListRecordsAsync(
        string tableName, string? offset = null,
        string? filterByFormula = null, string? view = null)
    {
        return await airtableBase.ListRecords(tableName,
            offset: offset,
            filterByFormula: filterByFormula,
            view: view).ConfigureAwait(false);
    }
}
```

**Step 4: Create data provider**

Pattern:
- Full: `ListRecordsAsync(tableName, offset, view:)` → paginate via `offset`
- Delta: `ListRecordsAsync(tableName, offset, filterByFormula: "LAST_MODIFIED_TIME()>'{deltaToken}'", view:)` when `LastModifiedFieldName` is set
- Per record:
  - Emit a markdown FileHandle: `# {first field value}`, field table, long text sections
  - For each attachment field: emit a separate FileHandle downloading from the signed URL
- Markdown FileHandle.Id = record ID
- Attachment FileHandle.Id = `{recordId}/{fieldName}/{attachmentFilename}`
- ETag = hash record fields JSON (no built-in content hash)

**IMPORTANT:** Airtable attachment URLs expire. The `OpenContentAsync` lambda must download the URL at call time using an `HttpClient`. Store an `HttpClient` in the provider for attachment downloads.

**Step 5: Create DI extensions**

`AddAirtableDataProvider(baseId, tableName, token, configure?)` — creates `AirtableBase(token, baseId)` and wraps in `AirtableClientWrapper`.

**Step 6: Tests (5 tests)**

1. Constructor_NullClient_Throws
2. GetFilesAsync_FullTraversal_YieldsRowsAndAttachments — record with 1 attachment → 2 FileEntries
3. GetFilesAsync_DeltaTraversal_UsesFilterFormula — verify formula contains LAST_MODIFIED_TIME
4. GetFilesAsync_ExtensionFilter_ExcludesNonMatching
5. GetFilesAsync_NullLastModifiedFieldName_FullTraversalEvenWithDeltaToken — if field name not set, DeltaToken is ignored

Mock `IAirtableClient` with NSubstitute. For attachment download, mock HttpClient with FakeHandler.

**Step 7: Build, test, commit**

```bash
git commit -m "feat: add Rag.NET.DataProviders.Airtable connector"
```

---

## Task 6: Update Documentation

**Files:**
- Modify: `docs/guide/data-providers.md` (add 4 connectors to table + DI examples)
- Modify: `README.md` (add 4 rows to packages table)
- Modify: `docs/index.md` (add to package diagram)
- Modify: `docs/reference/features.md` (mark 4 connectors as done)

**Step 1: Add to connector reference table in data-providers.md**

4 new rows: GitLab, Bitbucket, Zendesk, Airtable with package, SDK, auth, delta info.

**Step 2: Add DI registration examples**

One code block per connector (Zendesk gets two — tickets + articles).

**Step 3: Update README packages table**

4 new rows.

**Step 4: Update features.md**

Mark GitLab, Bitbucket, Zendesk, Airtable as `[x]` done.

**Step 5: Commit**

```bash
git commit -m "docs: add GitLab, Bitbucket, Zendesk, Airtable to documentation"
```

---

## Task 7: Build + Test Full Solution

**Step 1: Build entire solution**

```bash
dotnet build Rag.NET.slnx
```

**Step 2: Run all tests**

```bash
dotnet test Rag.NET.slnx
```

Expected: all tests pass, 0 regressions.

---

## Summary

| Task | Connector | SDK | Tests | Parallel |
|------|-----------|-----|-------|----------|
| 1 | GitLab | NGitLab | ~5 | Yes |
| 2 | Bitbucket | Refit | ~5 | Yes |
| 3 | Zendesk Tickets | Refit | ~5 | Yes |
| 4 | Zendesk Articles | Refit | ~5 | After 3 |
| 5 | Airtable | Airtable.NET | ~5 | Yes |
| 6 | Documentation | — | — | After 1-5 |
| 7 | Build + test | — | — | After 6 |
