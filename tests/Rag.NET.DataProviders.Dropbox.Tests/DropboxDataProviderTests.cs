using Dropbox.Api.Files;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Dropbox;
using Rag.NET.DataProviders.Testing;
using Xunit;

namespace Rag.NET.DataProviders.Dropbox.Tests;

public sealed class DropboxDataProviderTests
{
    [Fact]
    public void Constructor_NullTokenProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DropboxDataProvider(null!));
    }

    [Fact]
    public void Constructor_WithOptions_Succeeds()
    {
        var provider = new StaticTokenProvider("tok");
        var opts = new DropboxOptions { FolderPath = "/docs", Extensions = [".md"] };

        var sut = new DropboxDataProvider(provider, opts);

        Assert.NotNull(sut);
    }

    [Fact]
    public void AddDropboxDataProvider_AccessToken_RegistersIFileContentProvider()
    {
        var services = new ServiceCollection();

        services.AddDropboxDataProvider("my-access-token");

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileContentProvider>();
        Assert.IsType<DropboxDataProvider>(provider);
    }

    [Fact]
    public void AddDropboxDataProvider_TokenProvider_RegistersIFileContentProvider()
    {
        var services = new ServiceCollection();
        var tokenProvider = new StaticTokenProvider("tok");

        services.AddDropboxDataProvider(tokenProvider);

        var sp = services.BuildServiceProvider();
        var fileProvider = sp.GetRequiredService<IFileContentProvider>();
        Assert.IsType<DropboxDataProvider>(fileProvider);
    }

    [Fact]
    public void AddDropboxDataProvider_NullAccessToken_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddDropboxDataProvider((string)null!));
    }

    [Fact]
    public void AddDropboxDataProvider_NullTokenProvider_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddDropboxDataProvider((ITokenProvider)null!));
    }

    // -----------------------------------------------------------------------
    // Metadata — the exact keys and values this connector emits
    //
    // DropboxClient is built from a token inside the provider and exposes no injectable
    // transport, so GetFilesAsync cannot be driven without hitting the network. These tests
    // call the internal ToHandle that both the full and delta paths funnel through.
    // -----------------------------------------------------------------------

    /// <summary>Dropbox validates content_hash as a 64-character SHA-256 hex digest.</summary>
    private const string ContentHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static FileMetadata MakeFile(
        string name, string pathLower, string? pathDisplay, string contentHash = ContentHash)
        => new(
            name:           name,
            id:             "id:abc123",
            clientModified: new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            serverModified: new DateTime(2026, 3, 1, 10, 5, 0, DateTimeKind.Utc),
            rev:            "0123456789abcdef",
            size:           42,
            pathLower:      pathLower,
            pathDisplay:    pathDisplay,
            contentHash:    contentHash);

    [Fact]
    public void ToHandle_EmitsPathAndFolder()
    {
        var sut = new DropboxDataProvider(
            new StaticTokenProvider("tok"), new DropboxOptions { FolderPath = "/docs" });

        var handle = sut.ToHandle(MakeFile("readme.md", "/docs/readme.md", "/Docs/README.md"));

        Assert.NotNull(handle.Metadata);
        // The display path is the human-facing one and is what the handle id uses too.
        Assert.Equal("/Docs/README.md", handle.Metadata["path"]);
        Assert.Equal("/docs",           handle.Metadata["folder"]);
        Assert.Equal(2, handle.Metadata.Count);
        MetadataContract.AssertValid(handle.Metadata, handle.Id);
    }

    [Fact]
    public void ToHandle_RootFolder_OmitsFolder()
    {
        // FolderPath defaults to the empty string, meaning "the whole Dropbox". That is not a
        // folder name, so the key is omitted rather than written empty.
        var sut = new DropboxDataProvider(new StaticTokenProvider("tok"));

        var handle = sut.ToHandle(MakeFile("readme.md", "/readme.md", "/README.md"));

        Assert.NotNull(handle.Metadata);
        Assert.Equal("/README.md", handle.Metadata["path"]);
        Assert.False(handle.Metadata.ContainsKey("folder"));
        _ = Assert.Single(handle.Metadata);
        MetadataContract.AssertValid(handle.Metadata, handle.Id);
    }

    [Fact]
    public void ToHandle_NoDisplayPath_FallsBackToLowercasedPath()
    {
        var sut = new DropboxDataProvider(new StaticTokenProvider("tok"));

        var handle = sut.ToHandle(MakeFile("readme.md", "/docs/readme.md", pathDisplay: null));

        Assert.NotNull(handle.Metadata);
        Assert.Equal("/docs/readme.md", handle.Metadata["path"]);
        // path never ends up empty, so the key is never omitted.
        Assert.Equal(handle.Id, handle.Metadata["path"]);
        Assert.False(handle.Metadata.ContainsKey("folder"));
        _ = Assert.Single(handle.Metadata);
        MetadataContract.AssertValid(handle.Metadata, handle.Id);
    }
}
