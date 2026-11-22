using System.Text;
using Grpc.Core;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.DependencyInjection;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.Grpc.Services;

internal sealed class RagGrpcService(
    IRagPipeline pipeline, RagGrpcApiOptions apiOptions, IGuidProvider? guidProvider = null)
    : RagService.RagServiceBase
{
    private readonly IGuidProvider _guidProvider = guidProvider ?? SystemGuidProvider.Instance;

    public override async Task<IngestResponse> Ingest(IngestRequest request, ServerCallContext context)
    {
        var docId = string.IsNullOrEmpty(request.DocumentId)
            ? _guidProvider.NewGuid().ToString()
            : request.DocumentId;

        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId(docId),
            FileName = string.IsNullOrEmpty(request.FileName) ? "document.txt" : request.FileName,
            ContentType = string.IsNullOrEmpty(request.ContentType) ? null : request.ContentType,
            Tags = ToTags(request.Tags)
        };

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request.Content));
        var result = await pipeline.IngestAsync(stream, metadata, cancellationToken: context.CancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw new RpcException(new Status(ToStatusCode(result.Error), $"Ingestion failed: {result.Error}"));

        return new IngestResponse
        {
            DocumentId = result.Value.DocumentId,
            ChunksStored = result.Value.ChunksStored
        };
    }

    public override async Task<RetrieveResponse> Retrieve(RetrieveRequest request, ServerCallContext context)
    {
        if (ProtoOptionsMapper.Reject(request.Options, "options") is { } rejection)
            throw new RpcException(new Status(StatusCode.InvalidArgument, rejection));

        var options = ProtoOptionsMapper.ToRetrievalOptions(request.Options);
        var result = await pipeline.RetrieveAsync(request.Query, options, context.CancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw new RpcException(new Status(ToStatusCode(result.Error), $"Retrieval failed: {result.Error}"));

        var response = new RetrieveResponse();
        response.Results.AddRange(result.Value.Select(ToProto));
        return response;
    }

    public override async Task<AskResponse> Ask(AskRequest request, ServerCallContext context)
    {
        var options = ToRagOptions(request);
        RagResponse result;
        try
        {
            result = await pipeline.AskAsync(request.Query, options, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (AsRefusal(ex) is { } refusal)
        {
            throw Refused(refusal);
        }

        var response = new AskResponse { Answer = result.Answer };
        response.Sources.AddRange(result.Sources.Select(ToProto));
        return response;
    }

    public override async Task AskStream(
        AskRequest request,
        IServerStreamWriter<AskStreamUpdate> responseStream,
        ServerCallContext context)
    {
        var options = ToRagOptions(request);
        try
        {
            await foreach (var update in pipeline.AskStreamingAsync(request.Query, options, context.CancellationToken).ConfigureAwait(false))
            {
                if (update.TextDelta is not null)
                {
                    await responseStream.WriteAsync(new AskStreamUpdate { TextDelta = update.TextDelta }, context.CancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (AsRefusal(ex) is { } refusal)
        {
            throw Refused(refusal);
        }
    }

    // AskAsync throws a refusal itself when an answer engine's call is refused, and wraps it in an
    // InvalidOperationException when retrieval is; either way it is ResourceExhausted (#937).
    private static CallRefusedException? AsRefusal(Exception ex) =>
        ex as CallRefusedException ?? ex.InnerException as CallRefusedException;

    private static RpcException Refused(CallRefusedException refusal) =>
        new(new Status(StatusCode.ResourceExhausted, $"Call refused: {refusal.Message}"));

    public override async Task<DeleteResponse> Delete(DeleteRequest request, ServerCallContext context)
    {
        await pipeline.DeleteAsync(request.DocumentId, context.CancellationToken).ConfigureAwait(false);
        return new DeleteResponse();
    }

    // Rejected options fail the call with InvalidArgument, naming the fields, before anything runs.
    private RagOptions? ToRagOptions(AskRequest request)
    {
        if (ProtoOptionsMapper.Reject(request.Options, apiOptions.AllowClientPromptOverrides) is { } rejection)
            throw new RpcException(new Status(StatusCode.InvalidArgument, rejection));

        return ProtoOptionsMapper.ToRagOptions(request.Options);
    }

    // A refused call, usually a spent cost budget, is a quota rather than a fault: ResourceExhausted,
    // the status the HTTP API's 429 corresponds to (#937). Every other failure stays Internal.
    private static StatusCode ToStatusCode(RagError error) =>
        error is RagError.CallRefused ? StatusCode.ResourceExhausted : StatusCode.Internal;

    // The inbound side of the same wire contract as ToProto below: the proto map carries
    // strings, so every tag arrives as a String-kind value. A typed proto map is follow-up
    // work tracked with the typed-metadata change.
    private static Dictionary<string, MetadataValue> ToTags(
        Google.Protobuf.Collections.MapField<string, string> tags)
    {
        var result = new Dictionary<string, MetadataValue>(tags.Count, StringComparer.Ordinal);
        foreach (var (key, value) in tags)
            result[key] = value;
        return result;
    }

    private static SearchResultProto ToProto(SearchResult r)
    {
        var proto = new SearchResultProto
        {
            Text = r.Chunk.Text,
            DocumentId = r.Chunk.DocumentId,
            ChunkIndex = r.Chunk.ChunkIndex,
            Score = r.Score
        };
        // The gRPC wire format still carries metadata as strings (ToString is lossless as text
        // but drops the kind); a typed proto map is follow-up work tracked with the
        // typed-metadata change.
        foreach (var kvp in r.Chunk.Metadata)
            proto.Metadata[kvp.Key] = kvp.Value.ToString();
        return proto;
    }
}
