using System.Net;
using System.Text;
using System.Text.Json;
using AirtableApiClient;
using NSubstitute;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Airtable;
using Xunit;

namespace Rag.NET.DataProviders.Airtable.Tests;

public sealed class AirtableDataProviderTests
{
    private static AirtableRecord MakeRecord(string id, Dictionary<string, object> fields)
    {
        var record = new AirtableRecord { Id = id };
        foreach (var (key, value) in fields)
            record.Fields[key] = value;
        return record;
    }

    private static JsonElement Json(string raw) =>
        JsonDocument.Parse(raw).RootElement.Clone();

    private static AirtableListRecordsResponse MakeResponse(
        AirtableRecord[] records, string? offset = null)
    {
        var list = new AirtableRecordList { Records = records, Offset = offset };
        return new AirtableListRecordsResponse(list);
    }

    private static AirtableDataProvider MakeProvider(
        IAirtableClient client,
        AirtableOptions? options = null,
        HttpClient? http = null)
    {
        return new AirtableDataProvider(
            client,
            http ?? new HttpClient(),
            options ?? new AirtableOptions { BaseId = "appTEST", TableName = "Tasks" });
    }

    private static async Task<string> ReadContentAsync(FileEntry entry)
    {
        await using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AirtableDataProvider(
                null!,
                new HttpClient(),
                new AirtableOptions { BaseId = "appTEST", TableName = "Tasks" }));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsRowsAndAttachments()
    {
        // A record with a Name field, a Status field, and an Attachments field containing one file.
        var attachmentJson = Json("""
            [
                {
                    "id": "att001",
                    "url": "https://dl.airtable.test/photo.png",
                    "filename": "photo.png",
                    "type": "image/png"
                }
            ]
            """);

        var record = MakeRecord("rec001", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"]        = Json("\"Design doc\""),
            ["Status"]      = Json("\"In Progress\""),
            ["Attachments"] = attachmentJson
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        // Set up an HTTP handler that serves the attachment download.
        var handler = new FakeDownloadHandler("attachment-bytes");
        using var http = new HttpClient(handler);
        var sut = MakeProvider(client, http: http);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Should yield 2 entries: 1 markdown record + 1 attachment.
        Assert.Equal(2, results.Count);

        // First entry: the markdown record.
        Assert.Equal("rec001", results[0].Id);
        Assert.Equal("Design doc.md", results[0].FileName);
        var markdown = await ReadContentAsync(results[0]);
        Assert.Contains("# Design doc", markdown, StringComparison.Ordinal);
        Assert.Contains("Status", markdown, StringComparison.Ordinal);
        Assert.Contains("In Progress", markdown, StringComparison.Ordinal);

        // Second entry: the attachment.
        Assert.Equal("rec001/Attachments/photo.png", results[1].Id);
        Assert.Equal("photo.png", results[1].FileName);
        var attachmentContent = await ReadContentAsync(results[1]);
        Assert.Equal("attachment-bytes", attachmentContent);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaWithLastModifiedField_UsesFilterFormula()
    {
        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync(
                "Tasks",
                null,
                "LAST_MODIFIED_TIME()>'2026-03-01T00:00:00Z'",
                null,
                Arg.Any<CancellationToken>())
            .Returns(MakeResponse([]));

        var opts = new AirtableOptions
        {
            BaseId                = "appTEST",
            TableName             = "Tasks",
            LastModifiedFieldName = "Modified",
            DeltaToken            = "2026-03-01T00:00:00Z"
        };
        var sut = MakeProvider(client, opts);

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Verify the formula was passed correctly.
        await client.Received(1).ListRecordsAsync(
            "Tasks",
            null,
            "LAST_MODIFIED_TIME()>'2026-03-01T00:00:00Z'",
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatching()
    {
        var record = MakeRecord("rec002", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = Json("\"Some record\"")
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        // Only allow .txt files — the markdown .md file should be excluded.
        var opts = new AirtableOptions
        {
            BaseId     = "appTEST",
            TableName  = "Tasks",
            Extensions = [".txt"]
        };
        var sut = MakeProvider(client, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_NoLastModifiedFieldName_IgnoresDeltaToken()
    {
        // DeltaToken is set but LastModifiedFieldName is null → full traversal (no formula).
        var record = MakeRecord("rec003", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = Json("\"Full scan record\"")
        });

        var client = Substitute.For<IAirtableClient>();
        // Expect a call with null formula (full traversal).
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var opts = new AirtableOptions
        {
            BaseId                = "appTEST",
            TableName             = "Tasks",
            LastModifiedFieldName = null,   // not set
            DeltaToken            = "2026-03-01T00:00:00Z"  // set but should be ignored
        };
        var sut = MakeProvider(client, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("rec003", results[0].Id);

        // Verify no formula was used.
        await client.Received(1).ListRecordsAsync(
            "Tasks",
            null,
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFilesAsync_Pagination_FetchesAllPages()
    {
        var record1 = MakeRecord("rec010", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = Json("\"Page one\"")
        });
        var record2 = MakeRecord("rec011", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = Json("\"Page two\"")
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record1], offset: "page2token"));
        client.ListRecordsAsync("Tasks", "page2token", null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record2]));

        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("Page one.md", results[0].FileName);
        Assert.Equal("Page two.md", results[1].FileName);
    }
}

/// <summary>Fake HTTP handler that returns a fixed string body for any request.</summary>
file sealed class FakeDownloadHandler(string responseBody) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/octet-stream")
        });
    }
}
