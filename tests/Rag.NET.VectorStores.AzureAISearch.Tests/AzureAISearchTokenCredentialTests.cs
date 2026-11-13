using System.Net;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;
using AzureSearchClientOptions = Azure.Search.Documents.SearchClientOptions;

namespace Rag.NET.AzureAISearch.Tests;

/// <summary>
/// The <see cref="TokenCredential"/> overloads: the store they build sends a bearer token, and
/// <c>UseAzureAISearch</c> registers and validates it exactly as it does the key-based store.
/// </summary>
public class AzureAISearchTokenCredentialTests
{
    private static readonly Uri Endpoint = new("https://test.search.windows.net");

    [Fact]
    public async Task TheTokenStoreSendsTheCredentialsTokenAsABearerHeader()
    {
        var capture = new RefusingHandler();
        using var sut = new AzureAISearchVectorStore(
            Endpoint,
            "test-index",
            new FakeCredential(),
            vectorDimensions: 3,
            ClientOptions(capture),
            options: null);

        await Assert.ThrowsAsync<RequestFailedException>(
            () => sut.InitializeAsync(TestContext.Current.CancellationToken));

        var request = Assert.Single(capture.Requests);
        Assert.Equal("Bearer", request.Scheme);
        Assert.Equal(FakeCredential.Token, request.Parameter);
        Assert.False(request.SentApiKey);
    }

    /// <summary>
    /// The contrast that makes the test above mean something: the same capture sees the key path's
    /// <c>api-key</c> header and no bearer token.
    /// </summary>
    [Fact]
    public async Task TheKeyStoreSendsAnApiKeyAndNoBearerHeader()
    {
        var capture = new RefusingHandler();
        using var sut = new AzureAISearchVectorStore(
            Endpoint,
            "test-index",
            new AzureKeyCredential("dummy-key"),
            vectorDimensions: 3,
            ClientOptions(capture),
            options: null);

        await Assert.ThrowsAsync<RequestFailedException>(
            () => sut.InitializeAsync(TestContext.Current.CancellationToken));

        var request = Assert.Single(capture.Requests);
        Assert.Null(request.Scheme);
        Assert.True(request.SentApiKey);
    }

    [Fact]
    public void UseAzureAISearch_WithATokenCredential_RegistersOneStoreUnderEveryInterface()
    {
        var provider = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(Endpoint, "test-index", new FakeCredential()))
            .BuildServiceProvider();

        var store = Assert.IsType<AzureAISearchVectorStore>(provider.GetRequiredService<IVectorStore>());
        Assert.Same(store, provider.GetRequiredService<IHybridSearchable>());
        Assert.Same(store, provider.GetRequiredService<ICollectionManageable>());
    }

    [Fact]
    public void UseAzureAISearch_WithATokenCredential_ValidatesTheConfiguredOptions()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseAzureAISearch(
                Endpoint,
                "test-index",
                new FakeCredential(),
                configure: o =>
                {
                    o.EnableSemanticRanking = true;
                    o.KNearestNeighborsCount = 49;
                })));

        Assert.Equal("configure", error.ParamName);
    }

    private static AzureSearchClientOptions ClientOptions(HttpMessageHandler handler)
    {
        var options = new AzureSearchClientOptions { Transport = new HttpClientTransport(handler) };
        options.Retry.MaxRetries = 0;
        return options;
    }

    /// <summary>Records each request's credentials and refuses it, so no test needs a service.</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        public List<(string? Scheme, string? Parameter, bool SentApiKey)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authorization = request.Headers.Authorization;
            Requests.Add((authorization?.Scheme, authorization?.Parameter, request.Headers.Contains("api-key")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }

    /// <summary>A credential that hands out a fixed token and is never redeemed anywhere.</summary>
    private sealed class FakeCredential : TokenCredential
    {
        public const string Token = "fake-token";

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(Token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
