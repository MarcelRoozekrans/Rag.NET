using System.Text;
using Grpc.Core;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.Grpc.Services;

internal sealed class RagGrpcService(IRagPipeline pipeline) : RagService.RagServiceBase
{
    public override async Task<IngestResponse> Ingest(IngestRequest request, ServerCallContext context)
    {
        var docId = string.IsNullOrEmpty(request.DocumentId)
            ? Guid.NewGuid().ToString()
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
            throw new RpcException(new Status(StatusCode.Internal, $"Ingestion failed: {result.Error}"));

        return new IngestResponse
        {
            DocumentId = result.Value.DocumentId,
            ChunksStored = result.Value.ChunksStored
        };
    }

    public override async Task<RetrieveResponse> Retrieve(RetrieveRequest request, ServerCallContext context)
    {
        var options = new RetrievalOptions
        {
            TopK = request.TopK == 0 ? 5 : request.TopK,
            UseHybridSearch = request.UseHybrid
        };

        var result = await pipeline.RetrieveAsync(request.Query, options, context.CancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.Internal, $"Retrieval failed: {result.Error}"));

        var response = new RetrieveResponse();
        response.Results.AddRange(result.Value.Select(ToProto));
        return response;
    }

    public override async Task<AskResponse> Ask(AskRequest request, ServerCallContext context)
    {
        var options = new RagOptions
        {
            TopK = request.TopK == 0 ? 5 : request.TopK,
            UseHybridSearch = request.UseHybrid
        };

        var result = await pipeline.AskAsync(request.Query, options, context.CancellationToken).ConfigureAwait(false);

        var response = new AskResponse { Answer = result.Answer };
        response.Sources.AddRange(result.Sources.Select(ToProto));
        return response;
    }

    public override async Task AskStream(
        AskRequest request,
        IServerStreamWriter<AskStreamUpdate> responseStream,
        ServerCallContext context)
    {
        var options = new RagOptions
        {
            TopK = request.TopK == 0 ? 5 : request.TopK,
            UseHybridSearch = request.UseHybrid
        };

        await foreach (var update in pipeline.AskStreamingAsync(request.Query, options, context.CancellationToken).ConfigureAwait(false))
        {
            if (update.TextDelta is not null)
            {
                await responseStream.WriteAsync(new AskStreamUpdate { TextDelta = update.TextDelta }, context.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override async Task<DeleteResponse> Delete(DeleteRequest request, ServerCallContext context)
    {
        await pipeline.DeleteAsync(request.DocumentId, context.CancellationToken).ConfigureAwait(false);
        return new DeleteResponse();
    }

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
