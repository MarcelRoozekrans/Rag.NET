using System.Net;
using System.Text;
using System.Text.Json;
using AirtableApiClient;
using NSubstitute;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Airtable;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Results;

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
        Assert.Equal("rec001", results[0].Value.Id);
        Assert.Equal("Design doc.md", results[0].Value.FileName);
        var markdown = await ReadContentAsync(results[0].Value);
        Assert.Contains("# Design doc", markdown, StringComparison.Ordinal);
        Assert.Contains("Status", markdown, StringComparison.Ordinal);
        Assert.Contains("In Progress", markdown, StringComparison.Ordinal);

        // Second entry: the attachment.
        Assert.Equal("rec001/Attachments/photo.png", results[1].Value.Id);
        Assert.Equal("photo.png", results[1].Value.FileName);
        var attachmentContent = await ReadContentAsync(results[1].Value);
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
    public async Task GetFilesAsync_HostileRecordTitle_Sanitized()
    {
        // The title is the first field's value — entirely user-controlled.
        var record = MakeRecord("rec009", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = Json("\"Ops/Runbook: Prod\"")
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("Ops_Runbook_ Prod.md", results[0].Value.FileName);
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

        _ = Assert.Single(results);
        Assert.Equal("rec003", results[0].Value.Id);

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
        Assert.Equal("Page one.md", results[0].Value.FileName);
        Assert.Equal("Page two.md", results[1].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_RowMarkdown_ContainsFieldTable()
    {
        var record = MakeRecord("rec100", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"]   = Json("\"My Task\""),
            ["Status"] = Json("\"Done\""),
            ["Priority"] = Json("\"High\"")
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var sut = MakeProvider(client);
        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var markdown = await ReadContentAsync(results[0].Value);
        Assert.Contains("| Field | Value |", markdown, StringComparison.Ordinal);
        Assert.Contains("| --- | --- |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Status | Done |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Priority | High |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_RowMarkdown_LongTextAsSeparateSection()
    {
        var record = MakeRecord("rec101", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"]  = Json("\"Doc Title\""),
            ["Notes"] = Json("\"Line one\\nLine two\\nLine three\"")
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var sut = MakeProvider(client);
        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var markdown = await ReadContentAsync(results[0].Value);
        Assert.Contains("## Notes", markdown, StringComparison.Ordinal);
        Assert.Contains("Line one", markdown, StringComparison.Ordinal);
        // Long text should NOT appear in the table.
        Assert.DoesNotContain("| Notes |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_RowMarkdown_FirstFieldAsTitle()
    {
        var record = MakeRecord("rec102", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Title"]  = Json("\"Project Alpha\""),
            ["Status"] = Json("\"Active\"")
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var sut = MakeProvider(client);
        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var markdown = await ReadContentAsync(results[0].Value);
        Assert.StartsWith("# Project Alpha", markdown, StringComparison.Ordinal);
        Assert.Equal("Project Alpha.md", results[0].Value.FileName);
        // The first field should NOT appear again in the table body.
        Assert.DoesNotContain("| Title |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFilesAsync_NullFieldValues_Handled()
    {
        var record = MakeRecord("rec103", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"]    = Json("\"Has Nulls\""),
            ["Empty"]   = Json("null"),
            ["Blank"]   = Json("\"\"")
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var sut = MakeProvider(client);
        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        var markdown = await ReadContentAsync(results[0].Value);
        Assert.Contains("# Has Nulls", markdown, StringComparison.Ordinal);
        // Should not crash — null/empty values handled gracefully.
    }

    [Fact]
    public async Task GetFilesAsync_MultipleAttachments_AllYielded()
    {
        var attachmentJson = Json("""
            [
                { "id": "att1", "url": "https://dl.test/a.png", "filename": "a.png" },
                { "id": "att2", "url": "https://dl.test/b.pdf", "filename": "b.pdf" },
                { "id": "att3", "url": "https://dl.test/c.docx", "filename": "c.docx" }
            ]
            """);

        var record = MakeRecord("rec104", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"]  = Json("\"Multi Attach\""),
            ["Files"] = attachmentJson
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var handler = new FakeDownloadHandler("data");
        using var http = new HttpClient(handler);
        var sut = MakeProvider(client, http: http);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // 1 markdown row + 3 attachments = 4 entries.
        Assert.Equal(4, results.Count);
        Assert.Equal("rec104", results[0].Value.Id);
        Assert.Equal("a.png", results[1].Value.FileName);
        Assert.Equal("b.pdf", results[2].Value.FileName);
        Assert.Equal("c.docx", results[3].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_AttachmentFileHandleId_CorrectFormat()
    {
        var attachmentJson = Json("""
            [
                { "id": "attX", "url": "https://dl.test/report.xlsx", "filename": "report.xlsx" }
            ]
            """);

        var record = MakeRecord("rec105", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"]    = Json("\"Report Row\""),
            ["Uploads"] = attachmentJson
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var handler = new FakeDownloadHandler("data");
        using var http = new HttpClient(handler);
        var sut = MakeProvider(client, http: http);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var attachment = results[1].Value;
        Assert.Equal("rec105/Uploads/report.xlsx", attachment.Id);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyRecords_YieldsNothing()
    {
        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([]));

        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        var client = Substitute.For<IAirtableClient>();
        // Return a page with offset so the provider loops — cancellation fires on second iteration.
        var record = MakeRecord("rec106", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = Json("\"Cancel me\"")
        });
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record], offset: "next"));
        client.ListRecordsAsync("Tasks", "next", null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record], offset: "next2"));

        var sut = MakeProvider(client);
        using var cts = new CancellationTokenSource();

        var enumerator = sut.GetFilesAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        // Consume the first item, then cancel.
        Assert.True(await enumerator.MoveNextAsync());
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await enumerator.MoveNextAsync()) { }
        });
    }

    [Fact]
    public async Task GetFilesAsync_ViewOption_PassedToClient()
    {
        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, "Grid view", Arg.Any<CancellationToken>())
            .Returns(MakeResponse([]));

        var opts = new AirtableOptions
        {
            BaseId    = "appTEST",
            TableName = "Tasks",
            View      = "Grid view"
        };
        var sut = MakeProvider(client, opts);

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        await client.Received(1).ListRecordsAsync(
            "Tasks",
            null,
            null,
            "Grid view",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFilesAsync_ETagIsContentHash()
    {
        var record = MakeRecord("rec108", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"]   = Json("\"Hash Test\""),
            ["Status"] = Json("\"Open\"")
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var sut = MakeProvider(client);
        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var etag = results[0].Value.ETag;
        Assert.NotNull(etag);
        // ETag should be a 64-char lowercase hex string (SHA256).
        Assert.Equal(64, etag.Length);
        Assert.Matches("^[0-9a-f]{64}$", etag);

        // Verify it matches the expected SHA256 of the serialized fields.
        var expectedJson = JsonSerializer.Serialize(record.Fields);
        var expectedHash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(expectedJson));
        var expectedEtag = Convert.ToHexStringLower(expectedHash);
        Assert.Equal(expectedEtag, etag);
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
