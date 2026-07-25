using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Api.Contracts;
using Rag.NET.Api.Mapping;
using Rag.NET.Api.Webhooks;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Mediator;

namespace Rag.NET.Api.DependencyInjection;

public static class EndpointRouteBuilderExtensions
{
    public static IApplicationBuilder UseRagNetApiAuthentication(this IApplicationBuilder app)
    {
        app.UseMiddleware<Authentication.ApiKeyMiddleware>();
        return app;
    }

    public static IEndpointRouteBuilder MapRagNetApi(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetService<RagApiOptions>() ?? new RagApiOptions();
        var prefix = options.RoutePrefix.TrimEnd('/');

        app.MapPost($"{prefix}/ingest", async (IngestRequest req, IMediator mediator, CancellationToken ct) =>
        {
            var docId = req.DocumentId ?? Guid.NewGuid().ToString();
            var metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(docId),
                FileName = req.FileName ?? "document.txt",
                ContentType = req.ContentType,
                Tags = req.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal)
            };
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(req.Content));
            var result = await mediator.Send(new IngestCommand(stream, metadata), ct).ConfigureAwait(false);
            return result.IsSuccess
                ? Results.Ok(new IngestResponse(result.Value.DocumentId.ToString(), result.Value.ChunksStored))
                : MapRagError(result.Error);
        });

        app.MapPost($"{prefix}/retrieve", async (RetrieveRequest req, IMediator mediator, CancellationToken ct) =>
        {
            var retrievalOptions = new RetrievalOptions { TopK = req.TopK, UseHybridSearch = req.UseHybridSearch };
            var result = await mediator.Send(new RetrieveQuery(req.Query, retrievalOptions), ct).ConfigureAwait(false);
            return result.IsSuccess
                ? Results.Ok(new RetrieveResponse(result.Value.Select(SearchResultMapper.ToDto).ToList()))
                : MapRagError(result.Error);
        });

        app.MapPost($"{prefix}/ask", async (AskRequest req, IRagPipeline pipeline, CancellationToken ct) =>
        {
            var ragOptions = new RagOptions { TopK = req.TopK, UseHybridSearch = req.UseHybridSearch };
            var result = await pipeline.AskAsync(req.Query, ragOptions, ct).ConfigureAwait(false);
            return Results.Ok(new AskResponse(result.Answer, result.Sources.Select(SearchResultMapper.ToDto).ToList()));
        });

        app.MapGet($"{prefix}/ask/stream", async (string query, IRagPipeline pipeline, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await foreach (var update in pipeline.AskStreamingAsync(query, cancellationToken: ct).ConfigureAwait(false))
            {
                if (update.TextDelta is not null)
                    await ctx.Response.WriteAsync($"data: {update.TextDelta}\n\n", ct).ConfigureAwait(false);
            }
        });

        app.MapDelete($"{prefix}/documents/{{documentId}}", async (string documentId, IMediator mediator, CancellationToken ct) =>
        {
            var deleteResult = await mediator.Send(new DeleteCommand(new DocumentId(documentId)), ct).ConfigureAwait(false);
            return deleteResult.IsSuccess
                ? Results.NoContent()
                : MapRagError(deleteResult.Error);
        });

        return app;
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

    private static IResult MapRagError(RagError err) => err switch
    {
        RagError.ValidationFailed v => Results.UnprocessableEntity(new { errors = v.Failures.Select(f => new { f.PropertyName, f.ErrorMessage }) }),
        RagError.NoParserFound n    => Results.BadRequest(new { error = $"No parser for content type: {n.ContentType}" }),
        RagError.NonSeekableStream  => Results.BadRequest(new { error = "Document stream is not readable." }),
        RagError.StorageFailed s    => Results.Problem($"Storage error: {s.Inner.Message}"),
        _                           => Results.StatusCode(500),
    };
}
