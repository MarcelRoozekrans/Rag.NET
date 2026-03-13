using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Rag.NET.Api.Authentication;

internal sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options)
{
    private const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (options.Value.ApiKeys.Length > 0)
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
}
