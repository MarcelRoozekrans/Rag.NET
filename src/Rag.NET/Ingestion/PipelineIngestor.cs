using System.Diagnostics;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Telemetry;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;
using ZeroAlloc.Validation;

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
    [Inject] public ChunkingOptions ChunkingOptions { get; set; } = null!;
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
        var invalid = ValidateRequest(document, metadata, options);
        if (invalid is not null)
            return invalid.Value;

        var ctx = new IngestionContext
        {
            Stream = document,
            Metadata = metadata,
            Options = options,
            Progress = progress,
            GetNextBm25DocId = () => System.Threading.Interlocked.Increment(ref _nextBm25DocId),
        };

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.ingest");
        activity?.SetTag("document.id", metadata.DocumentId.Value);
        activity?.SetTag("content.type", metadata.ContentType);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await Pipeline.ExecuteAsync(ctx, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("chunk.count", result.ChunksStored);
            return Result<IngestionResult, RagError>.Success(result);
        }
        catch (NoParserFoundException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RagTelemetry.IngestErrors.Add(1);
            return Result<IngestionResult, RagError>.Failure(new RagError.NoParserFound(ex.ContentType));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RagTelemetry.IngestErrors.Add(1);
            return Result<IngestionResult, RagError>.Failure(new RagError.StorageFailed(ex));
        }
        finally
        {
            sw.Stop();
            RagTelemetry.IngestDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await VectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        Bm25Index.Remove(documentId);
        ParentStore?.Remove(documentId);
        DataManager?.Remove(documentId);
    }

    private Result<IngestionResult, RagError>? ValidateRequest(
        Stream document, DocumentMetadata metadata, IngestionOptions? options)
    {
        var chunkingValidation = new ChunkingOptionsValidator().Validate(ChunkingOptions);
        if (!chunkingValidation.IsValid)
            return Result<IngestionResult, RagError>.Failure(
                new RagError.ValidationFailed(MapFailures(chunkingValidation.Failures)));

        var validationResult = new DocumentMetadataValidator().Validate(metadata);
        if (!validationResult.IsValid)
            return Result<IngestionResult, RagError>.Failure(
                new RagError.ValidationFailed(MapFailures(validationResult.Failures)));

        if (options is not null)
        {
            var optionsValidation = new IngestionOptionsValidator().Validate(options);
            if (!optionsValidation.IsValid)
                return Result<IngestionResult, RagError>.Failure(
                    new RagError.ValidationFailed(MapFailures(optionsValidation.Failures)));
        }

        if (!document.CanRead)
            return Result<IngestionResult, RagError>.Failure(new RagError.NonSeekableStream());

        return null;
    }

    private static IReadOnlyList<Models.ValidationFailure> MapFailures(ReadOnlySpan<ZeroAlloc.Validation.ValidationFailure> failures)
    {
        var result = new Models.ValidationFailure[failures.Length];
        for (var i = 0; i < failures.Length; i++)
            result[i] = new Models.ValidationFailure(failures[i].PropertyName, failures[i].ErrorMessage);
        return result;
    }
}
