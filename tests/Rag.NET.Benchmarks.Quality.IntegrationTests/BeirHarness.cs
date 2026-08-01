using System.Diagnostics;
using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The embed → store → retrieve → score path every measurement runs through, shared so that runs
/// differ only in the axes measurements are about: <b>what text units get indexed</b> (the parity
/// and real protocols) and <b>how retrieval happens</b> (the <see cref="AblationRow"/>s).
/// <para>
/// Every component is the library's own — <see cref="OnnxEmbeddingGenerator"/> embeds,
/// <see cref="InMemoryVectorStore"/> stores and scores cosine, <see cref="DocumentRanking"/>
/// aggregates and <see cref="IrMetrics"/> scores. Nothing here is a benchmark-only
/// reimplementation, which is the point: a harness built out of purpose-made parts measures the
/// harness.
/// </para>
/// <para>
/// <b>Shared rather than copied, because the two runs are compared to each other.</b> The real run's
/// assertion is a relationship to our own parity run, and a relationship between two numbers
/// produced by two copies of a measurement measures the copies as much as the protocols. One
/// difference, in one argument, is the whole design.
/// </para>
/// </summary>
public static class BeirHarness
{
    /// <summary>The rank cutoff the published figures are quoted at.</summary>
    public const int Cutoff = 10;

    /// <summary>The message an unprovisioned run skips with.</summary>
    public const string SkipReason =
        "Set RAGNET_ONNX_EMBED_MODEL and RAGNET_ONNX_EMBED_VOCAB to an existing all-MiniLM-L6-v2 " +
        "ONNX export (token-level output) and its WordPiece vocab.txt, and RAGNET_BEIR_CACHE to a " +
        "writable directory for the dataset downloads, to run the BEIR measurements.";

    /// <summary>The message a run without the cross-encoder skips with.</summary>
    /// <remarks>
    /// The reranker's variables follow the embedder's convention —
    /// <c>RAGNET_ONNX_EMBED_MODEL</c>/<c>RAGNET_ONNX_EMBED_VOCAB</c> begat
    /// <c>RAGNET_ONNX_RERANK_MODEL</c>/<c>RAGNET_ONNX_RERANK_VOCAB</c> — and both are provisioned
    /// by the same pinned-revision, SHA-256-verified steps in <c>nightly.yml</c>, where the pins
    /// and their sources are recorded.
    /// </remarks>
    public const string RerankerSkipReason =
        "Set RAGNET_ONNX_RERANK_MODEL and RAGNET_ONNX_RERANK_VOCAB to an existing " +
        "cross-encoder/ms-marco-MiniLM-L6-v2 ONNX export and its WordPiece vocab.txt to run the " +
        "reranked ablation cell.";

    /// <summary>
    /// What the embedding cache's keys are salted with.
    /// </summary>
    /// <remarks>
    /// Everything that changes a vector for a fixed input text: the model, the export, the sequence
    /// length, the pooling and the normalisation. Not the title/text separator — that changes the
    /// text itself, which is already the other half of every key, so the two separators are
    /// different entries rather than a collision.
    /// </remarks>
    public const string ModelIdentity =
        "all-MiniLM-L6-v2/onnx maxTokens=256 mean-pooled-excluding-padding l2-normalised";

    /// <summary>
    /// Documents embedded per <see cref="OnnxEmbeddingGenerator.GenerateAsync"/> call. Only a
    /// working-set bound — the generator does its own padded batching underneath, and its pooling
    /// excludes padding, so no slab or batch size can change a document's vector. It also bounds
    /// what the cache is asked for at once, which matters on a 57,638-document corpus.
    /// </summary>
    private const int SlabSize = 512;

    /// <summary>
    /// Reports whether the dataset cache alone is configured — no model needed.
    /// </summary>
    /// <param name="cacheDirectory">Receives <c>RAGNET_BEIR_CACHE</c>.</param>
    /// <returns><see langword="true"/> when a corpus can be loaded.</returns>
    /// <remarks>
    /// A weaker gate than <see cref="IsProvisioned"/>, for the checks that only read text. Those run
    /// in seconds against the same corpora the measurements take an hour over, which makes them worth
    /// running first: a chunking defect found here costs nothing, and found by an nDCG that failed to
    /// move it costs the whole run.
    /// </remarks>
    public static bool IsDatasetCacheProvisioned(out string cacheDirectory)
    {
        cacheDirectory = BeirDatasetCache.ResolveCacheDirectoryFromEnvironment() ?? string.Empty;
        return cacheDirectory.Length > 0;
    }

    /// <summary>Reports whether the model, vocab and dataset cache are all present.</summary>
    /// <param name="modelPath">Receives <c>RAGNET_ONNX_EMBED_MODEL</c>.</param>
    /// <param name="vocabPath">Receives <c>RAGNET_ONNX_EMBED_VOCAB</c>.</param>
    /// <param name="cacheDirectory">Receives <c>RAGNET_BEIR_CACHE</c>.</param>
    /// <returns><see langword="true"/> when a measurement can run.</returns>
    public static bool IsProvisioned(
        out string modelPath, out string vocabPath, out string cacheDirectory)
    {
        modelPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_MODEL") ?? string.Empty;
        vocabPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_VOCAB") ?? string.Empty;
        cacheDirectory = BeirDatasetCache.ResolveCacheDirectoryFromEnvironment() ?? string.Empty;

        return File.Exists(modelPath) && File.Exists(vocabPath) && cacheDirectory.Length > 0;
    }

    /// <summary>Reports whether the cross-encoder the reranked row rescores with is present.</summary>
    /// <param name="modelPath">Receives <c>RAGNET_ONNX_RERANK_MODEL</c>.</param>
    /// <param name="vocabPath">Receives <c>RAGNET_ONNX_RERANK_VOCAB</c>.</param>
    /// <returns><see langword="true"/> when the reranked cell can run.</returns>
    /// <remarks>
    /// Separate from <see cref="IsProvisioned"/> rather than folded into it, because folding it in
    /// would make every dense, hybrid and HyDE measurement skip on a machine that has the embedder
    /// but not the cross-encoder — three rows held hostage to a model only the fourth one reads.
    /// </remarks>
    public static bool IsRerankerProvisioned(out string modelPath, out string vocabPath)
    {
        modelPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_RERANK_MODEL") ?? string.Empty;
        vocabPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_RERANK_VOCAB") ?? string.Empty;

        return File.Exists(modelPath) && File.Exists(vocabPath);
    }

    /// <summary>
    /// Builds the generator both protocols embed with.
    /// </summary>
    /// <param name="modelPath">The ONNX export.</param>
    /// <param name="vocabPath">Its WordPiece vocabulary.</param>
    /// <returns>The generator; the caller disposes it.</returns>
    /// <remarks>
    /// <c>MaxTokens</c> is deliberately left at its default of 256 — all-MiniLM-L6-v2's
    /// <c>max_seq_length</c>, and the configuration the published figures were produced under.
    /// Raising it would measure something else, and it would do so for the real run too, which is
    /// meant to differ from parity by its <i>chunking</i> and by nothing else. <c>ModelId</c> is set
    /// because a bare "model.onnx" would otherwise become every model's identity.
    /// </remarks>
    public static OnnxEmbeddingGenerator CreateGenerator(string modelPath, string vocabPath) =>
        new(new OnnxEmbeddingOptions
        {
            ModelPath = modelPath,
            TokenizerVocabPath = vocabPath,
            ModelId = "all-MiniLM-L6-v2",
        });

    /// <summary>
    /// Downloads the dataset if it is not cached, loads it, and checks it is the whole dataset
    /// before anything is scored against it.
    /// </summary>
    /// <param name="descriptor">The dataset.</param>
    /// <param name="cacheDirectory">Where archives are cached.</param>
    /// <param name="titleTextSeparator">What goes between a document's title and its text.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The loaded dataset.</returns>
    /// <remarks>
    /// The four assertions are cheap and they are the difference between a diagnosable failure and
    /// an undiagnosable one: a short corpus or a half-loaded qrels split produces a bad number that
    /// looks exactly like a retrieval defect, and this is the last point where the real cause is
    /// still visible. They come off the descriptor, so every dataset is checked the way SciFact was.
    /// </remarks>
    public static async Task<BeirDataset> LoadAsync(
        BeirDatasetDescriptor descriptor,
        string cacheDirectory,
        string titleTextSeparator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var cache = new BeirDatasetCache(cacheDirectory);
        var datasetDirectory = await cache.EnsureAsync(descriptor, cancellationToken);
        var dataset = BeirLoader.Load(datasetDirectory, "test", titleTextSeparator);

        Assert.Equal(descriptor.DocumentCount, dataset.Documents.Count);
        Assert.Equal(descriptor.QueryCount, dataset.Queries.Count);
        Assert.Equal(descriptor.TestQueryCount, dataset.JudgedQueryCount);
        Assert.Equal(descriptor.TitledDocumentCount, CountTitled(dataset.Documents));

        return dataset;
    }

    /// <summary>
    /// The parity protocol's text units: one per document, the whole document, truncated by the
    /// model at 256 tokens.
    /// </summary>
    /// <param name="documents">The corpus.</param>
    /// <returns>One chunk per document, in corpus order.</returns>
    /// <remarks>
    /// This is what BEIR's published figures embed — each corpus entry as one sequence truncated at
    /// the model's <c>max_seq_length</c>, which is exactly what <see cref="OnnxEmbeddingGenerator"/>
    /// does at <c>MaxTokens = 256</c>. It also makes <see cref="DocumentRanking"/>'s max-pooling a
    /// no-op, since every document contributes exactly one candidate.
    /// </remarks>
    public static IReadOnlyList<TextChunk> OneChunkPerDocument(IReadOnlyList<BeirDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var units = new TextChunk[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            units[i] = new TextChunk
            {
                Text = documents[i].RetrievalText,
                DocumentId = new DocumentId(documents[i].Id),
                ChunkIndex = 0,
            };
        }

        return units;
    }

    /// <summary>
    /// Embeds and indexes <paramref name="units"/>, retrieves for every query with
    /// <paramref name="row"/>, and scores the run.
    /// </summary>
    /// <param name="descriptor">The dataset, for its retrieval protocol.</param>
    /// <param name="dataset">The loaded corpus, queries and qrels.</param>
    /// <param name="units">
    /// What to index. What differs between the parity and real <b>protocols</b>; indexing stays out
    /// of the row so every row of a dataset queries one index.
    /// </param>
    /// <param name="row">
    /// How to retrieve. What differs between the ablation table's <b>rows</b>;
    /// <see cref="AblationRow.Dense"/> is what this method always did.
    /// </param>
    /// <param name="generator">The embedder.</param>
    /// <param name="embeddings">The vector cache; every embed call goes through it.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The metrics and the shape of the run that produced them.</returns>
    public static async Task<BeirRunResult> MeasureAsync(
        BeirDatasetDescriptor descriptor,
        BeirDataset dataset,
        IReadOnlyList<TextChunk> units,
        AblationRow row,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(embeddings);

        var startedAt = Stopwatch.GetTimestamp();
        var hitsBefore = embeddings.Hits;
        var missesBefore = embeddings.Misses;
        var (maxPerDocument, distinctDocuments) = SummariseUnits(units);

        using var store = new InMemoryVectorStore();
        await IndexAsync(generator, embeddings, store, units, cancellationToken);
        var (runs, pooledQueries) = await RetrieveAsync(
            row, generator, embeddings, store, dataset.Queries, descriptor, maxPerDocument, cancellationToken);

        return new BeirRunResult(
            IrMetrics.Evaluate(runs, dataset.Qrels, Cutoff),
            dataset.Documents.Count,
            units.Count,
            distinctDocuments,
            maxPerDocument,
            pooledQueries,
            Stopwatch.GetElapsedTime(startedAt),
            embeddings.Hits - hitsBefore,
            embeddings.Misses - missesBefore);
    }

    /// <summary>Counts documents whose <c>title</c> is present and non-empty.</summary>
    /// <remarks>
    /// Asserted because <c>BeirParityTests.DatasetsAndSeparators</c> drops the newline case for a
    /// corpus with no titles, on the grounds that it would measure identical bytes. If a corpus ever
    /// gained titles, that reasoning would stop holding and the case would go on being skipped
    /// silently — which reads, from the test summary, exactly like a case that passed.
    /// </remarks>
    private static int CountTitled(IReadOnlyList<BeirDocument> documents)
    {
        var titled = 0;
        for (var i = 0; i < documents.Count; i++)
        {
            if (!string.IsNullOrEmpty(documents[i].Title))
            {
                titled++;
            }
        }

        return titled;
    }

    /// <summary>
    /// Gets the largest number of units any one document contributed, and how many distinct
    /// documents contributed any at all.
    /// </summary>
    /// <remarks>
    /// Retrieval over-shoots the cutoff by the first, so pooling still sees <c>k</c> distinct
    /// documents in the worst case where the top hits are as concentrated as they can be. It is 1
    /// under the parity protocol, which is why that protocol retrieves exactly the cutoff. The
    /// second exists because a document that contributed nothing is unretrievable and says so
    /// nowhere else.
    /// </remarks>
    private static (int MaxPerDocument, int DistinctDocuments) SummariseUnits(
        IReadOnlyList<TextChunk> units)
    {
        var perDocument = new Dictionary<string, int>(StringComparer.Ordinal);
        var most = 0;

        for (var i = 0; i < units.Count; i++)
        {
            var documentId = units[i].DocumentId.Value;
            perDocument.TryGetValue(documentId, out var count);
            count++;
            perDocument[documentId] = count;
            if (count > most)
            {
                most = count;
            }
        }

        return (most, perDocument.Count);
    }

    /// <summary>Embeds every unit through the cache and stores it.</summary>
    private static async Task IndexAsync(
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        InMemoryVectorStore store,
        IReadOnlyList<TextChunk> units,
        CancellationToken cancellationToken)
    {
        for (var start = 0; start < units.Count; start += SlabSize)
        {
            var end = Math.Min(start + SlabSize, units.Count);
            var texts = new string[end - start];
            for (var i = start; i < end; i++)
            {
                texts[i - start] = units[i].Text;
            }

            var vectors = await EmbedAsync(generator, embeddings, texts, cancellationToken);

            var stored = new EmbeddedChunk[end - start];
            for (var i = start; i < end; i++)
            {
                stored[i - start] = new EmbeddedChunk
                {
                    Chunk = units[i],
                    Embedding = vectors[i - start],
                };
            }

            await store.StoreAsync(stored, cancellationToken);
        }
    }

    /// <summary>
    /// Retrieves for every query with the row and aggregates each result list to a document ranking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every</b> query in <c>queries.jsonl</c>, not only the judged ones. The ranker never sees
    /// qrels — which is what "materially above the band means a leak" is about — and
    /// <see cref="IrMetrics.Evaluate"/> is the single place the exclusion rule is applied, reporting
    /// the unjudged ones as <see cref="IrEvaluation.ExcludedQueryCount"/>.
    /// </para>
    /// <para>
    /// The retrieval itself is the row's; everything downstream of the hit list is not. Pooling,
    /// the self-exclusion and <see cref="BeirRunResult.PooledQueryCount"/> stay here so that every
    /// row's hits are aggregated by the same code — a row that pooled its own way would make the
    /// table compare aggregations rather than retrieval strategies.
    /// </para>
    /// <para>
    /// <c>TopK</c> over-shoots the cutoff by <paramref name="maxUnitsPerDocument"/>, and by one more
    /// cutoff's worth when the dataset excludes the query's own document. Under the parity protocol
    /// that factor is 1 and this is the cutoff exactly, as it always was. Under a chunking protocol
    /// it has to be more, or pooling is handed a list top-k already truncated — the ordering defect
    /// <see cref="DocumentRanking"/> exists to prevent, reintroduced one level up.
    /// </para>
    /// <para>
    /// The exclusion is BEIR's own: <c>DenseRetrievalExactSearch.search</c> pushes a hit only
    /// <c>if corpus_id != query_id</c>, and MTEB exposes it as <c>ignore_identical_ids</c>.
    /// </para>
    /// </remarks>
    private static async Task<(IReadOnlyDictionary<string, IReadOnlyList<string>> Runs, int PooledQueries)>
        RetrieveAsync(
            AblationRow row,
            OnnxEmbeddingGenerator generator,
            EmbeddingCache embeddings,
            InMemoryVectorStore store,
            IReadOnlyList<BeirQuery> queries,
            BeirDatasetDescriptor descriptor,
            int maxUnitsPerDocument,
            CancellationToken cancellationToken)
    {
        var excludesSelf = descriptor.ExcludesSelfRetrievedDocument;
        var runs = new Dictionary<string, IReadOnlyList<string>>(queries.Count, StringComparer.Ordinal);
        var options = new SearchOptions
        {
            TopK = (Cutoff + (excludesSelf ? 1 : 0)) * maxUnitsPerDocument,
        };

        var pooledQueries = 0;
        for (var i = 0; i < queries.Count; i++)
        {
            var hits = await row.RetrieveAsync(
                queries[i], generator, embeddings, store, options, cancellationToken);

            // The same excluded id goes to both, and it has to. DocumentRanking drops the
            // excluded document's chunks BEFORE it pools, so a query whose only repeated
            // document is its own — every ArguAna query whose argument was chunked, for
            // instance — would otherwise be counted as pooled on a ranking that pooling never
            // touched. PooledQueryCount is documented as "queries on which max-pooling had
            // anything to do at all"; measuring it on the pre-exclusion list measures a
            // different sentence.
            var excludedDocumentId = excludesSelf ? queries[i].Id : null;
            if (HasRepeatedDocument(hits, excludedDocumentId))
            {
                pooledQueries++;
            }

            runs[queries[i].Id] = DocumentRanking.TopDocumentIds(
                hits, Cutoff, excludedDocumentId);
        }

        return (runs, pooledQueries);
    }

    /// <summary>Embeds through the cache, so a re-run pays only for texts it has not seen.</summary>
    /// <remarks>
    /// The vectors are stored and returned <b>verbatim</b>.
    /// <see cref="OnnxEmbeddingGenerator"/> already mean-pools excluding padding and L2-normalises,
    /// so they arrive unit-length and <see cref="InMemoryVectorStore"/>'s cosine is a dot product.
    /// Pooling or normalising again here is the regression to watch for: it would not throw, it would
    /// quietly move the number.
    /// </remarks>
    internal static Task<IReadOnlyList<float[]>> EmbedAsync(
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken) =>
        embeddings.GetOrAddAsync(
            texts,
            async (missing, token) =>
            {
                var generated = await generator.GenerateAsync(missing, cancellationToken: token);
                var vectors = new float[generated.Count][];
                for (var i = 0; i < generated.Count; i++)
                {
                    vectors[i] = generated[i].Vector.ToArray();
                }

                return vectors;
            },
            cancellationToken);

    /// <summary>
    /// Reports whether any document that survives the exclusion contributed two or more of these
    /// hits — the condition under which max-pooling does anything at all.
    /// </summary>
    /// <param name="hits">The retrieved chunks, before <see cref="DocumentRanking"/> sees them.</param>
    /// <param name="excludedDocumentId">
    /// The document <see cref="DocumentRanking.TopDocuments"/> will drop, or <see langword="null"/>.
    /// Skipped here for the same reason it is dropped there: chunks that never reach the pool
    /// cannot be evidence that the pool did anything.
    /// </param>
    private static bool HasRepeatedDocument(
        IReadOnlyList<ChunkHit> hits, string? excludedDocumentId)
    {
        var seen = new HashSet<string>(hits.Count, StringComparer.Ordinal);
        for (var i = 0; i < hits.Count; i++)
        {
            var documentId = hits[i].DocumentId;
            if (string.Equals(documentId, excludedDocumentId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!seen.Add(documentId))
            {
                return true;
            }
        }

        return false;
    }
}
