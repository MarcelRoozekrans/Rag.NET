using System.Diagnostics;
using System.Globalization;
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
    /// <c>RAGNET_ONNX_RERANK_MODEL</c>/<c>RAGNET_ONNX_RERANK_VOCAB</c> — but only the embedder is
    /// provisioned by <c>nightly.yml</c>. The reranker is provisioned by the fenced local
    /// procedure in <c>docs/reference/ci.md</c> (same pinned revision and SHA-256 checks the
    /// nightly once ran): every reader of these variables sits behind the opt-in
    /// <c>RAGNET_BEIR_LONG_RUNS</c> gate, so the nightly's download fed nothing it runs, and
    /// Phase 4.1 removed it.
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

    /// <summary>The environment variable naming which repeat run this invocation is.</summary>
    public const string RunIndexVariable = "RAGNET_BEIR_RUN_INDEX";

    /// <summary>
    /// Which repeat run this invocation writes, 1-based; <c>1</c> unless
    /// <see cref="RunIndexVariable"/> says otherwise.
    /// <para>
    /// <b>Without this the .NET rows could not be reproducibility-checked at all.</b> No cost
    /// figure may be published from a single run — <see cref="CostReproducibility"/> compares
    /// repeats — but a test that always wrote run 1 would overwrite its own previous sidecar, so
    /// the gate would have had nothing to compare and would have applied to the Python rows only.
    /// A guard that covers half the data reads as a guard; this repository has shipped that shape
    /// before, so the variable exists to make the .NET side feedable rather than exempt.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The variable is set to something that is not a positive integer. Falling back to 1 would
    /// silently overwrite run 1 with what the operator meant to be run 2, and the gate would then
    /// compare a run against itself and report perfect reproducibility.
    /// </exception>
    public static int RunIndex
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(RunIndexVariable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 1;
            }

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || index < 1)
            {
                throw new InvalidOperationException(
                    $"{RunIndexVariable} is '{raw}', which is not a positive integer. Defaulting " +
                    "to 1 would overwrite the previous run's sidecar, and CostReproducibility " +
                    "would then compare a run against itself and report perfect agreement.");
            }

            return index;
        }
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
    /// Embeds and indexes <paramref name="units"/>, retrieves for every <b>judged</b> query with
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
        var (runs, pooledQueries, _) = await RetrieveAsync(
            row, generator, embeddings, store, JudgedQueries(dataset), descriptor, maxPerDocument,
            cancellationToken);

        return new BeirRunResult(
            IrMetrics.Evaluate(ProjectDocumentIds(runs), dataset.Qrels, Cutoff),
            dataset.Documents.Count,
            units.Count,
            distinctDocuments,
            maxPerDocument,
            pooledQueries,
            Stopwatch.GetElapsedTime(startedAt),
            embeddings.Hits - hitsBefore,
            embeddings.Misses - missesBefore);
    }

    /// <summary>
    /// Embeds and indexes <paramref name="units"/>, retrieves for every judged query with
    /// <paramref name="row"/>, and returns the <b>scored</b> document rankings instead of scoring
    /// them — the shape <see cref="TrecRunFile.Write"/> takes — with the entrant's self-measured
    /// timings: indexing wall-clock around <see cref="IndexAsync"/> only, one latency per query
    /// around the row's retrieval only.
    /// </summary>
    /// <param name="descriptor">The dataset, for its retrieval protocol.</param>
    /// <param name="dataset">The loaded corpus, queries and qrels.</param>
    /// <param name="units">What to index.</param>
    /// <param name="row">How to retrieve.</param>
    /// <param name="generator">The embedder.</param>
    /// <param name="embeddings">The vector cache; every embed call goes through it.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The rankings with the timings measured while producing them.</returns>
    /// <remarks>
    /// Exists for the library comparison's control row (<see cref="BeirComparisonControlTests"/>),
    /// whose metric must come from a run file read back from disk rather than from these rankings
    /// — so <see cref="IrMetrics"/> is deliberately not called here. The retrieval is
    /// <see cref="MeasureAsync"/>'s own: both go through the one <see cref="RetrieveAsync"/>, so
    /// the rankings this returns are the rankings the parity figures were measured on,
    /// self-exclusion and tie-breaking included. A control retrieving through a second copy of the
    /// path would measure the copy.
    /// <para>
    /// The indexing span brackets embed-through-cache and store <b>only</b> — the same operations
    /// the Python harness brackets as <c>entrant.build</c>. Dataset loading and unit preparation
    /// stay outside; under the parity protocol unit preparation is a projection that allocates one
    /// <see cref="TextChunk"/> per document and nothing else.
    /// </para>
    /// <para>
    /// <b>Every vector the run will need is prefetched into memory before either span starts</b>
    /// (<see cref="EmbeddingCache.Prefetch"/>) — unit texts and judged query texts both, since
    /// query embedding also goes through the cache, inside the retrieval span. The indexing figure
    /// is therefore <b>not "the cost of indexing"</b>: it is the library building an index from
    /// vectors it already has, with embedding and its disk I/O excluded by construction. With one
    /// cache-file read per text inside the span, identical runs differed by up to 23x on OS
    /// page-cache state alone — a figure about run order, not about any entrant. A cold cache
    /// fails loudly in the prefetch instead of being silently paid for here.
    /// </para>
    /// </remarks>
    public static async Task<TimedScoredRuns> RetrieveScoredRunsAsync(
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

        var (maxPerDocument, _) = SummariseUnits(units);
        var judged = JudgedQueries(dataset);
        PrefetchEveryVectorTheRunWillNeed(embeddings, units, judged);

        using var store = new InMemoryVectorStore();
        var indexingStartedAt = Stopwatch.GetTimestamp();
        await IndexAsync(generator, embeddings, store, units, cancellationToken);
        var indexingSeconds = Stopwatch.GetElapsedTime(indexingStartedAt).TotalSeconds;
        var (runs, _, latencies) = await RetrieveAsync(
            row, generator, embeddings, store, judged, descriptor, maxPerDocument,
            cancellationToken);

        return new TimedScoredRuns(runs, indexingSeconds, latencies, units.Count, maxPerDocument);
    }

    /// <summary>
    /// Reads every vector the run will need — one per unit text and one per judged query text —
    /// from disk into the cache's memory, before any timed span starts. All the run's cache I/O
    /// happens here; the spans then measure the entrant with embedding already paid for, and a
    /// cold cache fails loudly in <see cref="EmbeddingCache.Prefetch"/> rather than being timed.
    /// </summary>
    private static void PrefetchEveryVectorTheRunWillNeed(
        EmbeddingCache embeddings, IReadOnlyList<TextChunk> units, IReadOnlyList<BeirQuery> queries)
    {
        var texts = new string[units.Count + queries.Count];
        for (var i = 0; i < units.Count; i++)
        {
            texts[i] = units[i].Text;
        }

        for (var i = 0; i < queries.Count; i++)
        {
            texts[units.Count + i] = queries[i].Text;
        }

        embeddings.Prefetch(texts);
    }

    /// <summary>
    /// The queries a run retrieves for: exactly those <c>qrels</c> judges, in <c>queries.jsonl</c>
    /// order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unjudged queries are excluded because they cannot be scored, not as an optimisation.</b>
    /// <see cref="IrMetrics.Evaluate"/> iterates the qrels — an unjudged query's ranking never
    /// enters any mean no matter what is retrieved for it — so retrieving for the 809 of SciFact's
    /// 1,109 queries the test split does not judge computed rankings that were thrown away, and it
    /// billed every per-query resource for them: wall clock, query embeddings, and one cached
    /// hypothetical per hypothesis on the HyDE row, whose refuse-on-miss cache rightly failed on
    /// queries the generation tool never paid for. The metrics are unchanged by construction;
    /// the run's counters (<see cref="BeirRunResult.PooledQueryCount"/>, cache traffic, elapsed)
    /// now describe the judged set only.
    /// </para>
    /// <para>
    /// This is not qrels reaching the ranker. Only <i>membership</i> of the judged set is read
    /// here — which documents are relevant, and how relevant, stays invisible to retrieval, so the
    /// leak the parity band's upper edge watches for cannot enter through this filter.
    /// </para>
    /// <para>
    /// Internal rather than private since Phase 3.14 Task 4: the Semantic Kernel entrant retrieves
    /// through its own library's search path, but <i>which queries a run retrieves for</i> is the
    /// harness's protocol, and a comparator that filtered its own way could measure a different
    /// query set than the control.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<BeirQuery> JudgedQueries(BeirDataset dataset)
    {
        var judged = new List<BeirQuery>(dataset.JudgedQueryCount);
        for (var i = 0; i < dataset.Queries.Count; i++)
        {
            if (dataset.Qrels.ContainsKey(dataset.Queries[i].Id))
            {
                judged.Add(dataset.Queries[i]);
            }
        }

        return judged;
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

    /// <summary>
    /// Embeds every unit through the cache and stores it. Under
    /// <see cref="RetrieveScoredRunsAsync"/> the cache has been prefetched, so "embeds" resolves
    /// to an in-memory lookup and the timed span around this call holds no disk I/O; under
    /// <see cref="MeasureAsync"/> the cache reads disk and embeds misses as it always did.
    /// </summary>
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
    /// Retrieves for each of <paramref name="queries"/> with the row and aggregates each result
    /// list to a document ranking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The judged queries only — <see cref="JudgedQueries"/> says why, and why that is not qrels
    /// reaching the ranker. <see cref="IrMetrics.Evaluate"/> remains the single place the scoring
    /// exclusion rule is applied: a judged query with no positive judgement is still excluded
    /// there, reported as <see cref="IrEvaluation.ExcludedQueryCount"/>.
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
    /// <para>
    /// Each query's latency spans <b>the row's retrieval only</b> — embed-through-cache and
    /// search, the operations the Python harness brackets as its entrants' <c>retrieve</c> call.
    /// The pooled-query bookkeeping and <see cref="DocumentRanking.TopDocuments"/> stay outside
    /// the span: pooling is harness protocol, deliberately identical across entrants. Under the
    /// comparison rows the query embedding resolves from the prefetched memory map, so the span
    /// holds the row's search with no disk read to inherit the OS page cache's state.
    /// </para>
    /// </remarks>
    private static async Task<(IReadOnlyDictionary<string, IReadOnlyList<ScoredDocument>> Runs,
        int PooledQueries, IReadOnlyDictionary<string, double> QueryLatenciesMilliseconds)>
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
        var runs = new Dictionary<string, IReadOnlyList<ScoredDocument>>(
            queries.Count, StringComparer.Ordinal);
        var latencies = new Dictionary<string, double>(queries.Count, StringComparer.Ordinal);
        var options = new SearchOptions
        {
            TopK = (Cutoff + (excludesSelf ? 1 : 0)) * maxUnitsPerDocument,
        };

        var pooledQueries = 0;
        for (var i = 0; i < queries.Count; i++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            var hits = await row.RetrieveAsync(
                queries[i], generator, embeddings, store, options, cancellationToken);
            latencies[queries[i].Id] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

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

            runs[queries[i].Id] = DocumentRanking.TopDocuments(
                hits, Cutoff, excludedDocumentId);
        }

        return (runs, pooledQueries, latencies);
    }

    /// <summary>
    /// Projects scored rankings to bare document ids — the shape
    /// <see cref="IrMetrics.Evaluate"/> consumes. The order is untouched, so this is exactly what
    /// <see cref="DocumentRanking.TopDocumentIds"/> would have produced from the same hits.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectDocumentIds(
        IReadOnlyDictionary<string, IReadOnlyList<ScoredDocument>> runs)
    {
        var documentIds = new Dictionary<string, IReadOnlyList<string>>(
            runs.Count, StringComparer.Ordinal);
        foreach (var (queryId, documents) in runs)
        {
            var ids = new string[documents.Count];
            for (var i = 0; i < documents.Count; i++)
            {
                ids[i] = documents[i].DocumentId;
            }

            documentIds[queryId] = ids;
        }

        return documentIds;
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
