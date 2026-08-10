using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Api.Grpc.Client;

public sealed class GrpcRagPipeline(RagService.RagServiceClient grpcClient) : IRagPipeline
{
    public async Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(document, Encoding.UTF8, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var request = new IngestRequest
        {
            Content = content,
            DocumentId = metadata.DocumentId,
            FileName = metadata.FileName,
            ContentType = metadata.ContentType ?? string.Empty
        };

        // The gRPC wire format still carries tags as strings (ToString is lossless as text but
        // drops the kind); a typed proto map is follow-up work tracked with the typed-metadata
        // change.
        foreach (var (k, v) in metadata.Tags)
            request.Tags[k] = v.ToString();

        var response = await grpcClient.IngestAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<IngestionResult, RagError>.Success(new IngestionResult
        {
            DocumentId = new DocumentId(response.DocumentId),
            ChunksStored = response.ChunksStored
        });
    }

    public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = new RetrieveRequest
        {
            Query = query,
            TopK = options?.TopK ?? 5,
            UseHybrid = options?.UseHybridSearch ?? true
        };

        var response = await grpcClient.RetrieveAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<SearchResult>, RagError>.Success(
            response.Results.Select(ToSearchResult).ToList());
    }

    public async Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = new AskRequest
        {
            Query = query,
            TopK = options?.TopK ?? 5,
            UseHybrid = options?.UseHybridSearch ?? true
        };

        var response = await grpcClient.AskAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new RagResponse
        {
            Answer = response.Answer,
            Sources = response.Sources.Select(ToSearchResult).ToList()
        };
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new AskRequest
        {
            Query = query,
            TopK = options?.TopK ?? 5,
            UseHybrid = options?.UseHybridSearch ?? true
        };

        using var call = grpcClient.AskStream(request, cancellationToken: cancellationToken);

        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            yield return new RagStreamingUpdate { TextDelta = call.ResponseStream.Current.TextDelta };
        }
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await grpcClient.DeleteAsync(
            new DeleteRequest { DocumentId = documentId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static SearchResult ToSearchResult(SearchResultProto proto)
    {
        var chunk = new TextChunk
        {
            Text = proto.Text,
            DocumentId = new DocumentId(proto.DocumentId),
            ChunkIndex = proto.ChunkIndex
        };

        foreach (var (k, v) in proto.Metadata)
            chunk.Metadata[k] = v;

        return new SearchResult
        {
            Chunk = chunk,
            Score = proto.Score
        };
    }
}
