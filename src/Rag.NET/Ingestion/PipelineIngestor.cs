using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using ZeroAlloc.Inject;

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

    public Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = new IngestionContext
        {
            Stream = document,
            Metadata = metadata,
            Options = options,
            Progress = progress,
            GetNextBm25DocId = () => System.Threading.Interlocked.Increment(ref _nextBm25DocId),
        };

        return Pipeline.ExecuteAsync(ctx, cancellationToken).AsTask();
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await VectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        Bm25Index.Remove(documentId);
        ParentStore?.Remove(documentId);
        DataManager?.Remove(documentId);
    }
}
