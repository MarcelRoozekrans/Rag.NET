using Microsoft.Graph;
using Rag.NET.DataProviders.OneDrive;
using Xunit;

namespace Rag.NET.DataProviders.OneDrive.Tests;

public sealed class OneDriveDataProviderTests
{
    [Fact]
    public void Constructor_NullGraph_Throws()
    {
        var opts = new OneDriveOptions { UserId = "me" };
        Assert.Throws<ArgumentNullException>(() =>
            new OneDriveDataProvider(null!, opts));
    }

    [Fact]
    public void Constructor_ValidArgs_Succeeds()
    {
        var graph = new GraphServiceClient(new HttpClient(), new FakeTokenCredential());
        var opts = new OneDriveOptions { UserId = "me" };
        var sut = new OneDriveDataProvider(graph, opts);
        Assert.NotNull(sut);
    }
}

file sealed class FakeTokenCredential : Azure.Core.TokenCredential
{
    public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new(string.Empty, DateTimeOffset.MaxValue);

    public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        => ValueTask.FromResult(new Azure.Core.AccessToken(string.Empty, DateTimeOffset.MaxValue));
}
