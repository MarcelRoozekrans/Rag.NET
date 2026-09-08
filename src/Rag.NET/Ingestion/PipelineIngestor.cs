using System.Diagnostics;
using Microsoft.Extensions.Logging;
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
    [Inject(Required = false)] public IEmbeddingVersionStore? VersionStore { get; set; }

    /// <summary>Stores outside this assembly that hold document-derived data — see #338.</summary>
    /// <remarks>
    /// A collection, and defaulted to empty, so a package registering one needs no coordination
    /// with core and none being registered is the ordinary case rather than a missing dependency.
    /// <c>IRaptorLeafStore</c> is the first: it lives in <c>Rag.NET.Raptor.Store</c>, which this
    /// assembly cannot reference, so before this it was simply never told about deletions.
    /// </remarks>
    [Inject] public IEnumerable<IDocumentScopedStore> DocumentScopedStores { get; set; } = [];
    [Inject(Required = false)] public ILogger<PipelineIngestor>? Logger { get; set; }


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
        };

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.ingest");
        activity?.SetTag("document.id", metadata.DocumentId.Value);
        activity?.SetTag("content.type", metadata.ContentType);

        using var scope = Logger?.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { ["document_id"] = metadata.DocumentId.Value });

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
        catch (ModelCallException ex)
        {
            // Before this case existed the catch-all below reported a failed completion as
            // StorageFailed, whose own remarks scope it to IVectorStore/persistence — so an
            // operator was sent to inspect a store that had not been asked to do anything (#504).
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RagTelemetry.IngestErrors.Add(1);
            return Result<IngestionResult, RagError>.Failure(new RagError.ModelCallFailed(ex));
        }
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
        if (VersionStore is not null)
            await VersionStore.RemoveAsync(documentId, cancellationToken).ConfigureAwait(false);

        // Everything above is an interface in Rag.NET.Abstractions. So is this one, and for the
        // same reason -- but it is a collection because the stores implementing it live in
        // packages core cannot name. Without it a deleted document's RAPTOR leaves survived, and
        // the next corpus build turned them into a searchable summary carrying no document id.
        foreach (var store in DocumentScopedStores)
            await store.RemoveDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
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
