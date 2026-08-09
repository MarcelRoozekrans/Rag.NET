using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.Authentication;
using Rag.NET.Api.Grpc.DependencyInjection;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Api.Grpc.Tests;

/// <summary>
/// The gRPC mirror of the REST API-key guarantees: registration without keys and without an
/// explicit opt-out fails at startup, and the interceptor fails closed if the mutable options
/// singleton is emptied after registration.
/// </summary>
public sealed class GrpcApiKeyConfigurationTests
{
    [Fact]
    public void AddRagNetGrpcApi_NoKeysAndNoOptOut_ThrowsNamingBothWaysOut()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddRagNetGrpcApi());

        Assert.Contains("ApiKeys", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AllowAnonymous", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interceptor_FailsClosed_WhenKeysEmptiedAfterRegistration()
    {
        using var host = await StartAsync(o => o.ApiKeys = ["test-key"]);

        // The options object is a mutable singleton: registration-time validation cannot stop
        // this. The interceptor itself must refuse to serve from the no-keys state.
        host.Services.GetRequiredService<IOptions<GrpcApiKeyOptions>>().Value.ApiKeys = [];

        using var channel = CreateChannel(host, apiKey: "test-key");
        var client = new RagService.RagServiceClient(channel);

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.RetrieveAsync(
                new RetrieveRequest { Query = "test" },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public void AddRagNetGrpcApi_AllowAnonymousWithKeys_Throws()
    {
        var services = new ServiceCollection();

        // The combination contradicts itself: keys say "authenticate", AllowAnonymous says
        // "don't". Rejected at registration rather than picking a silent winner.
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddRagNetGrpcApi(o =>
        {
            o.ApiKeys = ["key"];
            o.AllowAnonymous = true;
        }));

        Assert.Contains("AllowAnonymous", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ApiKeys", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowAnonymousNoKeys_StartsAndServesAnonymously()
    {
        using var host = await StartAsync(o => o.AllowAnonymous = true);
        using var channel = CreateChannel(host, apiKey: null);
        var client = new RagService.RagServiceClient(channel);

        var response = await client.RetrieveAsync(
            new RetrieveRequest { Query = "test" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Empty(response.Results);
    }

    private static async Task<IHost> StartAsync(Action<RagGrpcApiOptions> configure)
    {
        var pipeline = Substitute.For<IRagPipeline>();
        pipeline.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success(
                (IReadOnlyList<SearchResult>)Array.Empty<SearchResult>())));

        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureKestrel(o => o.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http2))
                .ConfigureServices(services =>
                {
                    services.AddSingleton(pipeline);
                    services.AddRagNetGrpcApi(configure);
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapRagNetGrpcApi());
                }))
            .StartAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
    }

    private static GrpcChannel CreateChannel(IHost host, string? apiKey)
    {
        var httpClient = host.GetTestClient();
        if (apiKey is not null)
            httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);

        return GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = httpClient,
        });
    }
}
