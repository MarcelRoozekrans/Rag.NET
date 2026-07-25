using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Rag.NET.Api.Authentication;

internal sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options)
{
    private const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (options.Value.ApiKeys.Length > 0 && !IsExempt(context.Request.Path))
        {
            if (!context.Request.Headers.TryGetValue(HeaderName, out var key)
                || !options.Value.ApiKeys.Contains(key.ToString(), StringComparer.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next(context).ConfigureAwait(false);
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
