using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.NET.Api.Authentication;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Diagnostics.AspNetCore;
using Rag.NET.Diagnostics.Internal;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// The endpoint, and the three things it must not get wrong: it is behind the API key, it is silent
/// when capture is not registered, and it hands back the trace you asked for.
/// </summary>
public sealed class TraceEndpointTests
{
    private const string Prefix = "/ragnet/traces";
    private const string ApiKey = "secret";
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";

    [Fact]
    public async Task WithoutTheApiKey_TheRouteRefuses()
    {
        using var host = await StartAsync(StoreWith(TraceId));
        using var client = host.GetTestClient();

        var response = await client.GetAsync(Prefix, TestContext.Current.CancellationToken);

        // The protection is ApiKeyMiddleware's, not the endpoint's: the route is behind the key by
        // being mapped into an authenticated application and by not being exempt from it, the way the
        // webhook route deliberately is. This test exists because that is a claim about two packages
        // and neither of them alone can be trusted to keep it.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutTheApiKey_AFetchByIdRefusesToo()
    {
        using var host = await StartAsync(StoreWith(TraceId));
        using var client = host.GetTestClient();

        // Both routes, not just the list one. A trace fetched by id is the response that actually
        // carries captured content.
        var response = await client.GetAsync($"{Prefix}/{TraceId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithTheApiKey_ATraceIsReturnedById()
    {
        using var host = await StartAsync(StoreWith(TraceId));
        using var client = Authenticated(host);

        var trace = await client.GetFromJsonAsync<RagTrace>(
            $"{Prefix}/{TraceId}", TestContext.Current.CancellationToken);

        Assert.NotNull(trace);
        Assert.Equal(TraceId, trace.TraceId);
        Assert.Equal("hash-of-the-query", trace.QueryHash);
    }

    [Fact]
    public async Task WithTheApiKey_TheListRouteSummarisesWhatIsRetained()
    {
        using var host = await StartAsync(StoreWith(TraceId));
        using var client = Authenticated(host);

        var summaries = await client.GetFromJsonAsync<RagTraceSummary[]>(
            Prefix, TestContext.Current.CancellationToken);

        Assert.NotNull(summaries);

        var summary = Assert.Single(summaries);
        Assert.Equal(TraceId, summary.TraceId);
        Assert.Equal(1, summary.ChunkCount);

        // Summaries, not whole traces: a fully capturing buffer of 50 traces is megabytes of document
        // text, and a list request should not be the way to get it.
        Assert.False(summary.HasCapturedText);
    }

    [Fact]
    public async Task AnUnknownTraceId_Is404()
    {
        using var host = await StartAsync(StoreWith(TraceId));
        using var client = Authenticated(host);

        var response = await client.GetAsync($"{Prefix}/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WithDiagnosticsNotRegistered_TheListRouteIsEmptyRatherThanBroken()
    {
        using var host = await StartAsync(store: null);
        using var client = Authenticated(host);

        var summaries = await client.GetFromJsonAsync<RagTraceSummary[]>(
            Prefix, TestContext.Current.CancellationToken);

        // Mapping the endpoint without calling AddRagDiagnostics is a configuration a deployment can
        // easily be in — capture turned off, route left mapped. "No traces" is the honest answer; a 500
        // would land in someone's alerting as a fault.
        Assert.NotNull(summaries);
        Assert.Empty(summaries);
    }

    [Fact]
    public async Task WithDiagnosticsNotRegistered_AFetchIs404RatherThanBroken()
    {
        using var host = await StartAsync(store: null);
        using var client = Authenticated(host);

        var response = await client.GetAsync($"{Prefix}/{TraceId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A client that carries a valid API key.</summary>
    /// <param name="host">The running test host.</param>
    /// <returns>The client.</returns>
    private static HttpClient Authenticated(IHost host)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        return client;
    }

    /// <summary>Starts an authenticated application with the trace routes mapped.</summary>
    /// <param name="store">
    /// The trace store to register, or <see langword="null"/> to model an application that mapped the
    /// endpoint without registering diagnostics.
    /// </param>
    /// <returns>The running host.</returns>
    private static async Task<IHost> StartAsync(ITraceStore? store) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.Configure<ApiKeyOptions>(o => o.ApiKeys = [ApiKey]);

                    if (store is not null)
                        services.AddSingleton(store);
                })
                .Configure(app =>
                {
                    app.UseRagNetApiAuthentication();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapRagNetTrace());
                }))
            .StartAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

    /// <summary>A store holding one committed trace.</summary>
    /// <param name="traceId">The id to file it under.</param>
    /// <returns>The store.</returns>
    private static ITraceStore StoreWith(string traceId)
    {
        var buffer = new TraceRingBuffer(capacity: 10);

        buffer.Add(new RagTrace
        {
            TraceId = traceId,
            StartedAt = DateTimeOffset.UnixEpoch,
            QueryHash = "hash-of-the-query",
            Chunks = [new TraceChunk { DocumentId = "doc-a", ChunkIndex = 0, Score = 0.91 }],
        });

        return buffer;
    }
}
