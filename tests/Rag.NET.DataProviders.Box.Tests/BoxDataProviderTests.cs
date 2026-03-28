using Box.V2;
using Box.V2.Config;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Box;
using Xunit;

namespace Rag.NET.DataProviders.Box.Tests;

public sealed class BoxDataProviderTests
{
    private static BoxClient MakeBoxClient()
    {
        var config = new BoxConfig("clientId", "clientSecret", new Uri("https://localhost"));
        return new BoxClient(config);
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BoxDataProvider(null!));
    }

    [Fact]
    public void Constructor_WithOptions_Succeeds()
    {
        var client = MakeBoxClient();
        var opts = new BoxOptions { RootFolderId = "123", Extensions = [".pdf"] };

        var sut = new BoxDataProvider(client, opts);

        Assert.NotNull(sut);
    }

    [Fact]
    public void AddBoxDataProvider_BoxClient_RegistersIFileContentProvider()
    {
        var services = new ServiceCollection();
        var client = MakeBoxClient();

        services.AddBoxDataProvider(client);

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileContentProvider>();
        Assert.IsType<BoxDataProvider>(provider);
    }

    [Fact]
    public void AddBoxDataProvider_NullClient_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddBoxDataProvider((BoxClient)null!));
    }
}
