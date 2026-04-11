using System.Net;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Http;
using Google.Apis.Json;
using Google.Apis.Services;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.GoogleDrive;
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
