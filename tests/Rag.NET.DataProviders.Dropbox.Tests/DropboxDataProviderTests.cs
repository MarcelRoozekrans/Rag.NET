using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Dropbox;
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
}
