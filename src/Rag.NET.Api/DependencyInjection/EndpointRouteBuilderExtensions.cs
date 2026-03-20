using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Api.Contracts;
using Rag.NET.Api.Mapping;
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

    private static IResult MapRagError(RagError err) => err switch
    {
        RagError.ValidationFailed v => Results.UnprocessableEntity(new { errors = v.Failures.Select(f => new { f.PropertyName, f.ErrorMessage }) }),
        RagError.NoParserFound n    => Results.BadRequest(new { error = $"No parser for content type: {n.ContentType}" }),
        RagError.NonSeekableStream  => Results.BadRequest(new { error = "Document stream is not readable." }),
        RagError.StorageFailed s    => Results.Problem($"Storage error: {s.Inner.Message}"),
        _                           => Results.StatusCode(500),
    };
}
