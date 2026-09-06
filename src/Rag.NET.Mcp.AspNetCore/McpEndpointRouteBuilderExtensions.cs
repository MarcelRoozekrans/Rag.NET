using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Mcp.AspNetCore;

/// <summary>
/// Maps the Rag.NET MCP endpoints with their authentication attached.
/// </summary>
public static class McpEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the MCP Streamable HTTP endpoints and attaches the API-key check to them.
    /// </summary>
    /// <param name="endpoints">The route builder to map onto.</param>
    /// <param name="pattern">The route prefix. Defaults to <c>/mcp</c>.</param>
    /// <returns>The convention builder for the mapped endpoints.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="McpServerBuilderExtensions.WithRagNetHttpTransport"/> was not called, so no
    /// authentication decision exists to enforce.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The check is an endpoint filter on the endpoints this maps, not middleware the caller
    /// installs.</b> That is deliberately stronger than the marker <c>Rag.NET.Api</c> uses for the
    /// same problem (#189): there, <c>UseRagNetApiAuthentication</c> and <c>MapRagNetApi</c> are
    /// two calls on two builders that cannot see each other, so the best available guard is to
    /// DETECT the missing one and throw. Here <c>MapMcp</c> returns the convention builder for the
    /// endpoints it just created, so auth and endpoints are attached to the same object and
    /// "mapped but unauthenticated" is not expressible rather than merely detected.
    /// </para>
    /// <para>
    /// <b>Scoped to these endpoints only</b>, unlike <c>ragnet-mcp</c>'s global <c>app.Use</c>.
    /// A host that mounts MCP beside its own unrelated endpoints must not have those endpoints
    /// authenticated by this key as a side effect.
    /// </para>
    /// </remarks>
    public static IEndpointConventionBuilder MapRagNetMcp(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetService<RagNetMcpHttpOptions>()
            ?? throw new InvalidOperationException(
                "MapRagNetMcp() was called without WithRagNetHttpTransport(), so there is no " +
                "authentication decision to enforce and the MCP transport is not registered. " +
                "Call services.AddRagNetMcpServer().WithRagNetHttpTransport(o => ...) first.");

        return endpoints
            .MapMcp(pattern)
            .AddEndpointFilter(new McpApiKeyEndpointFilter(options));
    }
}
