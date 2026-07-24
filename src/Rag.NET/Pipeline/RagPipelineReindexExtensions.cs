using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Pipeline;

/// <summary>
/// Extension methods that re-embed documents whose stored vectors were produced by a
/// different embedding model (or dimension) than the currently configured one.
/// </summary>
public static class RagPipelineReindexExtensions
{
    /// <summary>Constant probe text used to learn the current model's vector dimension.</summary>
    private const string DimensionProbeText = "ragnet dimension probe";

    /// <summary>
    /// Re-indexes every document whose embedding version stamp differs from the current
    /// model identity or vector dimension. Stale documents are re-embedded from the chunk
    /// text stored by <paramref name="dataManager"/> (chunks are reused verbatim — no
    /// re-parse or re-chunk) and re-stored via <paramref name="vectorStore"/> (which
    /// replaces by <c>(DocumentId, ChunkIndex)</c>), then re-stamped. Without a data
    /// manager, stale documents are only reported (<see cref="ReindexResult.ReportedStale"/>).
    /// Per-document failures are collected in <see cref="ReindexResult.Failed"/> and the
    /// loop continues. When <paramref name="sparseGenerator"/> is supplied and the store is
    /// <see cref="ISparseSearchable"/>, sparse vectors are regenerated too (a sparse
    /// failure is logged; dense re-indexing still counts as success).
    /// </summary>
    /// <remarks>
    /// Dimension staleness is detected against the current model's actual output dimension,
    /// learned by embedding one constant probe text — this costs a single embedding call per
    /// run, and only when at least one stamp matches the current model id.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The current embedding model identity is unresolvable (no
    /// <see cref="EmbeddingGeneratorMetadata"/> with a model id and no
    /// <see cref="EmbeddingVersioningOptions.ModelId"/> override).
    /// </exception>
    public static async Task<ReindexResult> ReindexStaleAsync(
        this IRagPipeline pipeline,
        IEmbeddingVersionStore versionStore,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IVectorStore vectorStore,
        IRagDataManager? dataManager = null,
        EmbeddingVersioningOptions? options = null,
        IngestionOptions? ingestionOptions = null,
        ISparseEmbeddingGenerator? sparseGenerator = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(versionStore);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(vectorStore);

        var modelId = EmbeddingModelIdentity.Resolve(embedder, options)
            ?? throw new InvalidOperationException(
                "Cannot re-index: the embedding model identity is unresolvable. The embedding generator " +
                "exposes no EmbeddingGeneratorMetadata with a model id — set EmbeddingVersioningOptions.ModelId " +
                "explicitly (UseEmbeddingVersioning(o => o.ModelId = \"...\")).");

        var log = logger ?? NullLogger.Instance;
        var batchOptions = ingestionOptions ?? new IngestionOptions();
        var entries = await versionStore.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var reindexed = new List<string>();
        var reportedStale = new List<string>();
        var failed = new List<(string DocumentId, string Error)>();
        int? currentDimension = null;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(entry.ModelId, modelId, StringComparison.Ordinal))
            {
                currentDimension ??= await ProbeDimensionAsync(embedder, cancellationToken).ConfigureAwait(false);
                if (entry.Dimension == currentDimension.Value)
                    continue; // fresh
            }

            if (dataManager is null)
            {
                reportedStale.Add(entry.DocumentId);
                continue;
            }

            try
            {
                var dimension = await ReindexDocumentAsync(
                    dataManager, embedder, vectorStore, sparseGenerator, entry.DocumentId,
                    batchOptions, log, cancellationToken).ConfigureAwait(false);
                await versionStore.SetAsync(entry.DocumentId, modelId, dimension, cancellationToken).ConfigureAwait(false);
                reindexed.Add(entry.DocumentId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RagPipelineLog.ReindexDocumentFailed(log, entry.DocumentId, ex);
                failed.Add((entry.DocumentId, ex.Message));
            }
        }

        return new ReindexResult { Reindexed = reindexed, ReportedStale = reportedStale, Failed = failed };
    }

    /// <summary>
    /// DI convenience overload: resolves the version store (required —
    /// <c>UseEmbeddingVersioning</c> must have been called), embedder, vector store, and
    /// the optional data manager, options, and sparse generator from
    /// <paramref name="services"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="IEmbeddingVersionStore"/> is registered.
    /// </exception>
    public static Task<ReindexResult> ReindexStaleAsync(
        this IRagPipeline pipeline,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(services);

        var versionStore = services.GetService<IEmbeddingVersionStore>()
            ?? throw new InvalidOperationException(
                "ReindexStaleAsync requires an IEmbeddingVersionStore — call UseEmbeddingVersioning when configuring Rag.NET.");

        return pipeline.ReindexStaleAsync(
            versionStore,
            services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            services.GetRequiredService<IVectorStore>(),
            services.GetService<IRagDataManager>(),
            services.GetService<EmbeddingVersioningOptions>(),
            ingestionOptions: null,
            services.GetService<ISparseEmbeddingGenerator>(),
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(RagPipelineReindexExtensions)),
            cancellationToken);
    }

    /// <summary>Learns the current model's output dimension by embedding one probe text.</summary>
    private static async Task<int> ProbeDimensionAsync(
        IEmbeddingGenerator<string, Embedding<float>> embedder, CancellationToken ct)
    {
        var generated = await embedder.GenerateAsync([DimensionProbeText], cancellationToken: ct).ConfigureAwait(false);
        if (generated.Count == 0)
        {
            throw new InvalidOperationException(
                "Embedding generator returned no embedding for the dimension probe; cannot determine the current vector dimension.");
        }

        return generated[0].Vector.Length;
    }

    /// <summary>
    /// Re-embeds one document's stored chunks (batched by
    /// <see cref="IngestionOptions.EmbedBatchSize"/>), re-stores dense (and, when
    /// available, sparse) vectors, and returns the new dense dimension.
    /// </summary>
    private static async Task<int> ReindexDocumentAsync(
        IRagDataManager dataManager,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IVectorStore vectorStore,
        ISparseEmbeddingGenerator? sparseGenerator,
        string documentId,
        IngestionOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        var chunks = await dataManager.GetChunksAsync(documentId, ct).ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            throw new InvalidOperationException(
                $"The data manager returned no stored chunks for document '{documentId}'; re-ingest it from its source.");
        }

        var vectors = await EmbedInBatchesAsync(embedder, chunks, options, documentId, ct).ConfigureAwait(false);

        var embedded = new List<EmbeddedChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
            embedded.Add(new EmbeddedChunk { Chunk = chunks[i], Embedding = vectors[i] });

        await vectorStore.StoreAsync(embedded, ct).ConfigureAwait(false);
        await RegenerateSparseAsync(vectorStore, sparseGenerator, embedded, documentId, logger, ct).ConfigureAwait(false);

        return vectors[0].Length;
    }

    /// <summary>Sequential local batching loop; all chunks are re-embedded from stored text.</summary>
    private static async Task<ReadOnlyMemory<float>[]> EmbedInBatchesAsync(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IReadOnlyList<TextChunk> chunks,
        IngestionOptions options,
        string documentId,
        CancellationToken ct)
    {
        var vectors = new ReadOnlyMemory<float>[chunks.Count];
        for (var start = 0; start < chunks.Count; start += options.EmbedBatchSize)
        {
            var count = Math.Min(options.EmbedBatchSize, chunks.Count - start);
            var texts = new List<string>(count);
            for (var i = 0; i < count; i++)
                texts.Add(chunks[start + i].Text);

            var generated = await embedder.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
            if (generated.Count != texts.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding generator returned {generated.Count} embeddings for {texts.Count} inputs (document '{documentId}').");
            }

            for (var i = 0; i < generated.Count; i++)
                vectors[start + i] = generated[i].Vector;
        }

        return vectors;
    }

    /// <summary>
    /// Regenerates sparse vectors from the same stored text when a sparse generator and a
    /// sparse-capable store are both available. Degraded, never broken: a sparse failure
    /// is logged and the dense re-index still succeeds.
    /// </summary>
    private static async Task RegenerateSparseAsync(
        IVectorStore vectorStore,
        ISparseEmbeddingGenerator? sparseGenerator,
        IReadOnlyList<EmbeddedChunk> embedded,
        string documentId,
        ILogger logger,
        CancellationToken ct)
    {
        if (sparseGenerator is null || vectorStore is not ISparseSearchable sparseStore)
            return;

        try
        {
            var items = new List<(EmbeddedChunk Chunk, SparseVector Sparse)>(embedded.Count);
            foreach (var ec in embedded)
                items.Add((ec, await sparseGenerator.GenerateAsync(ec.Chunk.Text, ct).ConfigureAwait(false)));

            await sparseStore.StoreSparseAsync(items, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.SparseStorageFailed(logger, documentId, ex);
        }
    }
}
