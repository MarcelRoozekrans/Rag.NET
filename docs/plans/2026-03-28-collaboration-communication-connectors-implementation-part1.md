# Collaboration & Communication Connectors — Part 1 (Tasks 1–4)

---

## Task 1: Confluence connector

**Files:**
- Create: `src/Rag.NET.DataProviders.Confluence/Rag.NET.DataProviders.Confluence.csproj`
- Create: `src/Rag.NET.DataProviders.Confluence/ConfluenceOptions.cs`
- Create: `src/Rag.NET.DataProviders.Confluence/ConfluenceApi.cs` (Refit interface + DTOs)
- Create: `src/Rag.NET.DataProviders.Confluence/ConfluenceDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Confluence/ConfluenceDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Confluence.Tests/Rag.NET.DataProviders.Confluence.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Confluence.Tests/ConfluenceDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

### csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Confluence</RootNamespace>
    <PackageId>Rag.NET.DataProviders.Confluence</PackageId>
    <Description>Confluence data provider for Rag.NET</Description>
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
      <_Parameter1>Rag.NET.DataProviders.Confluence.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

### ConfluenceOptions.cs
```csharp
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Confluence;

public sealed class ConfluenceOptions : CloudStorageOptions
{
    public required string BaseUrl { get; init; }
    public required string Email   { get; init; }
    public string? SpaceKey        { get; init; }
}
```

### ConfluenceApi.cs (Refit interface + DTOs)
```csharp
using System.Text.Json.Serialization;
using Refit;

namespace Rag.NET.DataProviders.Confluence;

[Headers("Accept: application/json")]
internal interface IConfluenceApi
{
    [Get("/wiki/rest/api/content")]
    Task<ConfluencePageList> GetPagesAsync(
        [Query] string? spaceKey,
        [Query] int limit,
        [Query] string? cursor,
        [Query("expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);

    [Get("/wiki/rest/api/content/search")]
    Task<ConfluencePageList> SearchPagesAsync(
        [Query] string cql,
        [Query] int limit,
        [Query] string? cursor,
        [Query("expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);
}

internal sealed record ConfluencePageList(
    [property: JsonPropertyName("results")] List<ConfluencePage> Results,
    [property: JsonPropertyName("_links")]  ConfluenceLinks Links);

internal sealed record ConfluencePage(
    [property: JsonPropertyName("id")]      string Id,
    [property: JsonPropertyName("title")]   string Title,
    [property: JsonPropertyName("body")]    ConfluenceBody Body,
    [property: JsonPropertyName("version")] ConfluenceVersion Version);

internal sealed record ConfluenceBody(
    [property: JsonPropertyName("storage")] ConfluenceStorage Storage);

internal sealed record ConfluenceStorage(
    [property: JsonPropertyName("value")] string Value);

internal sealed record ConfluenceVersion(
    [property: JsonPropertyName("number")] int Number);

internal sealed record ConfluenceLinks(
    [property: JsonPropertyName("next")] string? Next);
```

### ConfluenceDataProvider.cs
```csharp
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Confluence;

public sealed class ConfluenceDataProvider : FileContentProviderBase
{
    private readonly IConfluenceApi _api;
    private readonly ConfluenceOptions _options;

    public ConfluenceDataProvider(IConfluenceApi api, ConfluenceOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api     = api;
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
        string? cursor = null;
        do
        {
            var page = await _api.GetPagesAsync(
                _options.SpaceKey, limit: 50, cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var p in page.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ToHandle(p);
            }

            cursor = ExtractCursor(page.Links.Next);
        }
        while (cursor is not null);
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cql = _options.SpaceKey is not null
            ? $"space=\"{_options.SpaceKey}\" AND lastModified>\"{_options.DeltaToken}\""
            : $"lastModified>\"{_options.DeltaToken}\"";

        string? cursor = null;
        do
        {
            var page = await _api.SearchPagesAsync(
                cql, limit: 50, cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var p in page.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ToHandle(p);
            }

            cursor = ExtractCursor(page.Links.Next);
        }
        while (cursor is not null);
    }

    private static FileHandle ToHandle(ConfluencePage p)
    {
        var markdown = ToMarkdown(p);
        return new FileHandle(
            Id:              p.Id,
            FileName:        $"{p.Title}.md",
            ETag:            p.Version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private static string ToMarkdown(ConfluencePage p)
    {
        var body = Regex.Replace(p.Body.Storage.Value, "<[^>]+>", string.Empty);
        body = System.Net.WebUtility.HtmlDecode(body).Trim();
        return $"# {p.Title}\n\n{body}";
    }

    private static string? ExtractCursor(string? next)
    {
        if (next is null) return null;
        var idx = next.IndexOf("cursor=", StringComparison.Ordinal);
        return idx < 0 ? null : next[(idx + 7)..];
    }
}
```

### ConfluenceDataProviderExtensions.cs
```csharp
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var opts = new ConfluenceOptions { BaseUrl = baseUrl, Email = email };
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Confluence").AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Confluence");
            http.BaseAddress = new Uri(baseUrl);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            var api = RestService.For<IConfluenceApi>(http);
            return new ConfluenceDataProvider(api, opts);
        });
    }
}
```

### Test project csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders.Confluence\Rag.NET.DataProviders.Confluence.csproj" />
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

### Tests
```csharp
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Confluence;
using Refit;
using Xunit;

namespace Rag.NET.DataProviders.Confluence.Tests;

public sealed class ConfluenceDataProviderTests
{
    // Fake HTTP handler returning canned JSON for any request
    file sealed class FakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var key = responses.Keys.FirstOrDefault(k => url.Contains(k,
                StringComparison.Ordinal));
            if (key is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[key], Encoding.UTF8, "application/json")
            });
        }
    }

    private static ConfluenceDataProvider MakeProvider(
        string responseJson,
        ConfluenceOptions? options = null,
        string urlKey = "/wiki/rest/api/content")
    {
        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { [urlKey] = responseJson });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);
        return new ConfluenceDataProvider(api, options ?? new ConfluenceOptions
        {
            BaseUrl = "https://test.atlassian.net",
            Email   = "test@test.com"
        });
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsPages()
    {
        const string json = """
            {
              "results": [
                { "id": "123", "title": "Guide", "body": { "storage": { "value": "<p>Hello</p>" } }, "version": { "number": 3 } },
                { "id": "456", "title": "FAQ",   "body": { "storage": { "value": "<p>World</p>" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("Guide.md", results[0].FileName);
        Assert.Equal("3", results[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_UsesSearchEndpoint()
    {
        const string json = """
            {
              "results": [
                { "id": "789", "title": "Updated", "body": { "storage": { "value": "<p>New</p>" } }, "version": { "number": 5 } }
              ],
              "_links": {}
            }
            """;
        var opts = new ConfluenceOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            DeltaToken = "2026-01-01T00:00:00Z"
        };
        var sut = MakeProvider(json, opts, urlKey: "/wiki/rest/api/content/search");

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Updated.md", results[0].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMd()
    {
        // Extension filter applies to FileName (.md) — set non-md extension to exclude all
        const string json = """
            {
              "results": [
                { "id": "1", "title": "Doc", "body": { "storage": { "value": "x" } }, "version": { "number": 1 } }
              ],
              "_links": {}
            }
            """;
        var opts = new ConfluenceOptions
        {
            BaseUrl    = "https://test.atlassian.net",
            Email      = "test@test.com",
            Extensions = [".txt"]  // all pages are .md — nothing should match
        };
        var sut = MakeProvider(json, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ConfluenceDataProvider(null!, new ConfluenceOptions
            {
                BaseUrl = "https://test.atlassian.net",
                Email   = "t@t.com"
            }));
    }
}
```

### slnx additions
```xml
<!-- in /src/ folder -->
<Project Path="src/Rag.NET.DataProviders.Confluence/Rag.NET.DataProviders.Confluence.csproj" />
<!-- in /tests/ folder -->
<Project Path="tests/Rag.NET.DataProviders.Confluence.Tests/Rag.NET.DataProviders.Confluence.Tests.csproj" />
```

### Commit
```bash
git add src/Rag.NET.DataProviders.Confluence/ tests/Rag.NET.DataProviders.Confluence.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.Confluence connector"
```

---

## Task 2: Jira connector

**Files:**
- Create: `src/Rag.NET.DataProviders.Jira/Rag.NET.DataProviders.Jira.csproj`
- Create: `src/Rag.NET.DataProviders.Jira/JiraOptions.cs`
- Create: `src/Rag.NET.DataProviders.Jira/JiraApi.cs`
- Create: `src/Rag.NET.DataProviders.Jira/JiraDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Jira/JiraDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Jira.Tests/Rag.NET.DataProviders.Jira.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Jira.Tests/JiraDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

### csproj — same structure as Confluence, replace Confluence with Jira

### JiraOptions.cs
```csharp
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Jira;

public sealed class JiraOptions : CloudStorageOptions
{
    public required string BaseUrl   { get; init; }
    public required string Email     { get; init; }
    public string? ProjectKey        { get; init; }
    public string  Jql { get; init; } = "order by updated DESC";
}
```

### JiraApi.cs
```csharp
using System.Text.Json.Serialization;
using Refit;

namespace Rag.NET.DataProviders.Jira;

[Headers("Accept: application/json")]
internal interface IJiraApi
{
    [Get("/rest/api/3/search")]
    Task<JiraSearchResult> SearchAsync(
        [Query] string jql,
        [Query] int maxResults,
        [Query] int startAt,
        [Query] string fields = "summary,description,status,priority,assignee,comment,updated",
        CancellationToken cancellationToken = default);
}

internal sealed record JiraSearchResult(
    [property: JsonPropertyName("issues")] List<JiraIssue> Issues,
    [property: JsonPropertyName("total")]  int Total);

internal sealed record JiraIssue(
    [property: JsonPropertyName("id")]     string Id,
    [property: JsonPropertyName("key")]    string Key,
    [property: JsonPropertyName("fields")] JiraFields Fields);

internal sealed record JiraFields(
    [property: JsonPropertyName("summary")]     string Summary,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("status")]      JiraStatus Status,
    [property: JsonPropertyName("priority")]    JiraPriority? Priority,
    [property: JsonPropertyName("assignee")]    JiraUser? Assignee,
    [property: JsonPropertyName("comment")]     JiraCommentList? Comment,
    [property: JsonPropertyName("updated")]     string Updated);

internal sealed record JiraStatus([property: JsonPropertyName("name")] string Name);
internal sealed record JiraPriority([property: JsonPropertyName("name")] string Name);
internal sealed record JiraUser([property: JsonPropertyName("displayName")] string DisplayName);
internal sealed record JiraCommentList(
    [property: JsonPropertyName("comments")] List<JiraComment> Comments);
internal sealed record JiraComment(
    [property: JsonPropertyName("author")]  JiraUser Author,
    [property: JsonPropertyName("created")] string Created,
    [property: JsonPropertyName("body")]    string Body);
```

### JiraDataProvider.cs
```csharp
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Jira;

public sealed class JiraDataProvider : FileContentProviderBase
{
    private readonly IJiraApi _api;
    private readonly JiraOptions _options;

    public JiraDataProvider(IJiraApi api, JiraOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api     = api;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(BuildJql(), cancellationToken);

    private string BuildJql()
    {
        var parts = new List<string>();
        if (_options.ProjectKey is not null)
            parts.Add($"project = \"{_options.ProjectKey}\"");
        if (_options.DeltaToken is not null)
            parts.Add($"updated > \"{_options.DeltaToken}\"");
        parts.Add(_options.Jql);
        return string.Join(" AND ", parts);
    }

    private async IAsyncEnumerable<FileHandle> GetHandlesAsync(
        string jql,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int startAt = 0;
        const int maxResults = 50;

        while (true)
        {
            var result = await _api.SearchAsync(jql, maxResults, startAt,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var issue in result.Issues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ToHandle(issue);
            }

            startAt += result.Issues.Count;
            if (startAt >= result.Total) break;
        }
    }

    private static FileHandle ToHandle(JiraIssue issue)
    {
        var markdown = ToMarkdown(issue);
        return new FileHandle(
            Id:               issue.Key,
            FileName:         $"{issue.Key}.md",
            ETag:             issue.Fields.Updated,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private static string ToMarkdown(JiraIssue issue)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {issue.Fields.Summary}");
        sb.AppendLine();
        sb.Append($"**Status:** {issue.Fields.Status.Name}");
        if (issue.Fields.Priority is not null)
            sb.Append($"  **Priority:** {issue.Fields.Priority.Name}");
        if (issue.Fields.Assignee is not null)
            sb.Append($"  **Assignee:** {issue.Fields.Assignee.DisplayName}");
        sb.AppendLine();
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(issue.Fields.Description))
        {
            sb.AppendLine(issue.Fields.Description);
            sb.AppendLine();
        }
        var comments = issue.Fields.Comment?.Comments ?? [];
        if (comments.Count > 0)
        {
            sb.AppendLine("## Comments");
            foreach (var c in comments)
                sb.AppendLine($"**{c.Author.DisplayName}** ({c.Created}): {c.Body}");
        }
        return sb.ToString().TrimEnd();
    }
}
```

### JiraDataProviderExtensions.cs
```csharp
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Jira;

public static class JiraDataProviderExtensions
{
    public static IServiceCollection AddJiraDataProvider(
        this IServiceCollection services,
        string baseUrl,
        string email,
        string apiToken,
        Action<JiraOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var opts = new JiraOptions { BaseUrl = baseUrl, Email = email };
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Jira").AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Jira");
            http.BaseAddress = new Uri(baseUrl);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            return new JiraDataProvider(RestService.For<IJiraApi>(http), opts);
        });
    }
}
```

### Tests — same 4-test pattern as Confluence but with Jira JSON responses

Use this canned JSON for full traversal test:
```json
{
  "issues": [
    {
      "id": "10001",
      "key": "PROJ-1",
      "fields": {
        "summary": "Fix login bug",
        "description": "Users cannot login",
        "status": { "name": "In Progress" },
        "priority": { "name": "High" },
        "assignee": { "displayName": "Alice" },
        "comment": { "comments": [] },
        "updated": "2026-03-01T10:00:00Z"
      }
    }
  ],
  "total": 1
}
```

Assert: 1 result, `FileName == "PROJ-1.md"`, `ETag == "2026-03-01T10:00:00Z"`.

### Commit
```bash
git add src/Rag.NET.DataProviders.Jira/ tests/Rag.NET.DataProviders.Jira.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.Jira connector"
```

---

## Task 3: Notion connector

**Files:**
- Create: `src/Rag.NET.DataProviders.Notion/Rag.NET.DataProviders.Notion.csproj`
- Create: `src/Rag.NET.DataProviders.Notion/NotionOptions.cs`
- Create: `src/Rag.NET.DataProviders.Notion/NotionApi.cs`
- Create: `src/Rag.NET.DataProviders.Notion/NotionDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Notion/NotionDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Notion.Tests/Rag.NET.DataProviders.Notion.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Notion.Tests/NotionDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

### NotionOptions.cs
```csharp
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Notion;

public sealed class NotionOptions : CloudStorageOptions
{
    public string? DatabaseId { get; init; }
}
```

### NotionApi.cs
Notion uses POST for search and GET for block children. Auth header: `Authorization: Bearer {token}` and `Notion-Version: 2022-06-28`.

```csharp
using System.Text.Json.Serialization;
using Refit;

namespace Rag.NET.DataProviders.Notion;

[Headers("Accept: application/json", "Notion-Version: 2022-06-28")]
internal interface INotionApi
{
    [Post("/v1/search")]
    Task<NotionSearchResult> SearchAsync(
        [Body] NotionSearchRequest request,
        CancellationToken cancellationToken = default);

    [Get("/v1/blocks/{blockId}/children")]
    Task<NotionBlockList> GetBlockChildrenAsync(
        string blockId,
        [Query] int page_size = 100,
        [Query] string? start_cursor = null,
        CancellationToken cancellationToken = default);
}

internal sealed record NotionSearchRequest(
    [property: JsonPropertyName("filter")]       NotionFilter Filter,
    [property: JsonPropertyName("page_size")]    int PageSize,
    [property: JsonPropertyName("start_cursor")] string? StartCursor,
    [property: JsonPropertyName("sort")]         NotionSort? Sort = null);

internal sealed record NotionFilter(
    [property: JsonPropertyName("property")] string Property,
    [property: JsonPropertyName("value")]    string Value);

internal sealed record NotionSort(
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("timestamp")] string Timestamp);

internal sealed record NotionSearchResult(
    [property: JsonPropertyName("results")]     List<NotionPage> Results,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")]    bool HasMore);

internal sealed record NotionPage(
    [property: JsonPropertyName("id")]               string Id,
    [property: JsonPropertyName("last_edited_time")] string LastEditedTime,
    [property: JsonPropertyName("properties")]       Dictionary<string, NotionProperty> Properties);

internal sealed record NotionProperty(
    [property: JsonPropertyName("title")] List<NotionRichText>? Title);

internal sealed record NotionRichText(
    [property: JsonPropertyName("plain_text")] string PlainText);

internal sealed record NotionBlockList(
    [property: JsonPropertyName("results")]     List<NotionBlock> Results,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")]    bool HasMore);

internal sealed record NotionBlock(
    [property: JsonPropertyName("type")]             string Type,
    [property: JsonPropertyName("paragraph")]        NotionBlockContent? Paragraph,
    [property: JsonPropertyName("heading_1")]        NotionBlockContent? Heading1,
    [property: JsonPropertyName("heading_2")]        NotionBlockContent? Heading2,
    [property: JsonPropertyName("heading_3")]        NotionBlockContent? Heading3,
    [property: JsonPropertyName("bulleted_list_item")] NotionBlockContent? BulletedListItem,
    [property: JsonPropertyName("numbered_list_item")] NotionBlockContent? NumberedListItem,
    [property: JsonPropertyName("code")]             NotionBlockContent? Code,
    [property: JsonPropertyName("quote")]            NotionBlockContent? Quote);

internal sealed record NotionBlockContent(
    [property: JsonPropertyName("rich_text")] List<NotionRichText>? RichText,
    [property: JsonPropertyName("language")]  string? Language);
```

### NotionDataProvider.cs
```csharp
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Notion;

public sealed class NotionDataProvider : FileContentProviderBase
{
    private readonly INotionApi _api;
    private readonly NotionOptions _options;

    public NotionDataProvider(INotionApi api, NotionOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api     = api;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        NotionSort? sort = _options.DeltaToken is not null
            ? new NotionSort("descending", "last_edited_time")
            : null;

        string? cursor = null;
        do
        {
            var filter = _options.DatabaseId is not null
                ? new NotionFilter("object", "database")
                : new NotionFilter("object", "page");

            var result = await _api.SearchAsync(
                new NotionSearchRequest(filter, 100, cursor, sort),
                cancellationToken).ConfigureAwait(false);

            foreach (var page in result.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Delta: skip pages not modified after DeltaToken
                if (_options.DeltaToken is not null
                    && string.Compare(page.LastEditedTime, _options.DeltaToken,
                        StringComparison.Ordinal) <= 0)
                    continue;

                var blocks  = await FetchBlocksAsync(page.Id, cancellationToken).ConfigureAwait(false);
                var title   = GetTitle(page);
                var markdown = BlocksToMarkdown(title, blocks);

                yield return new FileHandle(
                    Id:               page.Id,
                    FileName:         $"{title}.md",
                    ETag:             page.LastEditedTime,
                    OpenContentAsync: _ => Task.FromResult<Stream>(
                        new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
            }

            cursor = result.HasMore ? result.NextCursor : null;
        }
        while (cursor is not null);
    }

    private async Task<List<NotionBlock>> FetchBlocksAsync(
        string pageId, CancellationToken cancellationToken)
    {
        var all = new List<NotionBlock>();
        string? cursor = null;
        do
        {
            var page = await _api.GetBlockChildrenAsync(pageId, start_cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Results);
            cursor = page.HasMore ? page.NextCursor : null;
        }
        while (cursor is not null);
        return all;
    }

    private static string GetTitle(NotionPage page)
    {
        foreach (var prop in page.Properties.Values)
        {
            if (prop.Title is { Count: > 0 })
                return string.Concat(prop.Title.Select(t => t.PlainText));
        }
        return page.Id;
    }

    private static string BlocksToMarkdown(string title, List<NotionBlock> blocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        foreach (var block in blocks)
        {
            var text = GetRichText(block);
            sb.AppendLine(block.Type switch
            {
                "heading_1"          => $"# {text}",
                "heading_2"          => $"## {text}",
                "heading_3"          => $"### {text}",
                "bulleted_list_item" => $"- {text}",
                "numbered_list_item" => $"1. {text}",
                "code"               => $"```{block.Code?.Language ?? string.Empty}\n{text}\n```",
                "quote"              => $"> {text}",
                _                    => text
            });
        }
        return sb.ToString().TrimEnd();
    }

    private static string GetRichText(NotionBlock block)
    {
        var content = block.Type switch
        {
            "paragraph"          => block.Paragraph,
            "heading_1"          => block.Heading1,
            "heading_2"          => block.Heading2,
            "heading_3"          => block.Heading3,
            "bulleted_list_item" => block.BulletedListItem,
            "numbered_list_item" => block.NumberedListItem,
            "code"               => block.Code,
            "quote"              => block.Quote,
            _                    => null
        };
        return content?.RichText is null ? string.Empty
            : string.Concat(content.RichText.Select(t => t.PlainText));
    }
}
```

### NotionDataProviderExtensions.cs
```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Notion;

public static class NotionDataProviderExtensions
{
    public static IServiceCollection AddNotionDataProvider(
        this IServiceCollection services,
        string integrationToken,
        Action<NotionOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(integrationToken);

        var opts = new NotionOptions();
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Notion").AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Notion");
            http.BaseAddress = new Uri("https://api.notion.com");
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", integrationToken);
            return new NotionDataProvider(RestService.For<INotionApi>(http), opts);
        });
    }
}
```

### Tests — 4 tests using fake HTTP handler, canned JSON

Full traversal JSON:
```json
{
  "results": [
    {
      "id": "page-1",
      "last_edited_time": "2026-03-01T10:00:00.000Z",
      "properties": { "title": { "title": [{ "plain_text": "My Page" }] } }
    }
  ],
  "has_more": false
}
```

Block children JSON (for `GET /v1/blocks/page-1/children`):
```json
{
  "results": [{ "type": "paragraph", "paragraph": { "rich_text": [{ "plain_text": "Hello world" }] } }],
  "has_more": false
}
```

Assert: 1 result, `FileName == "My Page.md"`, content contains "Hello world".

### Commit
```bash
git add src/Rag.NET.DataProviders.Notion/ tests/Rag.NET.DataProviders.Notion.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.Notion connector"
```

---

## Task 4: Asana connector

**Files:**
- Create: `src/Rag.NET.DataProviders.Asana/Rag.NET.DataProviders.Asana.csproj`
- Create: `src/Rag.NET.DataProviders.Asana/AsanaOptions.cs`
- Create: `src/Rag.NET.DataProviders.Asana/AsanaApi.cs`
- Create: `src/Rag.NET.DataProviders.Asana/AsanaDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Asana/AsanaDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Asana.Tests/Rag.NET.DataProviders.Asana.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Asana.Tests/AsanaDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

### AsanaOptions.cs
```csharp
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Asana;

public sealed class AsanaOptions : CloudStorageOptions
{
    public required string WorkspaceGid { get; init; }
    public string? ProjectGid           { get; init; }
}
```

### AsanaApi.cs
```csharp
using System.Text.Json.Serialization;
using Refit;

namespace Rag.NET.DataProviders.Asana;

[Headers("Accept: application/json")]
internal interface IAsanaApi
{
    [Get("/api/1.0/tasks")]
    Task<AsanaTaskList> GetWorkspaceTasksAsync(
        [Query] string workspace,
        [Query] string opt_fields,
        [Query] int limit,
        [Query] string? offset = null,
        [Query] string? modified_since = null,
        CancellationToken cancellationToken = default);

    [Get("/api/1.0/projects/{projectGid}/tasks")]
    Task<AsanaTaskList> GetProjectTasksAsync(
        string projectGid,
        [Query] string opt_fields,
        [Query] int limit,
        [Query] string? offset = null,
        [Query] string? modified_since = null,
        CancellationToken cancellationToken = default);

    [Get("/api/1.0/tasks/{taskGid}/subtasks")]
    Task<AsanaTaskList> GetSubtasksAsync(
        string taskGid,
        [Query] string opt_fields = "gid,name",
        CancellationToken cancellationToken = default);
}

internal sealed record AsanaTaskList(
    [property: JsonPropertyName("data")]      List<AsanaTask> Data,
    [property: JsonPropertyName("next_page")] AsanaNextPage?  NextPage);

internal sealed record AsanaTask(
    [property: JsonPropertyName("gid")]          string Gid,
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("notes")]        string? Notes,
    [property: JsonPropertyName("due_on")]       string? DueOn,
    [property: JsonPropertyName("completed")]    bool Completed,
    [property: JsonPropertyName("assignee")]     AsanaAssignee? Assignee,
    [property: JsonPropertyName("modified_at")]  string ModifiedAt);

internal sealed record AsanaAssignee([property: JsonPropertyName("name")] string Name);
internal sealed record AsanaNextPage([property: JsonPropertyName("offset")] string Offset);
```

### AsanaDataProvider.cs
```csharp
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Asana;

public sealed class AsanaDataProvider : FileContentProviderBase
{
    private readonly IAsanaApi _api;
    private readonly AsanaOptions _options;

    private const string OptFields =
        "gid,name,notes,due_on,completed,assignee.name,modified_at";

    public AsanaDataProvider(IAsanaApi api, AsanaOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api     = api;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? offset = null;
        var modifiedSince = _options.DeltaToken;

        do
        {
            AsanaTaskList result;
            if (_options.ProjectGid is not null)
                result = await _api.GetProjectTasksAsync(
                    _options.ProjectGid, OptFields, 100, offset, modifiedSince,
                    cancellationToken).ConfigureAwait(false);
            else
                result = await _api.GetWorkspaceTasksAsync(
                    _options.WorkspaceGid, OptFields, 100, offset, modifiedSince,
                    cancellationToken).ConfigureAwait(false);

            foreach (var task in result.Data)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subtasks = await _api.GetSubtasksAsync(task.Gid,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                yield return ToHandle(task, subtasks.Data);
            }

            offset = result.NextPage?.Offset;
        }
        while (offset is not null);
    }

    private static FileHandle ToHandle(AsanaTask task, List<AsanaTask> subtasks)
    {
        var markdown = ToMarkdown(task, subtasks);
        return new FileHandle(
            Id:               task.Gid,
            FileName:         $"{task.Name}.md",
            ETag:             task.ModifiedAt,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private static string ToMarkdown(AsanaTask task, List<AsanaTask> subtasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {task.Name}");
        sb.AppendLine();
        if (task.DueOn is not null)     sb.Append($"**Due:** {task.DueOn}  ");
        if (task.Assignee is not null)  sb.Append($"**Assignee:** {task.Assignee.Name}  ");
        sb.AppendLine($"**Completed:** {task.Completed}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(task.Notes))
        {
            sb.AppendLine(task.Notes);
            sb.AppendLine();
        }
        if (subtasks.Count > 0)
        {
            sb.AppendLine("## Subtasks");
            foreach (var s in subtasks)
                sb.AppendLine($"- {s.Name}");
        }
        return sb.ToString().TrimEnd();
    }
}
```

### AsanaDataProviderExtensions.cs — two overloads: string PAT and ITokenProvider
```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Asana;

public static class AsanaDataProviderExtensions
{
    public static IServiceCollection AddAsanaDataProvider(
        this IServiceCollection services,
        string personalAccessToken,
        string workspaceGid,
        Action<AsanaOptions>? configure = null)
        => services.AddAsanaDataProvider(new StaticTokenProvider(personalAccessToken), workspaceGid, configure);

    public static IServiceCollection AddAsanaDataProvider(
        this IServiceCollection services,
        ITokenProvider tokenProvider,
        string workspaceGid,
        Action<AsanaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceGid);

        var opts = new AsanaOptions { WorkspaceGid = workspaceGid };
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Asana").AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Asana");
            http.BaseAddress = new Uri("https://app.asana.com");
            // Token is fetched per-request via a delegating handler — for simplicity
            // in this connector, we fetch it once at construction (PAT doesn't expire)
            var token = tokenProvider.GetTokenAsync().AsTask().GetAwaiter().GetResult();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return new AsanaDataProvider(RestService.For<IAsanaApi>(http), opts);
        });
    }
}
```

### Tests — 4 tests using fake HTTP handler with canned Asana JSON

Full traversal canned JSON:
```json
{
  "data": [
    {
      "gid": "task-1",
      "name": "Fix bug",
      "notes": "Reproduce on dev",
      "due_on": "2026-04-01",
      "completed": false,
      "assignee": { "name": "Bob" },
      "modified_at": "2026-03-01T10:00:00Z"
    }
  ]
}
```

Subtasks response: `{ "data": [{ "gid": "sub-1", "name": "Write test" }] }`

Assert: 1 result, `FileName == "Fix bug.md"`, content contains "## Subtasks".

### slnx additions
```xml
<!-- in /src/ folder -->
<Project Path="src/Rag.NET.DataProviders.Asana/Rag.NET.DataProviders.Asana.csproj" />
<!-- in /tests/ folder -->
<Project Path="tests/Rag.NET.DataProviders.Asana.Tests/Rag.NET.DataProviders.Asana.Tests.csproj" />
```

### Commit
```bash
git add src/Rag.NET.DataProviders.Asana/ tests/Rag.NET.DataProviders.Asana.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.Asana connector"
```
