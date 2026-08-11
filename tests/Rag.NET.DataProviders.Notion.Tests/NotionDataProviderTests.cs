using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Notion;
using Rag.NET.DataProviders.Testing;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Rest;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.Notion.Tests;

public sealed class NotionDataProviderTests
{
    private static readonly IRestSerializer JsonSerializer = new SystemTextJsonSerializer();

    private static NotionDataProvider MakeProvider(
        HttpMessageHandler handler, NotionOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
        var api = new NotionApiClient(http, JsonSerializer);
        return new NotionDataProvider(api, options ?? new NotionOptions());
    }

    private static NotionDataProvider MakeProvider(
        Dictionary<string, string> responses,
        NotionOptions? options = null)
    {
        var handler = new FakeHandler(responses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
        var api = new NotionApiClient(http, JsonSerializer);
        return new NotionDataProvider(api, options ?? new NotionOptions());
    }

    private static async Task<string> ReadContentAsync(FileEntry entry)
    {
        await using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsPageWithContent()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-1",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": {
                    "title": {
                      "title": [{ "plain_text": "My Page" }]
                    }
                  }
                }
              ],
              "has_more": false
            }
            """;

        const string blocksJson = """
            {
              "results": [
                {
                  "type": "paragraph",
                  "paragraph": {
                    "rich_text": [{ "plain_text": "Hello world" }]
                  }
                }
              ],
              "has_more": false
            }
            """;

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"]   = searchJson,
            ["page-1"]       = blocksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("My Page.md", results[0].Value.FileName);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("Hello world", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_HostilePageTitle_Sanitized()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-9",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": {
                    "title": {
                      "title": [{ "plain_text": "Q1 Plan: Draft/Final" }]
                    }
                  }
                }
              ],
              "has_more": false
            }
            """;
        const string blocksJson = """{ "results": [], "has_more": false }""";

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"] = searchJson,
            ["page-9"]     = blocksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("Q1 Plan_ Draft_Final.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_SkipsOldPages()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-2",
                  "last_edited_time": "2026-03-01T00:00:00.000Z",
                  "properties": {
                    "title": {
                      "title": [{ "plain_text": "Old Page" }]
                    }
                  }
                }
              ],
              "has_more": false
            }
            """;

        const string blocksJson = """{ "results": [], "has_more": false }""";

        var opts = new NotionOptions { DeltaToken = "2026-03-15T00:00:00.000Z" };
        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"] = searchJson,
            ["page-2"]     = blocksJson
        }, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMd()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-3",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": {
                    "title": {
                      "title": [{ "plain_text": "Some Page" }]
                    }
                  }
                }
              ],
              "has_more": false
            }
            """;

        const string blocksJson = """{ "results": [], "has_more": false }""";

        var opts = new NotionOptions { Extensions = [".txt"] };
        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"] = searchJson,
            ["page-3"]     = blocksJson
        }, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NotionDataProvider(null!, new NotionOptions()));
    }

    [Fact]
    public async Task GetFilesAsync_AllBlockTypes_MarkdownRendersCorrectly()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-blocks",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": {
                    "title": { "title": [{ "plain_text": "Block Test" }] }
                  }
                }
              ],
              "has_more": false
            }
            """;

        const string blocksJson = """
            {
              "results": [
                { "type": "heading_1",  "heading_1":  { "rich_text": [{ "plain_text": "H1" }] } },
                { "type": "heading_2",  "heading_2":  { "rich_text": [{ "plain_text": "H2" }] } },
                { "type": "heading_3",  "heading_3":  { "rich_text": [{ "plain_text": "H3" }] } },
                { "type": "paragraph",  "paragraph":  { "rich_text": [{ "plain_text": "Para" }] } },
                { "type": "bulleted_list_item", "bulleted_list_item": { "rich_text": [{ "plain_text": "Bullet" }] } },
                { "type": "numbered_list_item", "numbered_list_item": { "rich_text": [{ "plain_text": "Numbered" }] } },
                { "type": "code",       "code":       { "rich_text": [{ "plain_text": "x = 1" }], "language": "python" } },
                { "type": "quote",      "quote":      { "rich_text": [{ "plain_text": "Quote" }] } },
                { "type": "divider" }
              ],
              "has_more": false
            }
            """;

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"]  = searchJson,
            ["page-blocks"] = blocksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("# H1", content, StringComparison.Ordinal);
        Assert.Contains("## H2", content, StringComparison.Ordinal);
        Assert.Contains("### H3", content, StringComparison.Ordinal);
        Assert.Contains("Para", content, StringComparison.Ordinal);
        Assert.Contains("- Bullet", content, StringComparison.Ordinal);
        Assert.Contains("1. Numbered", content, StringComparison.Ordinal);
        Assert.Contains("```python\nx = 1\n```", content, StringComparison.Ordinal);
        Assert.Contains("> Quote", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NoTitleProperty_FallsBackToPageId()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-no-title",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": {
                    "status": {}
                  }
                }
              ],
              "has_more": false
            }
            """;

        const string blocksJson = """{ "results": [], "has_more": false }""";

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"]      = searchJson,
            ["page-no-title"]   = blocksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("page-no-title.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_MultipleRichTexts_Concatenated()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-multi",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": {
                    "title": {
                      "title": [
                        { "plain_text": "Hello " },
                        { "plain_text": "World" }
                      ]
                    }
                  }
                }
              ],
              "has_more": false
            }
            """;

        const string blocksJson = """{ "results": [], "has_more": false }""";

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"]  = searchJson,
            ["page-multi"]  = blocksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("Hello World.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_SearchPagination_FetchesAllPages()
    {
        const string searchPage1 = """
            {
              "results": [
                {
                  "id": "page-a",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": { "title": { "title": [{ "plain_text": "Page A" }] } }
                }
              ],
              "has_more": true,
              "next_cursor": "cursor-2"
            }
            """;

        const string searchPage2 = """
            {
              "results": [
                {
                  "id": "page-b",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": { "title": { "title": [{ "plain_text": "Page B" }] } }
                }
              ],
              "has_more": false
            }
            """;

        const string blocksJson = """{ "results": [], "has_more": false }""";

        var handler = new SearchPaginationHandler(searchPage1, searchPage2, blocksJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
        var api = new NotionApiClient(http, JsonSerializer);
        var sut = new NotionDataProvider(api, new NotionOptions());

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("Page A.md", results[0].Value.FileName);
        Assert.Equal("Page B.md", results[1].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaStopsAtOldPage()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-new",
                  "last_edited_time": "2026-03-20T00:00:00.000Z",
                  "properties": { "title": { "title": [{ "plain_text": "New Page" }] } }
                },
                {
                  "id": "page-old",
                  "last_edited_time": "2026-03-01T00:00:00.000Z",
                  "properties": { "title": { "title": [{ "plain_text": "Old Page" }] } }
                }
              ],
              "has_more": false
            }
            """;

        const string blocksJson = """{ "results": [], "has_more": false }""";

        var opts = new NotionOptions { DeltaToken = "2026-03-10T00:00:00.000Z" };
        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"] = searchJson,
            ["page-new"]   = blocksJson,
            ["page-old"]   = blocksJson
        }, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("New Page.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_EmptySearch_YieldsNothing()
    {
        const string searchJson = """
            {
              "results": [],
              "has_more": false
            }
            """;

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"] = searchJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        // Use a handler that honours cancellation properly by checking the token.
        var handler = new CancellationAwareHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
        var api = new NotionApiClient(http, JsonSerializer);
        var sut = new NotionDataProvider(api, new NotionOptions());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GetFilesAsync(cts.Token).ToListAsync(cts.Token));
    }

    [Fact]
    public async Task GetFilesAsync_BlockPagination_FetchesAllBlocks()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-bp",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": { "title": { "title": [{ "plain_text": "BP Page" }] } }
                }
              ],
              "has_more": false
            }
            """;

        const string blocksPage1 = """
            {
              "results": [
                { "type": "paragraph", "paragraph": { "rich_text": [{ "plain_text": "Block1" }] } }
              ],
              "has_more": true,
              "next_cursor": "block-cursor-2"
            }
            """;

        const string blocksPage2 = """
            {
              "results": [
                { "type": "paragraph", "paragraph": { "rich_text": [{ "plain_text": "Block2" }] } }
              ],
              "has_more": false
            }
            """;

        // Block pagination uses query params: start_cursor=block-cursor-2
        // First call has no start_cursor, second call has start_cursor in URL
        var handler = new BlockPaginationHandler(searchJson, blocksPage1, blocksPage2);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
        var api = new NotionApiClient(http, JsonSerializer);
        var sut = new NotionDataProvider(api, new NotionOptions());

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var content = await ReadContentAsync(results[0].Value);
        Assert.Contains("Block1", content, StringComparison.Ordinal);
        Assert.Contains("Block2", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_SearchHttpFailure_PropagatesError()
    {
        var handler = new StatusCodeHandler(HttpStatusCode.InternalServerError);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
        var api = new NotionApiClient(http, JsonSerializer);
        var sut = new NotionDataProvider(api, new NotionOptions());

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.True(results[0].IsFailure);
        var error = Assert.IsType<RagError.HttpFailed>(results[0].Error);
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    [Fact]
    public void AddNotionDataProvider_DefaultBaseUrl_UsesProductionUrl()
    {
        var services = new ServiceCollection();
        services.AddNotionDataProvider(integrationToken: "test-token");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public void AddNotionDataProvider_CustomBaseUrl_OverridesDefault()
    {
        var services = new ServiceCollection();
        services.AddNotionDataProvider(integrationToken: "test-token", baseUrl: "https://custom.example.com");

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public async Task GetFilesAsync_Metadata_PinsPageKeys()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-md",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": { "title": { "title": [{ "plain_text": "My Page" }] } }
                }
              ],
              "has_more": false
            }
            """;
        const string blocksJson = """{ "results": [], "has_more": false }""";

        // Unscoped: /v1/search returns every accessible page, so no database parentage is known.
        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"] = searchJson,
            ["page-md"]    = blocksJson
        }, new NotionOptions());

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("page-md", metadata["page_id"]);
        // updated_at is no longer a plain tag — it is the typed FileHandle.UpdatedAt, asserted in
        // GetFilesAsync_LastEditedTime_IsTypedAndSurfacesAsUpdatedAtChunkTag below.
        Assert.False(metadata.ContainsKey("updated_at"));
        _ = Assert.Single(metadata);

        // Without DatabaseId the provider searches, and /v1/search results carry no parent
        // object — so tagging them with a database id would claim a parentage the API never
        // confirmed. Scoped ingestion is the opposite case and is asserted separately.
        Assert.False(metadata.ContainsKey("database_id"));
    }

    // -------------------------------------------------------------------------
    // DatabaseId scoping (issue #108)
    // -------------------------------------------------------------------------

    private const string OnePageJson = """
        {
          "results": [
            {
              "id": "page-db",
              "last_edited_time": "2026-03-01T10:00:00.000Z",
              "properties": { "title": { "title": [{ "plain_text": "Scoped Page" }] } }
            }
          ],
          "has_more": false
        }
        """;

    /// <summary>
    /// With <see cref="NotionOptions.DatabaseId"/> set the provider queries the database endpoint
    /// instead of searching.
    /// <para>
    /// The fake serves <b>only</b> <c>/v1/databases/db-42/query</c>, so a provider that still
    /// called <c>/v1/search</c> gets a 404 and yields a failure — the endpoint choice is proven by
    /// the absence of the other route rather than by inspecting a URL and hoping.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_WithDatabaseId_QueriesTheDatabaseInsteadOfSearching()
    {
        var handler = new RecordingHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/databases/db-42/query"] = OnePageJson,
            ["page-db"] = """{ "results": [], "has_more": false }""",
        });
        var sut = MakeProvider(handler, new NotionOptions { DatabaseId = "db-42" });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(results[0].IsSuccess, "the provider did not reach the database endpoint");
        Assert.Contains(handler.Urls, url => url.Contains("/v1/databases/db-42/query", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Urls, url => url.Contains("/v1/search", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scoped ingestion tags every page with <c>database_id</c>, which is true of all of them
    /// because a database query returns only that database's pages. Unscoped ingestion does not —
    /// asserted in <c>GetFilesAsync_Metadata_*</c> above.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_WithDatabaseId_TagsEachPageWithTheDatabaseItCameFrom()
    {
        var handler = new RecordingHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/databases/db-42/query"] = OnePageJson,
            ["page-db"] = """{ "results": [], "has_more": false }""",
        });
        var sut = MakeProvider(handler, new NotionOptions { DatabaseId = "db-42" });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var metadata = Assert.Single(results).Value.Metadata!;
        Assert.Equal("db-42", metadata["database_id"]);
        Assert.Equal("page-db", metadata["page_id"]);
    }

    /// <summary>
    /// The delta sort reaches the database endpoint as <c>sorts</c>, an array.
    /// <para>
    /// <c>/v1/search</c> takes a single <c>sort</c> object and the database query takes a
    /// <c>sorts</c> array. Notion accepts and <b>ignores</b> the wrong one, so sending search's
    /// shape here would leave the query unsorted while everything still returned 200 — and delta
    /// ingestion, which stops at the first page older than the token, would then stop somewhere
    /// arbitrary. That failure is invisible in results, so it is asserted on the request.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_WithDatabaseIdAndDeltaToken_SendsSortsAsAnArray()
    {
        var handler = new RecordingHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/databases/db-42/query"] = """{ "results": [], "has_more": false }""",
        });
        var sut = MakeProvider(
            handler,
            new NotionOptions { DatabaseId = "db-42", DeltaToken = "2026-01-01T00:00:00.000Z" });

        _ = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("\"sorts\":[", body, StringComparison.Ordinal);
        Assert.Contains("last_edited_time", body, StringComparison.Ordinal);
        Assert.Contains("descending", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Phase 4.10 Task 4: <c>last_edited_time</c> used to be written as a plain
    /// <c>metadata["updated_at"]</c> tag; it now becomes <c>FileHandle.UpdatedAt</c> and reaches
    /// <c>FileEntry.UpdatedAt</c> unchanged. This pins both that the connector still produces the
    /// timestamp at all, and that it still surfaces as the reserved chunk tag downstream.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_LastEditedTime_IsTypedAndSurfacesAsUpdatedAtChunkTag()
    {
        const string searchJson = """
            {
              "results": [
                {
                  "id": "page-ts",
                  "last_edited_time": "2026-03-01T10:00:00.000Z",
                  "properties": { "title": { "title": [{ "plain_text": "Timestamp Page" }] } }
                }
              ],
              "has_more": false
            }
            """;
        const string blocksJson = """{ "results": [], "has_more": false }""";

        var sut = MakeProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"] = searchJson,
            ["page-ts"]    = blocksJson
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(results).Value;
        var expected = DateTime.Parse(
            "2026-03-01T10:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(expected, entry.UpdatedAt);
        Assert.False(entry.Metadata!.ContainsKey("updated_at"));

        await UpdatedAtChunkTagAssertion.AssertSurfacesAsChunkTagAsync(
            entry.UpdatedAt, TestContext.Current.CancellationToken);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure — fake HTTP handlers
// ---------------------------------------------------------------------------

/// <summary>
/// <see cref="FakeHandler"/> that also keeps every request URL and body, so a test can assert
/// what was sent rather than only what came back.
/// </summary>
file sealed class RecordingHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    public List<string> Urls { get; } = [];

    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Urls.Add(url);
        Bodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        var key = responses.Keys.FirstOrDefault(k => url.Contains(k, StringComparison.Ordinal));
        if (key is null)
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses[key], Encoding.UTF8, "application/json"),
        };
    }
}

file sealed class FakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var key = responses.Keys.FirstOrDefault(k => url.Contains(k, StringComparison.Ordinal));
        if (key is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses[key], Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Handler that properly checks the cancellation token and throws
/// OperationCanceledException when it is already cancelled.
/// </summary>
file sealed class CancellationAwareHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

/// <summary>
/// Handler that always returns a fixed status code.
/// </summary>
file sealed class StatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(statusCode));
}

/// <summary>
/// Handler for search pagination tests. Returns different search results based
/// on whether the POST body contains a start_cursor (i.e. second page request).
/// </summary>
file sealed class SearchPaginationHandler(
    string firstSearchPage,
    string secondSearchPage,
    string blocksJson) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        if (url.Contains("/v1/search", StringComparison.Ordinal))
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var json = body.Contains("cursor-2", StringComparison.Ordinal)
                ? secondSearchPage
                : firstSearchPage;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        // Block children requests
        if (url.Contains("/v1/blocks/", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(blocksJson, Encoding.UTF8, "application/json")
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// Handler for block pagination tests. Returns different block results based
/// on whether the URL contains a start_cursor query parameter.
/// </summary>
file sealed class BlockPaginationHandler(
    string searchJson,
    string firstBlocksPage,
    string secondBlocksPage) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        if (url.Contains("/v1/search", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson, Encoding.UTF8, "application/json")
            });
        }

        if (url.Contains("/v1/blocks/", StringComparison.Ordinal))
        {
            var json = url.Contains("block-cursor-2", StringComparison.Ordinal)
                ? secondBlocksPage
                : firstBlocksPage;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
