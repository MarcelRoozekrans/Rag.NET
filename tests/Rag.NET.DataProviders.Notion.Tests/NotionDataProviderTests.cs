using System.Net;
using System.Text;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Notion;
using Refit;
using Xunit;

namespace Rag.NET.DataProviders.Notion.Tests;

public sealed class NotionDataProviderTests
{
    private static NotionDataProvider MakeProvider(
        Dictionary<string, string> responses,
        NotionOptions? options = null)
    {
        var handler = new FakeHandler(responses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
        var api = RestService.For<INotionApi>(http);
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

        Assert.Single(results);
        Assert.Equal("My Page.md", results[0].FileName);
        var content = await ReadContentAsync(results[0]);
        Assert.Contains("Hello world", content, StringComparison.Ordinal);
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
}

// ---------------------------------------------------------------------------
// Test infrastructure — fake HTTP handler
// ---------------------------------------------------------------------------

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
