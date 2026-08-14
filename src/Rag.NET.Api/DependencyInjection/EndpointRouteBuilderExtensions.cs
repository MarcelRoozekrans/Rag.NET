using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rag.NET.Abstractions;
using Rag.NET.Api.Authentication;
using Rag.NET.Api.Contracts;
using Rag.NET.Api.Mapping;
using Rag.NET.Api.Webhooks;
using Rag.NET.Mediator;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.DependencyInjection;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Adds the <c>X-Api-Key</c> middleware to the pipeline, and records that it was added so
    /// <see cref="MapRagNetApi"/> can tell an authenticated pipeline from an open one. Call it
    /// before <see cref="MapRagNetApi"/>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same <paramref name="app"/>, for chaining.</returns>
    public static IApplicationBuilder UseRagNetApiAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.ApplicationServices.GetService<ApiKeyMiddlewareMarker>()?.MarkRegistered();
        app.UseMiddleware<Authentication.ApiKeyMiddleware>();
        return app;
    }

    /// <summary>
    /// Maps the ingest, retrieve, ask, ask-stream and delete endpoints under
    /// <see cref="RagApiOptions.RoutePrefix"/>. Requires <c>AddRagNetApi</c>, and — unless
    /// <see cref="RagApiOptions.AllowAnonymous"/> was set — requires that
    /// <see cref="UseRagNetApiAuthentication"/> has already run.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same <paramref name="app"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>AddRagNetApi</c> was not called, when the authentication middleware is
    /// missing from the pipeline, and when an exempt path prefix would cover one of the mapped
    /// routes — each of which would otherwise serve endpoints unauthenticated.
    /// </exception>
    public static IEndpointRouteBuilder MapRagNetApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Not a defaulted lookup: falling back to a fresh RagApiOptions let an application skip
        // AddRagNetApi — and with it the whole authentication decision — and still get endpoints.
        var options = app.ServiceProvider.GetService<RagApiOptions>()
            ?? throw new InvalidOperationException(
                "RagApiOptions not registered. Call services.AddRagNetApi(o => ...) before MapRagNetApi(). " +
                "That call is where authentication is decided; without it these endpoints would be mapped " +
                "with default options and served to anyone.");
        ThrowIfAuthenticationMiddlewareIsMissing(app.ServiceProvider, options);
        var prefix = options.RoutePrefix.TrimEnd('/');

        // The single source of the mapped paths: the guard below checks exactly the strings
        // that are mapped, so any future exemption source is caught, not just webhooks.
        var ingest = $"{prefix}/ingest";
        var retrieve = $"{prefix}/retrieve";
        var ask = $"{prefix}/ask";
        var askStream = $"{prefix}/ask/stream";
        var deleteDocument = $"{prefix}/documents/{{documentId}}";
        ThrowIfAnyApiRouteIsAuthExempt(
            app.ServiceProvider, [ingest, retrieve, ask, askStream, deleteDocument]);

        app.MapPost(ingest, HandleIngestAsync);
        app.MapPost(retrieve, HandleRetrieveAsync);
        app.MapPost(ask, HandleAskAsync);
        app.MapGet(askStream, HandleAskStreamAsync);
        app.MapDelete(deleteDocument, HandleDeleteDocumentAsync);

        return app;
    }

    /// <summary>
    /// The mapping-time middleware guard: <c>AddRagNetApi</c> makes an application decide
    /// whether to authenticate, but nothing used to check that the decision was carried out.
    /// Omitting <see cref="UseRagNetApiAuthentication"/> left <c>ApiKeyMiddleware</c> out of the
    /// pipeline and served every mapped endpoint to every caller — silently, since the
    /// configured keys were still there to be read and simply never consulted. Detection is by
    /// the marker the <c>Use</c> call sets, not by inspecting the pipeline, which an
    /// <see cref="IEndpointRouteBuilder"/> cannot see; the corollary is that the <c>Use</c> call
    /// must come first, which is the documented order and the only one this check accepts.
    /// <para>
    /// <see cref="RagApiOptions.AllowAnonymous"/> exits early: the opt-out stays a real opt-out,
    /// and the middleware is a pass-through for an anonymous API anyway.
    /// </para>
    /// </summary>
    private static void ThrowIfAuthenticationMiddlewareIsMissing(
        IServiceProvider services, RagApiOptions options)
    {
        if (options.AllowAnonymous || services.GetService<ApiKeyMiddlewareMarker>()?.IsRegistered == true)
            return;

        throw new InvalidOperationException(
            "app.UseRagNetApiAuthentication() has not been called, so ApiKeyMiddleware is not in the " +
            "request pipeline and MapRagNetApi() would serve every endpoint unauthenticated — the " +
            $"{options.ApiKeys.Length} configured API key(s) would never be checked. Call " +
            "app.UseRagNetApiAuthentication() before MapRagNetApi(), or opt out of authentication " +
            "explicitly with services.AddRagNetApi(o => o.AllowAnonymous = true).");
    }

    /// <summary>
    /// The mapping-time collision guard: an <see cref="ApiKeyOptions.ExemptPathPrefixes"/>
    /// entry that is a parent of an API route (segment-wise, exactly as
    /// <c>ApiKeyMiddleware.IsExempt</c> matches at request time) would silently disable
    /// API-key auth on that route. <c>AddRagNetWebhooks</c> cannot detect this — the API's
    /// route prefix is not chosen until <see cref="MapRagNetApi"/> runs — so the check lives
    /// here, where both values are finally known, and inspects the actual paths being mapped.
    /// </summary>
    private static void ThrowIfAnyApiRouteIsAuthExempt(IServiceProvider services, string[] apiRoutes)
    {
        var exemptPrefixes = services.GetService<IOptions<ApiKeyOptions>>()?.Value.ExemptPathPrefixes ?? [];
        foreach (var exemptPrefix in exemptPrefixes)
        {
            foreach (var route in apiRoutes)
            {
                if (new PathString(route).StartsWithSegments(exemptPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"ApiKeyOptions.ExemptPathPrefixes contains \"{exemptPrefix}\", which would exempt " +
                        $"the API route \"{route}\" from API-key authentication. An exempt prefix (e.g. " +
                        "WebhookOptions.RoutePrefix from AddRagNetWebhooks) must not be a parent of the API's " +
                        "own routes — choose a prefix that does not cover them, such as the default " +
                        "\"/rag/webhooks\" or one outside the API prefix entirely.");
                }
            }
        }
    }

    private static async Task<IResult> HandleIngestAsync(
        IngestRequest req, IRagMediator mediator, CancellationToken ct)
    {
        var docId = req.DocumentId ?? Guid.NewGuid().ToString();
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId(docId),
            FileName = req.FileName ?? "document.txt",
            ContentType = req.ContentType,
            Tags = ToTags(req.Tags)
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(req.Content));
        var result = await mediator.Send(new IngestCommand(stream, metadata), ct).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new IngestResponse(result.Value.DocumentId.ToString(), result.Value.ChunksStored))
            : MapRagError(result.Error);
    }

    private static async Task<IResult> HandleRetrieveAsync(
        RetrieveRequest req, IRagMediator mediator, CancellationToken ct)
    {
        var retrievalOptions = new RetrievalOptions { TopK = req.TopK, UseHybridSearch = req.UseHybridSearch };
        var result = await mediator.Send(new RetrieveQuery(req.Query, retrievalOptions), ct).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new RetrieveResponse(result.Value.Select(SearchResultMapper.ToDto).ToList()))
            : MapRagError(result.Error);
    }

    private static async Task<IResult> HandleAskAsync(
        AskRequest req, IRagPipeline pipeline, CancellationToken ct)
    {
        var ragOptions = new RagOptions { TopK = req.TopK, UseHybridSearch = req.UseHybridSearch };
        var result = await pipeline.AskAsync(req.Query, ragOptions, ct).ConfigureAwait(false);
        return Results.Ok(new AskResponse(result.Answer, result.Sources.Select(SearchResultMapper.ToDto).ToList()));
    }

    private static async Task HandleAskStreamAsync(
        string query, IRagPipeline pipeline, HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        await foreach (var update in pipeline.AskStreamingAsync(query, cancellationToken: ct).ConfigureAwait(false))
        {
            if (update.TextDelta is not null)
                await ctx.Response.WriteAsync($"data: {update.TextDelta}\n\n", ct).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> HandleDeleteDocumentAsync(
        string documentId, IRagMediator mediator, CancellationToken ct)
    {
        var deleteResult = await mediator.Send(new DeleteCommand(new DocumentId(documentId)), ct).ConfigureAwait(false);
        return deleteResult.IsSuccess
            ? Results.NoContent()
            : MapRagError(deleteResult.Error);
    }

    /// <summary>
    /// Maps POST <c>{WebhookOptions.RoutePrefix}/ingest</c>: an HMAC-SHA256-verified webhook
    /// endpoint that parses the payload via the registered <see cref="IWebhookPayloadParser"/>
    /// and enqueues the resulting jobs on the <see cref="IIngestionJobQueue"/>. Requires
    /// <c>AddRagNetWebhooks</c>. The route is exempt from API-key auth — the signature over
    /// the raw body replaces the key. Responses: 202 Accepted <c>{ enqueued: n }</c>;
    /// 401 missing/invalid signature; 400 invalid JSON or rejected payload; 503 when no
    /// <see cref="IIngestionJobQueue"/> is registered.
    /// </summary>
    public static IEndpointRouteBuilder MapRagNetWebhooks(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetService<WebhookOptions>()
            ?? throw new InvalidOperationException(
                "WebhookOptions not registered. Call services.AddRagNetWebhooks(o => ...) before MapRagNetWebhooks().");
        var prefix = options.RoutePrefix.TrimEnd('/');

        app.MapPost($"{prefix}/ingest",
            (HttpContext ctx, CancellationToken ct) => HandleWebhookIngestAsync(ctx, options, ct));

        return app;
    }

    private static async Task<IResult> HandleWebhookIngestAsync(
        HttpContext ctx, WebhookOptions options, CancellationToken ct)
    {
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
            body = buffer.ToArray();
        }

        ctx.Request.Headers.TryGetValue(options.SignatureHeader, out var signature);
        if (!WebhookSignatureValidator.IsValid(body, signature.ToString(), options.Secret))
            return Results.Unauthorized();

        if (!TryParseJobs(ctx, body, out var jobs))
        {
            return Results.BadRequest(new
            {
                error = "Invalid payload: body must be valid JSON matching { documentId, content, metadata? } (single object or array) with non-empty documentId and content.",
            });
        }

        var queue = ctx.RequestServices.GetService<IIngestionJobQueue>();
        if (queue is null)
        {
            return Results.Problem(
                detail: "No IIngestionJobQueue is registered — accepted webhook jobs would never be processed. Call UseEventDrivenIngestion() on the RAG builder (Rag.NET.DataProviders package).",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        foreach (var job in jobs)
            await queue.EnqueueAsync(job, ct).ConfigureAwait(false);

        return Results.Accepted(value: new { enqueued = jobs.Count });
    }

    /// <summary>Returns <see langword="false"/> for invalid JSON or a parser-rejected payload (both → 400).</summary>
    private static bool TryParseJobs(
        HttpContext ctx, byte[] body, [NotNullWhen(true)] out IReadOnlyList<IngestionJob>? jobs)
    {
        jobs = null;
        var parser = ctx.RequestServices.GetRequiredService<IWebhookPayloadParser>();
        try
        {
            using var document = JsonDocument.Parse(body);
            return parser.TryParse(document.RootElement, out jobs);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Projects a <see cref="RagError"/> onto an HTTP response.
    /// <para>
    /// Both upstream-dependency cases answer 502. An upstream status is never echoed as this
    /// API's own: Graph returning 404 for a mailbox does not make <i>this</i> resource
    /// missing, and a 403 upstream is not a 403 against this API's caller. The distinction the
    /// two cases preserve is whether an HTTP exchange happened at all.
    /// </para>
    /// </summary>
    // The REST wire contract (IngestRequest.Tags) still carries strings, so every tag arrives
    // as a String-kind value; a typed JSON contract is follow-up work tracked with the
    // typed-metadata change.
    private static Dictionary<string, MetadataValue> ToTags(IDictionary<string, string>? tags)
    {
        var result = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        if (tags is not null)
        {
            foreach (var (key, value) in tags)
                result[key] = value;
        }

        return result;
    }

    private static IResult MapRagError(RagError err) => err switch
    {
        RagError.ValidationFailed v => Results.UnprocessableEntity(new { errors = v.Failures.Select(f => new { f.PropertyName, f.ErrorMessage }) }),
        RagError.NoParserFound n    => Results.BadRequest(new { error = $"No parser for content type: {n.ContentType}" }),
        RagError.NonSeekableStream  => Results.BadRequest(new { error = "Document stream is not readable." }),
        RagError.StorageFailed s    => Results.Problem($"Storage error: {s.Inner.Message}"),
        // An upstream dependency answered, but with a failing status.
        RagError.HttpFailed h       => Results.Problem($"Upstream HTTP error: {(int)h.StatusCode}", statusCode: 502),
        // No HTTP response was received at all (DNS/TLS/socket/timeout/token acquisition).
        RagError.TransportFailed t  => Results.Problem($"Transport error: {t.Inner.Message}", statusCode: 502),
        // Unreachable for every case defined on RagError today. RagError is not a sealed
        // hierarchy — it has no private protected constructor, so an external assembly can
        // derive from it — and C# has no exhaustiveness checking for reference types, so
        // omitting this arm would emit CS8509 and fail the build under warnings-as-errors.
        _                           => Results.StatusCode(500),
    };
}
