using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Mcp;
using Rag.NET.Mcp.AspNetCore;
using Rag.NET.Mcp.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Rag.NET.Mcp.AspNetCore.Tests;

/// <summary>
/// The red run issue #198 asks for: stand the MCP HTTP host up and assert an unauthenticated call
/// is rejected.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real host, not a mock.</b> The defect is an endpoint that answers, so anything short of an
/// actual request through the real routing and filter pipeline cannot see it. Before this package
/// existed, a consumer following <c>docs/guide/mcp.mdx</c> reached
/// <c>AddRagNetMcpServer().Server.WithHttpTransport()</c> and got a remote <b>write</b> surface —
/// <c>rag_ingest</c> included — with nothing authenticating it.
/// </para>
/// <para>
/// <b>The happy path alone would prove nothing.</b> A host with no authentication at all passes
/// "the right key works". The tests that carry the claim are the two rejections, and deleting the
/// <c>AddEndpointFilter</c> call in <c>MapRagNetMcp</c> must fail them.
/// </para>
/// </remarks>
public sealed class McpHttpAuthenticationTests
{
    private const string Key = "the-configured-key";
    private const string Pattern = "/mcp";

    [Fact]
    public async Task NoHeader_IsRejected()
    {
        using var host = await StartAsync(o => o.ApiKey = Key);
        using var client = host.GetTestClient();

        var response = await client.PostAsync(
            new Uri(Pattern, UriKind.Relative), EmptyJson(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongKey_IsRejected()
    {
        using var host = await StartAsync(o => o.ApiKey = Key);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(McpApiKeyAuthorization.HeaderName, "not-the-key");

        var response = await client.PostAsync(
            new Uri(Pattern, UriKind.Relative), EmptyJson(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The right key must NOT be rejected — otherwise the guard is just a closed door.</summary>
    /// <remarks>
    /// It asserts "not 401" rather than a specific success code: what comes back is the MCP
    /// transport's business and varies with the protocol handshake. The claim here is only that
    /// authentication let the request through to it.
    /// </remarks>
    [Fact]
    public async Task RightKey_ReachesTheTransport()
    {
        using var host = await StartAsync(o => o.ApiKey = Key);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(McpApiKeyAuthorization.HeaderName, Key);

        var response = await client.PostAsync(
            new Uri(Pattern, UriKind.Relative), EmptyJson(), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The opt-out stays a real opt-out.</summary>
    [Fact]
    public async Task AllowAnonymous_ServesWithoutAKey()
    {
        using var host = await StartAsync(o => o.AllowAnonymous = true);
        using var client = host.GetTestClient();

        var response = await client.PostAsync(
            new Uri(Pattern, UriKind.Relative), EmptyJson(), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Neither a key nor an opt-out is an unmade decision, and it fails at startup.</summary>
    [Fact]
    public void NeitherKeyNorOptOut_ThrowsAtConfiguration()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddRagNet();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddRagNetMcpServer().WithRagNetHttpTransport());

        Assert.Contains("rag_ingest", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RagNetMcpHttpOptions.AllowAnonymous), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Mapping without the transport call has no decision to enforce, and says so.</summary>
    [Fact]
    public async Task MapWithoutTransport_Throws()
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IVectorStore>());
                services.AddRagNet();
                services.AddRagNetMcpServer();
                services.AddRouting();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e => e.MapRagNetMcp(Pattern));
            });
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                using var host = await builder.StartAsync(TestContext.Current.CancellationToken);
            });

        Assert.Contains("WithRagNetHttpTransport", ex.Message, StringComparison.Ordinal);
    }

    private static StringContent EmptyJson() => new("{}", System.Text.Encoding.UTF8, "application/json");

    private static async Task<IHost> StartAsync(Action<RagNetMcpHttpOptions> configure)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                // A substituted store keeps this about the HTTP surface. The tools are registered
                // for real -- rag_ingest is the reason this guard exists -- but nothing here needs
                // them to retrieve anything.
                services.AddSingleton(Substitute.For<IVectorStore>());
                services.AddRagNet();
                services.AddRagNetMcpServer().WithRagNetHttpTransport(configure);
                services.AddRouting();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e => e.MapRagNetMcp(Pattern));
            });
        });

        return await builder.StartAsync(TestContext.Current.CancellationToken);
    }
}
