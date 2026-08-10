using System.Net;
using System.Text;
using System.Text.Json;
using AirtableApiClient;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Airtable;
using Rag.NET.DataProviders.Testing;
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
    public async Task GetFilesAsync_DeltaWithLastModifiedField_ScopesFormulaToTheNamedField()
    {
        // Issue #108: the field name's null-ness was consumed but its value discarded — an
        // argument-less LAST_MODIFIED_TIME() tracks every field. The formula must reference
        // the configured field.
        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync(
                "Tasks",
                null,
                "LAST_MODIFIED_TIME({Modified})>'2026-03-01T00:00:00Z'",
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
            "LAST_MODIFIED_TIME({Modified})>'2026-03-01T00:00:00Z'",
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFilesAsync_FieldNameWithSpaces_IsReferencedVerbatim()
    {
        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeResponse([]));

        var opts = new AirtableOptions
        {
            BaseId                = "appTEST",
            TableName             = "Tasks",
            LastModifiedFieldName = "Last Modified Time",
            DeltaToken            = "2026-03-01T00:00:00Z"
        };
        var sut = MakeProvider(client, opts);

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        await client.Received(1).ListRecordsAsync(
            "Tasks",
            null,
            "LAST_MODIFIED_TIME({Last Modified Time})>'2026-03-01T00:00:00Z'",
            null,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Bad{Field")]
    [InlineData("Bad}Field")]
    public void Constructor_FieldNameWithBraces_Throws(string fieldName)
    {
        // Airtable's formula grammar has no escape for braces inside a {Field} reference, so a
        // brace would truncate the reference or splice into the formula — rejected instead.
        var ex = Assert.Throws<ArgumentException>(() => MakeProvider(
            Substitute.For<IAirtableClient>(),
            new AirtableOptions
            {
                BaseId                = "appTEST",
                TableName             = "Tasks",
                LastModifiedFieldName = fieldName,
                DeltaToken            = "2026-03-01T00:00:00Z"
            }));

        Assert.Contains("braces", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAirtableDataProvider_ConfigureCallbackSettingFieldName_ReachesTheProvider()
    {
        // The configure callback can set LastModifiedFieldName (it is deliberately not
        // init-only). Observable through the provider's own construction-time validation:
        // a brace-containing field name from the callback must throw when the provider is
        // resolved, proving the configured value is the one the provider filters with.
        var services = new ServiceCollection();
        services.AddAirtableDataProvider(
            "appTEST", "Tasks", "patTEST",
            o => o.LastModifiedFieldName = "Broken{Field");

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<ArgumentException>(sp.GetRequiredService<IFileContentProvider>);
        Assert.Contains("braces", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DeltaTokenWithQuote_ThrowsWhenFormulaWouldUseIt()
    {
        // The token is interpolated inside single quotes; a quote in it would splice into the
        // formula. Only rejected when LastModifiedFieldName is set — without it no formula is
        // ever built and the token is an opaque cursor like every other connector's.
        var ex = Assert.Throws<ArgumentException>(() => MakeProvider(
            Substitute.For<IAirtableClient>(),
            new AirtableOptions
            {
                BaseId                = "appTEST",
                TableName             = "Tasks",
                LastModifiedFieldName = "Modified",
                DeltaToken            = "2026-03-01'OR'1"
            }));

        Assert.Contains("DeltaToken", ex.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// Phase 4.10 Task 5: <c>AirtableRecord.CreatedTime</c> is auto-populated by Airtable for
    /// every record and typed as a non-nullable <see cref="DateTime"/> by the SDK, so it becomes
    /// <see cref="FileEntry.CreatedAt"/> directly — no parsing involved. Airtable's "last
    /// modified" concept is per-field, not a fixed record property, so <c>UpdatedAt</c> stays
    /// unset.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_CreatedTime_IsTypedAsCreatedAt()
    {
        var createdTime = new DateTime(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc);
        var record = MakeRecord("rec010", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = Json("\"Timestamped record\"")
        });
        record.CreatedTime = createdTime;

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(results).Value;
        Assert.Equal(createdTime, entry.CreatedAt);
        Assert.Null(entry.UpdatedAt);
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

    [Fact]
    public async Task GetFilesAsync_Metadata_PinsRecordAndAttachmentKeys()
    {
        var attachmentJson = Json("""
            [ { "id": "att001", "url": "https://dl.airtable.test/photo.png", "filename": "photo.png" } ]
            """);
        var record = MakeRecord("rec200", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"]        = Json("\"Design doc\""),
            ["Attachments"] = attachmentJson
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        using var http = new HttpClient(new FakeDownloadHandler("bytes"));
        var sut = MakeProvider(client, http: http);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var recordMetadata = results[0].Value.Metadata!;
        Assert.Equal("appTEST", recordMetadata["base_id"]);
        Assert.Equal("Tasks",   recordMetadata["table"]);
        Assert.Equal("rec200",  recordMetadata["record_id"]);
        Assert.Equal(3, recordMetadata.Count);

        // An attachment carries its record's context plus the cell it came out of.
        var attachmentMetadata = results[1].Value.Metadata!;
        Assert.Equal("appTEST",     attachmentMetadata["base_id"]);
        Assert.Equal("Tasks",       attachmentMetadata["table"]);
        Assert.Equal("rec200",      attachmentMetadata["record_id"]);
        Assert.Equal("Attachments", attachmentMetadata["field"]);
        Assert.Equal("att001",      attachmentMetadata["attachment_id"]);
        Assert.Equal(5, attachmentMetadata.Count);
    }

    [Fact]
    public async Task GetFilesAsync_AttachmentWithoutId_OmitsAttachmentIdKey()
    {
        // Airtable's attachment objects are only guaranteed to carry "url"; an absent id must be
        // omitted rather than written empty.
        var attachmentJson = Json("""
            [ { "url": "https://dl.airtable.test/note.txt", "filename": "note.txt" } ]
            """);
        var record = MakeRecord("rec201", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"]  = Json("\"No att id\""),
            ["Files"] = attachmentJson
        });

        var client = Substitute.For<IAirtableClient>();
        client.ListRecordsAsync("Tasks", null, null, null, Arg.Any<CancellationToken>())
            .Returns(MakeResponse([record]));

        using var http = new HttpClient(new FakeDownloadHandler("bytes"));
        var sut = MakeProvider(client, http: http);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results.Select(r => r.Value));

        var attachmentMetadata = results[1].Value.Metadata!;
        Assert.Equal("Files", attachmentMetadata["field"]);
        Assert.False(attachmentMetadata.ContainsKey("attachment_id"));
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
