using Microsoft.AspNetCore.Http;

namespace Rag.NET.Mcp.AspNetCore;

/// <summary>
/// Rejects a request whose <c>X-Api-Key</c> does not satisfy the configured decision.
/// </summary>
/// <remarks>
/// The decision itself is <see cref="McpApiKeyAuthorization.IsAuthorized"/> in <c>Rag.NET.Mcp</c> —
/// shared with <c>ragnet-mcp</c> so the two cannot drift, and already fails closed when no key is
/// configured and no opt-out was chosen. This type is only the part that turns a <see langword="false"/>
/// into a 401.
/// </remarks>
internal sealed class McpApiKeyEndpointFilter(RagNetMcpHttpOptions options) : IEndpointFilter
{
    private readonly RagNetMcpHttpOptions _options = options;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.HttpContext.Request;
        var suppliedKey = request.Headers.TryGetValue(McpApiKeyAuthorization.HeaderName, out var value)
            ? value.ToString()
            : null;

        if (!McpApiKeyAuthorization.IsAuthorized(suppliedKey, _options.ApiKey, _options.AllowAnonymous))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        return await next(context).ConfigureAwait(false);
    }
}
