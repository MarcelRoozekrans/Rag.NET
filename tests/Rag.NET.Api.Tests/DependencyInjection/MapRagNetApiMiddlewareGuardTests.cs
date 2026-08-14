using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Mediator;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Api.Tests.DependencyInjection;

/// <summary>
/// The mapping-time guard against a pipeline that never got <c>UseRagNetApiAuthentication</c>.
/// <c>AddRagNetApi</c> forces authentication to be decided, but deciding it is not the same as
/// applying it: without the middleware in the pipeline the mapped endpoints answered every
/// unauthenticated request, with no error, no warning and no failing test. The prerequisite is
/// now checked where <c>MapRagNetWebhooks</c> checks its own — at mapping time, so a
/// misconfigured application does not start.
/// </summary>
public sealed class MapRagNetApiMiddlewareGuardTests
{
    private static IHostBuilder BuildHost(
        Action<RagApiOptions>? configureApi,
        bool useAuthenticationMiddleware) =>
        new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services =>
            {
                var pipeline = Substitute.For<IRagPipeline>();
                pipeline.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new RagResponse { Answer = "answer", Sources = [] }));
                services.AddSingleton(pipeline);
                services.AddSingleton(Substitute.For<IRagMediator>());
                if (configureApi is not null)
                    services.AddRagNetApi(configureApi);
                services.AddRouting();
            })
            .Configure(app =>
            {
                if (useAuthenticationMiddleware)
                    app.UseRagNetApiAuthentication();
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapRagNetApi());
            }));

    private static async Task<HttpResponseMessage> AskAsync(IHost host)
    {
        using var client = host.GetTestClient();
        using var content = new StringContent("""{"query":"q"}""", Encoding.UTF8, "application/json");
        return await client.PostAsync("/rag/ask", content, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task KeysConfiguredButMiddlewareOmitted_ThrowsAtMapping_NamingTheMissingCall()
    {
        // Without this guard the host starts happily and /rag/ask answers 200 to a request
        // carrying no key at all — an open API, not a degraded one.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildHost(o => o.ApiKeys = ["api-key"], useAuthenticationMiddleware: false)
                .StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("UseRagNetApiAuthentication", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddRagNetApiNeverCalled_ThrowsAtMapping_NamingTheMissingCall()
    {
        // The registration-time throw is only reached by applications that call the method that
        // throws. Skipping it entirely used to fall back to a default RagApiOptions and map the
        // endpoints anyway.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildHost(configureApi: null, useAuthenticationMiddleware: false)
                .StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("AddRagNetApi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeysConfiguredAndMiddlewarePresent_Maps_AndStillRejectsUnauthenticated()
    {
        // The other direction: the guard must not fire when the middleware IS there, and the
        // authentication it guards must still work.
        using var host = await BuildHost(o => o.ApiKeys = ["api-key"], useAuthenticationMiddleware: true)
            .StartAsync(TestContext.Current.CancellationToken);

        var response = await AskAsync(host);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        response.Dispose();
    }

    [Fact]
    public async Task AllowAnonymous_WithoutMiddleware_StillMapsAndServes()
    {
        // The explicit opt-out stays a working opt-out: someone who genuinely wants an open
        // endpoint gets one, middleware or no middleware.
        using var host = await BuildHost(o => o.AllowAnonymous = true, useAuthenticationMiddleware: false)
            .StartAsync(TestContext.Current.CancellationToken);

        var response = await AskAsync(host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Dispose();
    }
}
