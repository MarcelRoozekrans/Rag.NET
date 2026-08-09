using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Rag.NET.Api.Authentication;

internal sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options)
{
    private const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsExempt(context.Request.Path) && !IsAuthenticated(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Fails closed: no configured keys is only acceptable when <see
    /// cref="ApiKeyOptions.AllowAnonymous"/> was set explicitly. <c>AddRagNetApi</c> already
    /// rejects the no-keys/no-opt-out state at registration, but the options object is a
    /// mutable singleton, so this request-time check is the backstop for later mutation. When
    /// keys exist, they win unconditionally — <c>AllowAnonymous</c> only ever grants access in
    /// the complete absence of keys (the two together are rejected at registration; a mutated
    /// contradiction resolves to the stricter reading).
    /// </summary>
    private bool IsAuthenticated(HttpContext context)
    {
        var current = options.Value;
        if (current.ApiKeys.Length == 0)
            return current.AllowAnonymous;

        return context.Request.Headers.TryGetValue(HeaderName, out var key)
            && current.ApiKeys.Contains(key.ToString(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Paths under an exempt prefix (e.g. the webhook route registered by
    /// <c>AddRagNetWebhooks</c>) skip API-key auth — those endpoints carry their own
    /// authentication (webhooks: HMAC signature over the raw body).
    /// </summary>
    private bool IsExempt(PathString path)
    {
        foreach (var prefix in options.Value.ExemptPathPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
