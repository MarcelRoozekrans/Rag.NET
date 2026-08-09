using System.Net;
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
/// Registration-time validation of the API-key configuration: an application that registers
/// the API without keys and without explicitly opting out of authentication must fail at
/// startup, not silently serve every endpoint unauthenticated.
/// </summary>
public sealed class AddRagNetApiAuthenticationTests
{
    [Fact]
    public void NoKeysAndNoOptOut_ThrowsNamingBothWaysOut()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddRagNetApi());

        Assert.Contains("ApiKeys", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AllowAnonymous", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoKeysAndNoOptOut_WithConfigureDelegate_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddRagNetApi(o => o.RoutePrefix = "/api"));

        Assert.Contains("AllowAnonymous", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithKeys_Registers()
    {
        var services = new ServiceCollection();

        services.AddRagNetApi(o => o.ApiKeys = ["key"]);

        Assert.Contains(services, d => d.ServiceType == typeof(RagApiOptions));
    }

    [Fact]
    public void AllowAnonymousWithKeys_Throws()
    {
        var services = new ServiceCollection();

        // The combination contradicts itself: keys say "authenticate", AllowAnonymous says
        // "don't". Rejected at registration rather than picking a silent winner.
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddRagNetApi(o =>
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
        var pipeline = Substitute.For<IRagPipeline>();
        pipeline.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RagResponse { Answer = "answer", Sources = [] }));

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(pipeline);
                    services.AddSingleton(Substitute.For<IRagMediator>());
                    services.AddRagNetApi(o => o.AllowAnonymous = true);
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRagNetApiAuthentication();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapRagNetApi());
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        using var client = host.GetTestClient();
        using var content = new StringContent("""{"query":"q"}""",
            System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/rag/ask", content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
