using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

namespace Rag.NET.Ingestion;

/// <summary>
/// Thin facade over the ingestion pipeline.
/// Replaces <see cref="DocumentIngestor"/>.
/// </summary>
[Singleton(As = typeof(IIngestor))]
public sealed class PipelineIngestor : IIngestor
{
    [Inject] public Pipeline<IngestionContext, IngestionResult> Pipeline { get; set; } = null!;
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;
    [Inject(Required = false)] public IParentChunkStore? ParentStore { get; set; }
    [Inject(Required = false)] public IRagDataManager? DataManager { get; set; }

    private int _nextBm25DocId;

    public async Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!document.CanRead)
            return Result<IngestionResult, RagError>.Failure(new RagError.NonSeekableStream());

        var ctx = new IngestionContext
        {
            Stream = document,
            Metadata = metadata,
            Options = options,
            Progress = progress,
            GetNextBm25DocId = () => System.Threading.Interlocked.Increment(ref _nextBm25DocId),
        };

        try
        {
            var result = await Pipeline.ExecuteAsync(ctx, cancellationToken).ConfigureAwait(false);
            return Result<IngestionResult, RagError>.Success(result);
        }
        catch (NoParserFoundException ex)
        {
            return Result<IngestionResult, RagError>.Failure(new RagError.NoParserFound(ex.ContentType));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<IngestionResult, RagError>.Failure(new RagError.StorageFailed(ex));
        }
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await VectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        Bm25Index.Remove(documentId);
        ParentStore?.Remove(documentId);
        DataManager?.Remove(documentId);
    }
}
