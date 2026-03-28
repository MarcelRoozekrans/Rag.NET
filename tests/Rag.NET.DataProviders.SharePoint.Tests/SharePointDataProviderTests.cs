using Microsoft.Graph;
using Rag.NET.DataProviders.SharePoint;
using Xunit;

namespace Rag.NET.DataProviders.SharePoint.Tests;

public sealed class SharePointDataProviderTests
{
    [Fact]
    public void Constructor_NullGraph_Throws()
    {
        var opts = new SharePointOptions { SiteId = "s1", DriveId = "d1" };
        Assert.Throws<ArgumentNullException>(() =>
            new SharePointDataProvider(null!, opts));
    }

    [Fact]
    public void Constructor_ValidArgs_Succeeds()
    {
        var graph = new GraphServiceClient(new HttpClient(), new FakeTokenCredential());
        var opts = new SharePointOptions { SiteId = "s1", DriveId = "d1" };
        var sut = new SharePointDataProvider(graph, opts);
        Assert.NotNull(sut);
    }
}

// Minimal stub to satisfy GraphServiceClient constructor
file sealed class FakeTokenCredential : Azure.Core.TokenCredential
{
    public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new(string.Empty, DateTimeOffset.MaxValue);

    public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        => ValueTask.FromResult(new Azure.Core.AccessToken(string.Empty, DateTimeOffset.MaxValue));
}
