using System.Net;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Http;
using Google.Apis.Json;
using Google.Apis.Services;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.GoogleDrive;
using Rag.NET.DataProviders.Testing;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.GoogleDrive.Tests;

public sealed class GoogleDriveDataProviderTests
{
    private static DriveService MakeDriveService()
        => new(new BaseClientService.Initializer { ApplicationName = "test" });

    /// <summary>
    /// Creates a <see cref="DriveService"/> backed by a fake HTTP handler that responds to
    /// <c>/drive/v3/files</c> with <paramref name="filesListJson"/> so that unit tests never
    /// touch the network.
    /// </summary>
    private static DriveService MakeDriveServiceWithFakeHttp(string filesListJson)
    {
        var handler = new FakeFilesListHandler(filesListJson);
        return new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "test",
            HttpClientFactory = new FakeHttpClientFactory(handler),
        });
    }

    /// <summary>Builds the JSON payload that the Files.List endpoint returns.</summary>
    private static string BuildFilesListJson(
        IEnumerable<(string id, string name, string mimeType, string? md5)> files,
        string? nextPageToken = null)
    {
        var fileList = new FileList
        {
            Files = files.Select(f => new Google.Apis.Drive.v3.Data.File
            {
                Id          = f.id,
                Name        = f.name,
                MimeType    = f.mimeType,
                Md5Checksum = f.md5,
            }).ToList(),
            NextPageToken = nextPageToken,
        };
        return NewtonsoftJsonSerializer.Instance.Serialize(fileList);
    }

    /// <summary>
    /// Builds a Files.List JSON payload for a single file carrying <c>createdTime</c>/
    /// <c>modifiedTime</c>. Raw JSON, not the <c>File</c> model: <c>CreatedTimeDateTimeOffset</c>/
    /// <c>ModifiedTimeDateTimeOffset</c> are <c>[JsonIgnore]</c> on this SDK version — only the
    /// <c>*Raw</c> string properties round-trip through <see cref="NewtonsoftJsonSerializer"/> —
    /// so a raw literal matches the wire format the provider actually parses.
    /// </summary>
    private static string BuildFilesListJsonWithTimestamps(
        string id, string name, string mimeType, string createdTimeIso, string modifiedTimeIso)
        => $$"""
            {
              "files": [
                { "id": "{{id}}", "name": "{{name}}", "mimeType": "{{mimeType}}",
                  "createdTime": "{{createdTimeIso}}", "modifiedTime": "{{modifiedTimeIso}}" }
              ]
            }
            """;

    [Fact]
    public void Constructor_NullDrive_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GoogleDriveDataProvider(null!));
    }

    [Fact]
    public void Constructor_WithOptions_Succeeds()
    {
        var drive = MakeDriveService();
        var opts = new GoogleDriveOptions { FolderId = "folder-1", Extensions = [".md"] };

        var sut = new GoogleDriveDataProvider(drive, opts);

        Assert.NotNull(sut);
    }

    [Fact]
    public void AddGoogleDriveDataProvider_DriveService_RegistersIFileContentProvider()
    {
        var services = new ServiceCollection();
        var drive = MakeDriveService();

        services.AddGoogleDriveDataProvider(drive);

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileContentProvider>();
        Assert.IsType<GoogleDriveDataProvider>(provider);
    }

    [Fact]
    public void AddGoogleDriveDataProvider_NullDriveService_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddGoogleDriveDataProvider((DriveService)null!));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsFilesAndSkipsFolders()
    {
        // Arrange — two files + one Google-Drive folder entry
        var json = BuildFilesListJson(
        [
            ("file-1", "readme.md",   "text/plain",                              "md5-1"),
            ("file-2", "notes.txt",   "text/plain",                              "md5-2"),
            ("dir-1",  "my-folder",   "application/vnd.google-apps.folder",      null),
        ]);
        var sut = new GoogleDriveDataProvider(MakeDriveServiceWithFakeHttp(json));

        // Act
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert — folder must be excluded; both files must be present with correct metadata
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Value.Id,       "file-1",    StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Value.Id,       "file-2",    StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => string.Equals(e.Value.Id, "dir-1",     StringComparison.Ordinal));
        Assert.Equal("readme.md", entries.Single(e => string.Equals(e.Value.Id, "file-1", StringComparison.Ordinal)).Value.FileName);
        Assert.Equal("md5-1",     entries.Single(e => string.Equals(e.Value.Id, "file-1", StringComparison.Ordinal)).Value.ETag);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        // Arrange — three files; only .md should survive the extension filter
        var json = BuildFilesListJson(
        [
            ("file-1", "guide.md",   "text/plain", "md5-1"),
            ("file-2", "build.yaml", "text/plain", "md5-2"),
            ("file-3", "main.cs",    "text/plain", "md5-3"),
        ]);
        var opts = new GoogleDriveOptions { Extensions = [".md"] };
        var sut = new GoogleDriveDataProvider(MakeDriveServiceWithFakeHttp(json), opts);

        // Act
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        _ = Assert.Single(entries);
        Assert.Equal("file-1", entries[0].Value.Id);
        Assert.DoesNotContain(entries, e => string.Equals(e.Value.Id, "file-2", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => string.Equals(e.Value.Id, "file-3", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Metadata — the exact keys and values this connector emits
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_WholeDrive_EmitsMimeTypeAndOmitsFolderId()
    {
        // A whole-drive listing does not know which folder a file sits in, so folder_id is
        // omitted rather than written empty.
        var json = BuildFilesListJson([("file-1", "readme.md", "text/markdown", "md5-1")]);
        var sut = new GoogleDriveDataProvider(MakeDriveServiceWithFakeHttp(json));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var metadata = Assert.Single(entries).Value.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("text/markdown", metadata["mime_type"]);
        Assert.False(metadata.ContainsKey("folder_id"));
        _ = Assert.Single(metadata);
        MetadataContract.AssertValid(entries[0].Value);
    }

    [Fact]
    public async Task GetFilesAsync_FolderTraversal_EmitsMimeTypeAndFolderId()
    {
        var json = BuildFilesListJson([("file-1", "readme.md", "text/markdown", "md5-1")]);
        var opts = new GoogleDriveOptions { FolderId = "folder-abc" };
        var sut = new GoogleDriveDataProvider(MakeDriveServiceWithFakeHttp(json), opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var metadata = Assert.Single(entries).Value.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("text/markdown", metadata["mime_type"]);
        Assert.Equal("folder-abc",    metadata["folder_id"]);
        Assert.Equal(2, metadata.Count);
        MetadataContract.AssertValid(entries[0].Value);
    }

    [Fact]
    public async Task GetFilesAsync_ChangesFeed_EmitsMimeTypeAndOmitsFolderId()
    {
        // The Changes feed reports the file, not the folder it lives in, so folder_id is omitted
        // there too — but mime_type is on the change payload and must still be emitted.
        var changeList = new ChangeList
        {
            Changes =
            [
                new Change
                {
                    Removed = false,
                    File = new Google.Apis.Drive.v3.Data.File
                    {
                        Id          = "file-changed",
                        Name        = "updated.md",
                        MimeType    = "text/markdown",
                        Md5Checksum = "md5-new",
                    },
                },
            ],
        };
        var handler = new FakeChangesListHandler(
            NewtonsoftJsonSerializer.Instance.Serialize(changeList));
        var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName   = "test",
            HttpClientFactory = new FakeHttpClientFactory(handler),
        });
        var sut = new GoogleDriveDataProvider(drive, new GoogleDriveOptions { DeltaToken = "tok1" });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries).Value;
        Assert.Equal("file-changed", entry.Id.Value);
        Assert.NotNull(entry.Metadata);
        Assert.Equal("text/markdown", entry.Metadata["mime_type"]);
        Assert.False(entry.Metadata.ContainsKey("folder_id"));
        _ = Assert.Single(entry.Metadata);
        MetadataContract.AssertValid(entry);
    }

    // -----------------------------------------------------------------------
    // Phase 4.10 Task 6 — createdTime/modifiedTime become typed CreatedAt/UpdatedAt.
    // All four field masks (whole-drive Files.List, folder Files.List, and the two
    // Changes.List masks) were widened; the three tests below drive the whole-drive,
    // folder-traversal and Changes-feed code paths independently so that widening only
    // one mask cannot pass unnoticed.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_WholeDrive_CreatedTimeAndModifiedTime_BecomeTypedTimestamps()
    {
        var json = BuildFilesListJsonWithTimestamps(
            "file-1", "readme.md", "text/markdown",
            "2026-01-01T09:00:00Z", "2026-02-01T10:30:00Z");
        var sut = new GoogleDriveDataProvider(MakeDriveServiceWithFakeHttp(json));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries).Value;
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), entry.CreatedAt);
        Assert.Equal(new DateTime(2026, 2, 1, 10, 30, 0, DateTimeKind.Utc), entry.UpdatedAt);
    }

    [Fact]
    public async Task GetFilesAsync_FolderTraversal_CreatedTimeAndModifiedTime_BecomeTypedTimestamps()
    {
        var json = BuildFilesListJsonWithTimestamps(
            "file-1", "readme.md", "text/markdown",
            "2026-01-01T09:00:00Z", "2026-02-01T10:30:00Z");
        var opts = new GoogleDriveOptions { FolderId = "folder-abc" };
        var sut = new GoogleDriveDataProvider(MakeDriveServiceWithFakeHttp(json), opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries).Value;
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), entry.CreatedAt);
        Assert.Equal(new DateTime(2026, 2, 1, 10, 30, 0, DateTimeKind.Utc), entry.UpdatedAt);
    }

    [Fact]
    public async Task GetFilesAsync_ChangesFeed_CreatedTimeAndModifiedTime_BecomeTypedTimestamps()
    {
        const string changesJson = """
            {
              "changes": [
                { "removed": false,
                  "file": { "id": "file-changed", "name": "updated.md", "mimeType": "text/markdown",
                            "createdTime": "2026-01-01T09:00:00Z", "modifiedTime": "2026-02-01T10:30:00Z" } }
              ]
            }
            """;
        var handler = new FakeChangesListHandler(changesJson);
        var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName   = "test",
            HttpClientFactory = new FakeHttpClientFactory(handler),
        });
        var sut = new GoogleDriveDataProvider(drive, new GoogleDriveOptions { DeltaToken = "tok1" });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries).Value;
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), entry.CreatedAt);
        Assert.Equal(new DateTime(2026, 2, 1, 10, 30, 0, DateTimeKind.Utc), entry.UpdatedAt);
    }

    [Fact]
    public async Task GetFilesAsync_AllEntriesSatisfyMetadataContract()
    {
        var json = BuildFilesListJson(
        [
            ("file-1", "readme.md", "text/markdown", "md5-1"),
            ("file-2", "notes.txt", "text/plain",    "md5-2"),
        ]);
        var sut = new GoogleDriveDataProvider(MakeDriveServiceWithFakeHttp(json));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        MetadataContract.AssertAll(entries.Select(e => e.Value));
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure — fake HTTP layer for Google API SDK
// ---------------------------------------------------------------------------

/// <summary>
/// Intercepts HTTP calls to <c>/drive/v3/files</c> and returns a canned JSON body,
/// so tests never hit the network.
/// </summary>
file sealed class FakeFilesListHandler(string filesListJson) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.StartsWith("/drive/v3/files", StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(filesListJson, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// Intercepts HTTP calls to <c>/drive/v3/changes</c> and returns a canned JSON body, so the
/// delta path can be exercised without hitting the network.
/// </summary>
file sealed class FakeChangesListHandler(string changesListJson) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.StartsWith("/drive/v3/changes", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(changesListJson, System.Text.Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// Implements Google's <see cref="IHttpClientFactory"/> to inject a custom
/// <see cref="HttpMessageHandler"/> into a <see cref="DriveService"/>.
/// </summary>
file sealed class FakeHttpClientFactory(HttpMessageHandler innerHandler) : Google.Apis.Http.IHttpClientFactory
{
    public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
    {
        var configurableHandler = new ConfigurableMessageHandler(innerHandler);
        return new ConfigurableHttpClient(configurableHandler);
    }
}
