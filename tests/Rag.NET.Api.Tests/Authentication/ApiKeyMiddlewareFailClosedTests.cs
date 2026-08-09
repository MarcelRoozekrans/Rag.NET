using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rag.NET.Api.Authentication;
using Rag.NET.Api.DependencyInjection;
using Xunit;

namespace Rag.NET.Api.Tests.Authentication;

/// <summary>
/// The middleware's fail-closed guarantee, separate from the registration-time guard in
/// <c>AddRagNetApi</c>: <see cref="ApiKeyOptions"/> is a mutable singleton, so a registration
/// check alone cannot prevent the no-keys state from being reached later. When no keys are
/// configured and anonymous access has not been explicitly allowed, every non-exempt request
/// must be refused — never silently served.
/// </summary>
public sealed class ApiKeyMiddlewareFailClosedTests
{
    private static async Task<IHost> StartAsync(Action<ApiKeyOptions> configureOptions) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.Configure(configureOptions))
                .Configure(app =>
                {
                    app.UseRagNetApiAuthentication();
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }))
            .StartAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

    [Fact]
    public async Task NoKeysConfigured_FailsClosedWith401()
    {
        using var host = await StartAsync(_ => { });
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task KeysEmptiedAfterRegistration_FailsClosedWith401()
    {
        using var host = await StartAsync(o => o.ApiKeys = ["secret"]);

        // The options object is a mutable singleton: registration-time validation cannot stop
        // this. The middleware itself must refuse to serve from the no-keys state.
        host.Services.GetRequiredService<IOptions<ApiKeyOptions>>().Value.ApiKeys = [];

        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AllowAnonymousNoKeys_IsServedWithoutAKey()
    {
        using var host = await StartAsync(o => o.AllowAnonymous = true);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task KeysPresent_AllowAnonymousMutatedTrue_KeysStillWin()
    {
        // Registration rejects this combination, but the options singleton is mutable, so the
        // middleware must still have an answer: configured keys win — a request without a key
        // is refused. AllowAnonymous only takes effect when no keys exist at all.
        using var host = await StartAsync(o => o.ApiKeys = ["secret"]);
        var options = host.Services.GetRequiredService<IOptions<ApiKeyOptions>>().Value;
        options.AllowAnonymous = true;

        using var client = host.GetTestClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExemptPath_IsStillServed_WhenNoKeysConfigured()
    {
        // The webhook shape: no API keys in play on the exempt route — it carries its own
        // authentication (HMAC signature) — so fail-closed must not reach it.
        using var host = await StartAsync(o => o.ExemptPathPrefixes = ["/hooks"]);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/hooks/ingest", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
