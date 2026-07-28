using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Diagnostics;

namespace Rag.NET.Diagnostics.AspNetCore;

/// <summary>Maps the trace routes. Explicitly, and never as a side effect of anything else.</summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>The route prefix used when none is given.</summary>
    private const string DefaultRoutePrefix = "/ragnet/traces";

    /// <summary>
    /// Maps <c>GET {prefix}</c> — recent traces, newest first — and <c>GET {prefix}/{traceId}</c>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="routePrefix">
    /// Where the routes live. Defaults to <c>/ragnet/traces</c>. Whatever you choose, it must
    /// <b>not</b> appear in <c>ApiKeyOptions.ExemptPathPrefixes</c> — see the remarks.
    /// </param>
    /// <returns>The route builder.</returns>
    /// <remarks>
    /// <para>
    /// <b>There is no automatic registration and there will not be one.</b> A trace can hold the
    /// user's question, the retrieved document text, the assembled prompt and the answer, depending on
    /// which <c>Capture*</c> flags are on — and, because capture is deliberately not re-sanitised, it
    /// can hold content the pipeline itself went on to strip. Putting that behind an HTTP route is a
    /// decision an application makes on purpose, in one visible line, not something a package does for
    /// it because a reference happened to be present.
    /// </para>
    /// <para>
    /// <b>Authentication comes from the application, not from here.</b> <c>ApiKeyMiddleware</c> in
    /// <c>Rag.NET.Api</c> guards the whole pipeline: every path is checked unless it starts with one of
    /// <c>ApiKeyOptions.ExemptPathPrefixes</c>, which is how the webhook route — authenticated by an
    /// HMAC signature over its own body instead — opts out. These routes carry no alternative
    /// authentication, so they must simply not be exempt. Call
    /// <c>app.UseRagNetApiAuthentication()</c> with at least one API key configured and the routes are
    /// behind it; map them into an application with no authentication and they are as open as
    /// everything else in it.
    /// </para>
    /// <para>
    /// Mapping without <c>AddRagDiagnostics</c> is not an error. The list route answers with an empty
    /// list and a fetch answers 404 — a monitored deployment that has not turned capture on should read
    /// as "no traces", not as a 500 in someone's alerting.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="routePrefix"/> is empty or whitespace.</exception>
    public static IEndpointRouteBuilder MapRagNetTrace(
        this IEndpointRouteBuilder endpoints,
        string routePrefix = DefaultRoutePrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(routePrefix);

        var prefix = Normalise(routePrefix);

        endpoints.MapGet(prefix, static (HttpContext context) => ListTraces(context));
        endpoints.MapGet($"{prefix}/{{traceId}}", static (HttpContext context, string traceId) =>
            FetchTrace(context, traceId));

        return endpoints;
    }

    /// <summary>Answers with a summary per retained trace, newest first.</summary>
    /// <param name="context">The request, for the request-scoped service provider.</param>
    /// <returns>200 with the summaries, or 200 with an empty list when capture is not registered.</returns>
    private static IResult ListTraces(HttpContext context)
    {
        var store = context.RequestServices.GetService<ITraceStore>();

        if (store is null)
            return Results.Ok(Array.Empty<RagTraceSummary>());

        var traces = store.Snapshot();
        var summaries = new RagTraceSummary[traces.Count];

        for (var i = 0; i < traces.Count; i++)
            summaries[i] = RagTraceSummary.Of(traces[i]);

        return Results.Ok(summaries);
    }

    /// <summary>Answers with one whole trace.</summary>
    /// <param name="context">The request, for the request-scoped service provider.</param>
    /// <param name="traceId">The id from the route.</param>
    /// <returns>
    /// 200 with the trace, or 404. One status for "never seen", "already evicted" and "capture is not
    /// registered": the caller can do nothing different about any of them, and distinguishing them
    /// would tell an unauthenticated prober whether an id had ever existed.
    /// </returns>
    private static IResult FetchTrace(HttpContext context, string traceId)
    {
        var store = context.RequestServices.GetService<ITraceStore>();

        return store is not null && store.TryGet(traceId, out var trace)
            ? Results.Ok(trace)
            : Results.NotFound();
    }

    /// <summary>Makes a prefix into a route pattern the two routes can be built from.</summary>
    /// <param name="routePrefix">The prefix as configured.</param>
    /// <returns>The prefix with a leading slash and no trailing one.</returns>
    private static string Normalise(string routePrefix)
    {
        var trimmed = routePrefix.Trim().TrimEnd('/');

        return trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";
    }
}
