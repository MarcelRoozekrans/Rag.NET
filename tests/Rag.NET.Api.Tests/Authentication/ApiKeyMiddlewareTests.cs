using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Api.Authentication;
using Xunit;

namespace Rag.NET.Api.Tests.Authentication;

public sealed class ApiKeyMiddlewareTests
{
#pragma warning disable ASPDEPR004 // WebHostBuilder is deprecated in favor of HostBuilder/WebApplicationBuilder — intentional for TestServer usage
#pragma warning disable ASPDEPR008 // TestServer(IWebHostBuilder) is deprecated — intentional for minimal test setup
    private static HttpClient CreateClient(string[] validKeys)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
                services.Configure<ApiKeyOptions>(o => o.ApiKeys = validKeys))
            .Configure(app =>
            {
                app.UseMiddleware<ApiKeyMiddleware>();
                app.Run(ctx => ctx.Response.WriteAsync("ok"));
            });
        return new TestServer(builder).CreateClient();
    }
#pragma warning restore ASPDEPR008
#pragma warning restore ASPDEPR004

    [Fact]
    public async Task Returns401_WhenNoApiKeyHeader()
    {
        var client = CreateClient(["secret"]);
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Returns401_WhenWrongApiKey()
    {
        var client = CreateClient(["secret"]);
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Returns200_WhenValidApiKey()
    {
        var client = CreateClient(["secret"]);
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AcceptsAnyOfMultipleKeys()
    {
        var client = CreateClient(["key1", "key2"]);
        client.DefaultRequestHeaders.Add("X-Api-Key", "key2");
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
