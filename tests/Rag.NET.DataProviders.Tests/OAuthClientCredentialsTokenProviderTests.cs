using System.Net;
using System.Text.Json;
using Rag.NET.DataProviders;
using Xunit;

namespace Rag.NET.DataProviders.Tests;

public sealed class OAuthClientCredentialsTokenProviderTests
{
    private static HttpClient MakeHttpClient(string accessToken, int expiresIn = 3600)
        => new HttpClient(new FakeHttpHandler(accessToken, expiresIn));

    [Fact]
    public async Task GetTokenAsync_FetchesTokenOnFirstCall()
    {
        using var sut = new OAuthClientCredentialsTokenProvider(
            "https://auth.example.com/token", "client-id", "client-secret",
            httpClient: MakeHttpClient("tok-abc"));

        var token = await sut.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("tok-abc", token);
    }

    [Fact]
    public async Task GetTokenAsync_ReturnsCachedTokenOnSecondCall()
    {
        var handler = new FakeHttpHandler("tok-xyz", expiresIn: 3600);
        using var http = new HttpClient(handler);
        using var sut = new OAuthClientCredentialsTokenProvider(
            "https://auth.example.com/token", "id", "secret", httpClient: http);

        await sut.GetTokenAsync(TestContext.Current.CancellationToken);
        await sut.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_RefetchesTokenAfterExpiry()
    {
        var handler = new FakeHttpHandler("tok-new", expiresIn: 1);
        using var http = new HttpClient(handler);
        using var sut = new OAuthClientCredentialsTokenProvider(
            "https://auth.example.com/token", "id", "secret", httpClient: http);

        await sut.GetTokenAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await sut.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_ZeroExpiresIn_DoesNotCacheToken()
    {
        var handler = new FakeHttpHandler("tok-zero", expiresIn: 0);
        using var http = new HttpClient(handler);
        using var sut = new OAuthClientCredentialsTokenProvider(
            "https://auth.example.com/token", "id", "secret", httpClient: http);

        await sut.GetTokenAsync(TestContext.Current.CancellationToken);
        await sut.GetTokenAsync(TestContext.Current.CancellationToken);

        // expiresIn=0 → Math.Max(0-60, 0) = 0 → token expires immediately → 2 calls
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public void Constructor_NullOrEmptyArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new OAuthClientCredentialsTokenProvider("", "id", "secret"));
        Assert.Throws<ArgumentException>(() =>
            new OAuthClientCredentialsTokenProvider("https://endpoint", "", "secret"));
        Assert.Throws<ArgumentException>(() =>
            new OAuthClientCredentialsTokenProvider("https://endpoint", "id", ""));
    }

    private sealed class FakeHttpHandler(string token, int expiresIn) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var body = JsonSerializer.Serialize(new
            {
                access_token = token,
                expires_in   = expiresIn,
                token_type   = "Bearer",
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
