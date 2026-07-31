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
/// The measurement Phase 3.7 exists for, generalised: every dataset in
/// <see cref="BeirDatasetDescriptor.All"/> scored for nDCG@10 through Rag.NET's real embed → store →
/// retrieve path, and checked against the published figure that dataset carries in its
/// <see cref="BeirDatasetDescriptor.ParityTarget"/>.
/// <para>
/// Every component is the library's own — <see cref="OnnxEmbeddingGenerator"/> embeds,
/// <see cref="InMemoryVectorStore"/> stores and scores cosine, <see cref="DocumentRanking"/>
/// aggregates and <see cref="IrMetrics"/> scores. Nothing here is a benchmark-only reimplementation,
/// which is the point: a harness built out of purpose-made parts measures the harness.
/// </para>
/// <para>
/// <b>One test over the datasets, not one file per dataset.</b> The target and the band come off the
/// descriptor, so a second dataset is a second descriptor rather than a copy of this file with three
/// constants edited. Copies are how a band ends up widened to fit a number that should have been
/// investigated instead.
/// </para>
/// <para>
/// Skipped unless <c>RAGNET_ONNX_EMBED_MODEL</c>, <c>RAGNET_ONNX_EMBED_VOCAB</c> and
/// <c>RAGNET_BEIR_CACHE</c> are all usable. This project declares
/// <c>&lt;RequiresSecrets&gt;true&lt;/RequiresSecrets&gt;</c> so nightly.yml selects it and supplies
/// them; it is a project of its own so that declaration does not drag
/// <c>Rag.NET.Benchmarks.Quality.Tests</c>' unit tests out of the gating tier with it.
/// </para>
/// </summary>
public sealed class BeirParityTests
{
    /// <summary>The rank cutoff the published figures are quoted at.</summary>
    private const int Cutoff = 10;

    /// <summary>
    /// Documents embedded per <see cref="OnnxEmbeddingGenerator.GenerateAsync"/> call. Only a
    /// working-set bound — the generator does its own padded batching underneath, and its pooling
    /// excludes padding, so no slab or batch size can change a document's vector.
    /// </summary>
    private const int SlabSize = 512;

    private const string SkipReason =
        "Set RAGNET_ONNX_EMBED_MODEL and RAGNET_ONNX_EMBED_VOCAB to an existing all-MiniLM-L6-v2 " +
        "ONNX export (token-level output) and its WordPiece vocab.txt, and RAGNET_BEIR_CACHE to a " +
        "writable directory for the dataset downloads, to run the BEIR parity measurements.";

    private readonly ITestOutputHelper _output;

    public BeirParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Gets every described dataset crossed with both title/text separators.
    /// </summary>
    /// <returns>Dataset name and separator pairs.</returns>
    /// <remarks>
    /// <para>
    /// Names rather than descriptors, because theory data that is not serializable costs the run its
    /// per-case test names — and a parity failure that cannot say which dataset failed is most of the
    /// way to useless.
    /// </para>
    /// <para>
    /// <b>Both separators only where the corpus has titles.</b> The separator sits between title and
    /// text, so on a corpus with no titles <c>title + sep + text</c> trims back to identical bytes and
    /// the second case measures the first again. FiQA titles none of its 57,638 documents, and one
    /// FiQA measurement is roughly an order of magnitude more expensive than SciFact's ~355 s — an
    /// hour spent re-deriving a number that is equal by construction. The count comes off the
    /// descriptor and <see cref="LoadAsync"/> asserts it against the archive, so a dataset that
    /// quietly gained titles fails loudly rather than silently losing a case.
    /// </para>
    /// </remarks>
    public static TheoryData<string, string> DatasetsAndSeparators()
    {
        var data = new TheoryData<string, string>();
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            data.Add(descriptor.Name, " ");
            if (descriptor.TitledDocumentCount > 0)
            {
                data.Add(descriptor.Name, "\n");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DatasetsAndSeparators))]
    public async Task NdcgAt10_ThroughTheRealPipeline_LandsWithinToleranceOfPublished(
        string datasetName, string sep)
    {
        var modelPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_MODEL");
        var vocabPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_VOCAB");
        var cacheDirectory = BeirDatasetCache.ResolveCacheDirectoryFromEnvironment();

        Assert.SkipWhen(
            string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath) ||
            string.IsNullOrEmpty(vocabPath) || !File.Exists(vocabPath) ||
            string.IsNullOrEmpty(cacheDirectory),
            SkipReason);

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var ct = TestContext.Current.CancellationToken;
        var startedAt = Stopwatch.GetTimestamp();

        // The separator is passed explicitly, not left to the default, because it decides what is
        // embedded: BEIR's SentenceBERT joins title and text with a single space and the published
        // figures were produced that way. A later change to the default must not move these numbers
        // without someone editing this line.
        var evaluation = await MeasureAsync(descriptor, modelPath!, vocabPath!, cacheDirectory!, sep, ct);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        _output.WriteLine("SEPARATOR=" + (string.Equals(sep, " ", StringComparison.Ordinal) ? "SPACE" : "NEWLINE"));
        _output.WriteLine(Describe(descriptor, evaluation, elapsed));

        var ndcg = evaluation.NormalizedDiscountedCumulativeGain;
        Assert.True(descriptor.ParityTarget.Contains(ndcg), FailureMessage(descriptor, evaluation, elapsed));
    }

    /// <summary>
    /// Loads the dataset, embeds and indexes its corpus, retrieves for every query, and scores the
    /// run.
    /// </summary>
    private static async Task<IrEvaluation> MeasureAsync(
        BeirDatasetDescriptor descriptor,
        string modelPath,
        string vocabPath,
        string cacheDirectory,
        string titleTextSeparator,
        CancellationToken cancellationToken)
    {
        var dataset = await LoadAsync(descriptor, cacheDirectory, titleTextSeparator, cancellationToken);

        // MaxTokens is deliberately left at its default of 256 — all-MiniLM-L6-v2's max_seq_length,
        // and the configuration the published figures were produced under. Raising it would measure
        // something else. ModelId is set because a bare "model.onnx" would otherwise become every
        // model's identity.
        using var generator = new OnnxEmbeddingGenerator(new OnnxEmbeddingOptions
        {
            ModelPath = modelPath,
            TokenizerVocabPath = vocabPath,
            ModelId = "all-MiniLM-L6-v2",
        });

        using var store = new InMemoryVectorStore();
        await IndexCorpusAsync(generator, store, dataset.Documents, cancellationToken);
        var runs = await RetrieveAsync(generator, store, dataset.Queries, descriptor, cancellationToken);

        return IrMetrics.Evaluate(runs, dataset.Qrels, Cutoff);
    }

    /// <summary>
    /// Downloads the dataset if it is not cached, loads it, and checks it is the whole dataset before
    /// anything is scored against it.
    /// </summary>
    /// <remarks>
    /// The three assertions are cheap and they are the difference between a diagnosable failure and
    /// an undiagnosable one: a short corpus or a half-loaded qrels split produces a bad number that
    /// looks exactly like a retrieval defect, and this is the last point where the real cause is
    /// still visible. They come off the descriptor, so every dataset is checked the way SciFact was.
    /// </remarks>
    private static async Task<BeirDataset> LoadAsync(
        BeirDatasetDescriptor descriptor,
        string cacheDirectory,
        string titleTextSeparator,
        CancellationToken cancellationToken)
    {
        var cache = new BeirDatasetCache(cacheDirectory);
        var datasetDirectory = await cache.EnsureAsync(descriptor, cancellationToken);
        var dataset = BeirLoader.Load(datasetDirectory, "test", titleTextSeparator);

        Assert.Equal(descriptor.DocumentCount, dataset.Documents.Count);
        Assert.Equal(descriptor.QueryCount, dataset.Queries.Count);
        Assert.Equal(descriptor.TestQueryCount, dataset.JudgedQueryCount);
        Assert.Equal(descriptor.TitledDocumentCount, CountTitled(dataset.Documents));

        return dataset;
    }

    /// <summary>Counts documents whose <c>title</c> is present and non-empty.</summary>
    /// <remarks>
    /// Asserted because <see cref="DatasetsAndSeparators"/> drops the newline case for a corpus with
    /// no titles, on the grounds that it would measure identical bytes. If a corpus ever gained
    /// titles, that reasoning would stop holding and the case would go on being skipped silently —
    /// which reads from the test summary exactly like a case that passed.
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
    /// Embeds every corpus document and stores it as a single chunk.
    /// </summary>
    /// <remarks>
    /// One chunk per document, deliberately. BEIR's published figures embed each corpus entry as one
    /// sequence truncated at the model's <c>max_seq_length</c>, which is exactly what
    /// <see cref="OnnxEmbeddingGenerator"/> does at <c>MaxTokens = 256</c>. Chunking here would
    /// measure a configuration the published figures did not come from. It also makes
    /// <see cref="DocumentRanking"/>'s max-pooling a no-op on a single-chunk dataset — which is why
    /// the pool-before-cut order is pinned by its own fixture rather than by these numbers.
    /// </remarks>
    private static async Task IndexCorpusAsync(
        OnnxEmbeddingGenerator generator,
        InMemoryVectorStore store,
        IReadOnlyList<BeirDocument> documents,
        CancellationToken cancellationToken)
    {
        for (var start = 0; start < documents.Count; start += SlabSize)
        {
            var end = Math.Min(start + SlabSize, documents.Count);
            var texts = new string[end - start];
            for (var i = start; i < end; i++)
            {
                texts[i - start] = documents[i].RetrievalText;
            }

            var embeddings = await generator.GenerateAsync(texts, cancellationToken: cancellationToken);

            var chunks = new EmbeddedChunk[end - start];
            for (var i = start; i < end; i++)
            {
                chunks[i - start] = ToEmbeddedChunk(documents[i], embeddings[i - start]);
            }

            await store.StoreAsync(chunks, cancellationToken);
        }
    }

    /// <summary>
    /// Retrieves the top <see cref="Cutoff"/> chunks for every query and aggregates them to a
    /// document ranking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every</b> query in <c>queries.jsonl</c>, not only the judged ones. The ranker never sees
    /// qrels — which is what "materially above the band means a leak" is about — and
    /// <see cref="IrMetrics.Evaluate"/> is the single place the exclusion rule is applied, reporting
    /// the unjudged ones as <see cref="IrEvaluation.ExcludedQueryCount"/>.
    /// </para>
    /// <para>
    /// <c>TopK</c> equals the cutoff, plus one when the dataset excludes the query's own document —
    /// which is what BEIR does too, its <c>torch.topk</c> taking <c>min(top_k + 1, …)</c> before the
    /// same filter. Indexing stores one chunk per document, so hits are already distinct documents;
    /// without the spare, an excluded self-hit would leave nine. It is <b>not</b> added
    /// unconditionally, because a wider retrieval could reorder the tenth and eleventh documents
    /// under tie-breaking and SciFact's 0.64593 is this phase's regression gate. A harness that
    /// chunked would have to over-retrieve by much more, or pooling would be handed a list top-k had
    /// already truncated.
    /// </para>
    /// <para>
    /// <b>The exclusion is BEIR's own.</b> <c>DenseRetrievalExactSearch.search</c> pushes a hit only
    /// <c>if corpus_id != query_id</c>, and MTEB exposes the same thing as
    /// <c>ignore_identical_ids</c>, set for ArguAna and FiQA. It is a no-op on SciFact, whose query
    /// ids and corpus ids do not intersect at all, which is why the harness got this far without it.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RetrieveAsync(
        OnnxEmbeddingGenerator generator,
        InMemoryVectorStore store,
        IReadOnlyList<BeirQuery> queries,
        BeirDatasetDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var excludesSelf = descriptor.ExcludesSelfRetrievedDocument;
        var runs = new Dictionary<string, IReadOnlyList<string>>(queries.Count, StringComparer.Ordinal);
        var options = new SearchOptions { TopK = excludesSelf ? Cutoff + 1 : Cutoff };

        for (var start = 0; start < queries.Count; start += SlabSize)
        {
            var end = Math.Min(start + SlabSize, queries.Count);
            var texts = new string[end - start];
            for (var i = start; i < end; i++)
            {
                texts[i - start] = queries[i].Text;
            }

            var embeddings = await generator.GenerateAsync(texts, cancellationToken: cancellationToken);
            for (var i = start; i < end; i++)
            {
                var results = await store
                    .SearchAsync(embeddings[i - start].Vector, options, cancellationToken);
                runs[queries[i].Id] = DocumentRanking.TopDocumentIds(
                    ToChunkHits(results), Cutoff, excludesSelf ? queries[i].Id : null);
            }
        }

        return runs;
    }

    /// <summary>
    /// Wraps one document and its vector for storage.
    /// </summary>
    /// <remarks>
    /// The vector is stored <b>verbatim</b>. <see cref="OnnxEmbeddingGenerator"/> already mean-pools
    /// excluding padding and L2-normalises, so it arrives unit-length and
    /// <see cref="InMemoryVectorStore"/>'s cosine is a dot product. Pooling or normalising again
    /// here is the regression to watch for: it would not throw, it would quietly move the number.
    /// </remarks>
    private static EmbeddedChunk ToEmbeddedChunk(BeirDocument document, Embedding<float> embedding) =>
        new()
        {
            Chunk = new TextChunk
            {
                Text = document.RetrievalText,
                DocumentId = new DocumentId(document.Id),
                ChunkIndex = 0,
            },
            Embedding = embedding.Vector,
        };

    /// <summary>
    /// Projects search results onto <see cref="ChunkHit"/>, carrying the <b>parent document</b> id.
    /// </summary>
    /// <remarks>
    /// A chunk id reaching <see cref="IrMetrics"/> does not throw — it simply never matches a qrels
    /// entry and scores every query zero, so the mapping is made explicitly here rather than assumed.
    /// </remarks>
    private static IReadOnlyList<ChunkHit> ToChunkHits(IReadOnlyList<SearchResult> results)
    {
        var hits = new ChunkHit[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var chunk = results[i].Chunk;
            hits[i] = new ChunkHit(
                FormattableString.Invariant($"{chunk.DocumentId.Value}#{chunk.ChunkIndex}"),
                chunk.DocumentId.Value,
                results[i].Score);
        }

        return hits;
    }

    /// <summary>
    /// The line the run prints. It leads with the dataset name because the theory now produces one of
    /// these per dataset.
    /// </summary>
    private static string Describe(
        BeirDatasetDescriptor descriptor, IrEvaluation evaluation, TimeSpan elapsed)
    {
        var target = descriptor.ParityTarget;

        return FormattableString.Invariant($"""
            {descriptor.Name} nDCG@{evaluation.Cutoff} = {evaluation.NormalizedDiscountedCumulativeGain:F5} (published ≈ {target.PublishedNdcgAt10:F3}, band {target.LowerBound:F3}–{target.UpperBound:F3})
            Recall@{evaluation.Cutoff} = {evaluation.Recall:F5}, MRR@{evaluation.Cutoff} = {evaluation.MeanReciprocalRank:F5}
            {evaluation.EvaluatedQueryCount} queries evaluated, {evaluation.ExcludedQueryCount} excluded as unjudged
            elapsed {elapsed.TotalSeconds:F1} s
            """);
    }

    /// <summary>
    /// The message a red run leaves behind. It names the computed value first, because a parity
    /// failure that only says "false is not true" tells whoever sees it nothing at all.
    /// </summary>
    private static string FailureMessage(
        BeirDatasetDescriptor descriptor, IrEvaluation evaluation, TimeSpan elapsed)
    {
        var target = descriptor.ParityTarget;

        return FormattableString.Invariant($"""
            {descriptor.Name} parity FAILED. Measured nDCG@{evaluation.Cutoff} = {evaluation.NormalizedDiscountedCumulativeGain:F5}, outside {target.LowerBound:F3}–{target.UpperBound:F3} (published ≈ {target.PublishedNdcgAt10:F3}).
            The published figure is recorded as: {target.Source}
            {Diagnose(descriptor, evaluation)}
            {Describe(descriptor, evaluation, elapsed)}
            Do NOT widen the band to fit the number: the band is ±{target.Tolerance:F2} because the defects it
            exists to catch move a dataset by considerably more than that.
            """);
    }

    /// <summary>
    /// Names the likely cause from the direction the measurement missed in. The two directions have
    /// nothing in common, which is why the band is two-sided.
    /// </summary>
    private static string Diagnose(BeirDatasetDescriptor descriptor, IrEvaluation evaluation)
    {
        if (evaluation.NormalizedDiscountedCumulativeGain >= descriptor.ParityTarget.LowerBound)
        {
            return "ABOVE the band, which is not good news: it indicates a leak, most likely qrels " +
                "reaching the ranker. Nothing in this harness should be able to score better than " +
                "the model's own published figure.";
        }

        return FormattableString.Invariant($"""
            BELOW the band: retrieval or aggregation is wrong. Look at the chunk-to-document step, at
            whether the vectors were pooled or normalised twice, at the title/text separator, and at
            whether the whole {descriptor.DocumentCount}-document corpus was indexed.
            """);
    }
}
