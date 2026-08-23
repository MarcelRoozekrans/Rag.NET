using System.Diagnostics;
using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Raptor;
using Rag.NET.Raptor.Store;
using Rag.NET.Retrieval;
using Rag.NET.Storage;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// One end-to-end pass of <c>Rag.NET.Raptor</c> over a list of MultiHop-RAG articles: ingest, then
/// build the RAPTOR tree — the RAPTOR equivalent of <see cref="GraphRagRun"/>.
/// <para>
/// <b>Under <see cref="RaptorTreeScope.Corpus"/>, ingestion never goes through
/// <see cref="RaptorIngestionBehavior.HandleAsync"/> at all.</b> The obvious fix — leave the
/// behaviour's growth-threshold debounce suppressed via
/// <see cref="RaptorOptions.CorpusGrowthThreshold"/> — does not actually suppress it for a bulk
/// load. The debounce's baseline resets to whatever the corpus held at the last build, so after
/// document 0 (roughly 29 chunks) the next build fires once the corpus reaches roughly 101 x 29
/// &#8776; 2,929 leaves — around article 101 of 609, not "never". Worse, that trigger point is
/// <i>order dependent</i>: MultiHop-RAG has an article with 201 chunks, and if it happens to land
/// first, 101 x 201 already exceeds the whole corpus and no second build ever fires. A measurement
/// harness cannot have a hidden variable of that shape — the tree it measures would depend on
/// document order.
/// </para>
/// <para>
/// So this class does not tune the debounce; it bypasses the path that reads it. Under
/// <see cref="RaptorTreeScope.Corpus"/>, each document's chunks are embedded, written straight to
/// the leaf store (<see cref="Store.IRaptorLeafStore.AddLeavesAsync"/>) and the vector store, and
/// nothing else — <see cref="RaptorIngestionBehavior"/> is never asked to ingest. This loses no
/// fidelity: every summary <see cref="RaptorIngestionBehavior.HandleAsync"/> could have produced
/// mid-ingestion is filed under <see cref="RaptorCorpusDocumentId.Value"/>, and
/// <see cref="RaptorTreeRebuilder.RebuildAsync"/> opens by deleting exactly that id before writing
/// the tree it built — so any such summary would have been discarded before anything measured it.
/// The single call to <see cref="RaptorTreeRebuilder.RebuildAsync"/>, made once ingestion finishes,
/// is the only tree this class ever produces under <see cref="RaptorTreeScope.Corpus"/>, and it is
/// now the only one <i>possible</i> — not merely the only one intended.
/// </para>
/// <para>
/// <b>Under <see cref="RaptorTreeScope.PerDocument"/>, ingestion goes through
/// <see cref="RaptorIngestionBehavior.HandleAsync"/> unchanged.</b> There the ingestion path is
/// what builds the tree — each document's own chunks are clustered into their own tree as that
/// document is ingested — so bypassing it would leave nothing to measure.
/// </para>
/// <para>
/// <b>The embedder is an interface, not the concrete <c>OnnxEmbeddingGenerator</c> the design
/// sketched.</b> <c>OnnxEmbeddingGenerator</c>'s public constructor loads a real ONNX model and
/// vocabulary file from disk, and <see cref="GraphRagRun"/>'s own equivalent is exercised only by
/// tests that already require those files. This class's behaviour needs covering by a test with no
/// model and no corpus — so the parameter is <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>,
/// which the real <c>OnnxEmbeddingGenerator</c> implements just as well as a fake does. Nothing
/// about the shape callers see changes: the real harness still hands this an
/// <c>OnnxEmbeddingGenerator</c> instance.
/// </para>
/// <para>
/// <b>Chunking is shared with the dense arm and with <see cref="GraphRagRun"/>, not reimplemented.</b>
/// <see cref="GraphRagSliceIngestion.ChunkAsync"/> is <see cref="RecursiveChunkingStrategy"/> at
/// stock <see cref="ChunkingOptions"/> over <see cref="BeirDocument.RetrievalText"/> — the same
/// chunker <c>BeirRealChunkingTests</c> feeds the <c>dense</c> arm. The pilot's validation gate
/// (Phase 6.2.1 Task 4) compares <c>raptorfiltered</c> — this run's corpus store with every summary
/// dropped — against <c>dense</c>, and that comparison is meaningless if the two arms cut the
/// corpus differently.
/// </para>
/// </summary>
internal sealed class RaptorRun : IAsyncDisposable
{
    /// <summary>The telemetry source <c>Rag.NET.Raptor</c> writes its summarise spans to.</summary>
    private const string RaptorTelemetrySourceName = "Rag.NET";

    /// <summary>
    /// Names the trace that scopes one build, so concurrent runs cannot cross-record.
    /// </summary>
    /// <remarks>
    /// A <see langword="const"/> rather than <c>BuildScopeSource.Name</c>, and the listener below
    /// compares against it directly. Constructing an <see cref="ActivitySource"/> notifies every
    /// registered listener synchronously, so a <c>ShouldListenTo</c> that reads
    /// <c>BuildScopeSource</c> re-enters this type's static constructor while that very field is
    /// still being assigned — a <c>TypeInitializationException</c> on the first build.
    /// </remarks>
    private const string BuildScopeSourceName = "Rag.NET.Benchmarks.RaptorRun";

    private static readonly ActivitySource BuildScopeSource = new(BuildScopeSourceName);

    private readonly List<LevelShape> _levels = [];

    private ActivityTraceId? _buildTraceId;

    private readonly RaptorTreeScope _scope;
    private readonly CachingEmbedder _embedder;
    private readonly CountingChatClient _summariser;
    private readonly InMemoryVectorStore _store = new();
    private readonly SqliteRaptorLeafStore? _leafStore;
    private readonly RaptorIngestionBehavior _behavior;
    private readonly RaptorTreeRebuilder? _rebuilder;
    private int _corpusRebuildCount;

    private RaptorRun(
        RaptorTreeScope scope,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingCache embeddings,
        IChatClient summariser,
        string leafStorePath)
    {
        _scope = scope;
        _embedder = new CachingEmbedder(generator, embeddings);
        _summariser = new CountingChatClient(summariser);

        // CorpusGrowthThreshold is left at RaptorOptions' shipped default deliberately: under
        // Corpus scope IngestAsync below never calls RaptorIngestionBehavior.HandleAsync, which is
        // the only code path that ever reads it (ShouldBuild), so no value here changes this run's
        // behaviour. Under PerDocument scope it is equally unread — it only governs the Corpus
        // branch of HandleAsync. Setting it would document an intent this class no longer acts on.
        var options = new RaptorOptions { TreeScope = scope };

        _leafStore = scope == RaptorTreeScope.Corpus ? new SqliteRaptorLeafStore(leafStorePath) : null;
        _behavior = new RaptorIngestionBehavior(_summariser, _embedder, options, _leafStore);
        _rebuilder = scope == RaptorTreeScope.Corpus ? new RaptorTreeRebuilder(_behavior, _store) : null;
    }

    /// <summary>
    /// Ingests the corpus and, under <see cref="RaptorTreeScope.Corpus"/>, builds the tree exactly
    /// once at the end.
    /// </summary>
    /// <param name="documents">The corpus's articles, in corpus order.</param>
    /// <param name="scope">
    /// Whether to build one tree over the whole corpus or one tree per document. See
    /// <see cref="RaptorTreeScope"/>.
    /// </param>
    /// <param name="generator">The embedder; the caller disposes it. See the type remarks for why
    /// this is an interface rather than the concrete ONNX generator.</param>
    /// <param name="embeddings">The vector cache every text is embedded through.</param>
    /// <param name="summariser">The chat client RAPTOR's cluster summaries come from.</param>
    /// <param name="leafStorePath">
    /// Where the corpus-scope leaf store is opened — a file path, or <c>:memory:</c>. Unused under
    /// <see cref="RaptorTreeScope.PerDocument"/>, which needs no leaf store.
    /// </param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The finished run, ready to search.</returns>
    /// <remarks>
    /// A failure partway through — a bad document, a cancelled token — must not leave the run's
    /// SQLite leaf store open: a file-backed store left locked would fail every later attempt to
    /// reopen the same path, for a reason invisible at that later call site. So construction happens
    /// outside the <see langword="try"/>, and everything that can throw after it is disposed on the
    /// way out.
    /// </remarks>
    public static async Task<RaptorRun> BuildAsync(
        IReadOnlyList<BeirDocument> documents,
        RaptorTreeScope scope,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingCache embeddings,
        IChatClient summariser,
        string leafStorePath,
        CancellationToken cancellationToken)
    {
        var run = new RaptorRun(scope, generator, embeddings, summariser, leafStorePath);

        // The cluster shape of every level, read off the `ragnet.raptor.summarize` span that
        // already carries it. #345's floor bounds a level's AVERAGE cluster size and explicitly
        // not its maximum, and that design deferred "is the average enough on a real corpus?" to
        // measurement. This is that measurement. The tag has existed since #345 and nothing
        // outside Rag.NET.Raptor.Tests had ever read it, so a corpus run could report that it
        // succeeded while saying nothing about the margin it succeeded by.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                string.Equals(source.Name, RaptorTelemetrySourceName, StringComparison.Ordinal)
                || string.Equals(source.Name, BuildScopeSourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = run.CaptureLevel,
        };
        ActivitySource.AddActivityListener(listener);

        // Scoped by trace, not by operation name: an ActivityListener is process-wide, so two
        // RaptorRuns building at once would otherwise each record the other's levels — and the
        // corpus and per-document arms of one sweep are exactly two such runs.
        using var buildScope = BuildScopeSource.StartActivity("ragnet.benchmark.raptor.build");
        run._buildTraceId = buildScope?.TraceId;

        try
        {
            if (run._leafStore is not null)
            {
                await run._leafStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            await run.IngestAsync(documents, cancellationToken).ConfigureAwait(false);

            if (scope == RaptorTreeScope.Corpus)
            {
                var produced = await run._rebuilder!.RebuildAsync(cancellationToken).ConfigureAwait(false);
                run._corpusRebuildCount = 1;
                run.SummaryCount = produced;
            }

            return run;
        }
        catch
        {
            await run.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Gets how many level-0 (leaf) chunks the corpus was cut into and embedded.</summary>
    /// <remarks>
    /// Counted at ingestion, before RAPTOR appends any summary — the same figure under either
    /// <see cref="RaptorTreeScope"/>, since a tree adds nodes on top of the leaves rather than
    /// replacing them.
    /// </remarks>
    public int LeafCount { get; private set; }

    /// <summary>Gets how many RAPTOR summary chunks the finished tree holds.</summary>
    /// <remarks>
    /// Under <see cref="RaptorTreeScope.Corpus"/> this is the single
    /// <see cref="RaptorTreeRebuilder.RebuildAsync"/> call's return value. Under
    /// <see cref="RaptorTreeScope.PerDocument"/> there is no rebuild, so this accumulates the
    /// summaries each document's own ingestion produced.
    /// </remarks>
    public int SummaryCount { get; private set; }

    /// <summary>
    /// Gets how many times this run called <see cref="RaptorTreeRebuilder.RebuildAsync"/>.
    /// </summary>
    /// <remarks>
    /// Under <see cref="RaptorTreeScope.Corpus"/> this is always <c>1</c>: ingestion writes leaves
    /// directly and never calls <see cref="RaptorIngestionBehavior.HandleAsync"/> (see the type
    /// remarks), so the one explicit rebuild after ingestion is the only tree build this run can
    /// possibly perform. Always <c>0</c> under <see cref="RaptorTreeScope.PerDocument"/>, which
    /// builds during ingestion and never rebuilds.
    /// </remarks>
    public int CorpusRebuildCount => _corpusRebuildCount;

    /// <summary>Gets how many requests <see cref="RaptorIngestionBehavior"/> sent the summariser.</summary>
    public long SummariserCalls => _summariser.Calls;

    /// <summary>
    /// Gets the cluster shape of every tree level this run built, newest level last.
    /// </summary>
    /// <remarks>
    /// Empty when no tree was built. Under <see cref="RaptorTreeScope.Corpus"/> this has one entry
    /// per level of the single rebuild; under <see cref="RaptorTreeScope.PerDocument"/> it
    /// accumulates every document's levels, so <see cref="LevelShape.Level"/> repeats.
    /// </remarks>
    public IReadOnlyList<LevelShape> Levels
    {
        get
        {
            lock (_levels)
            {
                return [.. _levels];
            }
        }
    }

    /// <summary>The cluster shape of one tree level, read off its summarise span.</summary>
    /// <param name="Level">The tree level, 1 for the first level above the leaves.</param>
    /// <param name="ChunkCount">How many chunks entered this level.</param>
    /// <param name="ClusterCount">How many clusters came out.</param>
    /// <param name="MaxClusterSize">The largest cluster's chunk count.</param>
    /// <param name="MaxClustersOverridden">Whether the size floor overrode a configured cap.</param>
    /// <param name="Degenerate">Whether clustering collapsed and the level was not reduced.</param>
    public sealed record LevelShape(
        int Level,
        int ChunkCount,
        int ClusterCount,
        int MaxClusterSize,
        bool MaxClustersOverridden,
        bool Degenerate)
    {
        /// <summary>
        /// Gets how many times larger the biggest cluster is than this level's mean cluster.
        /// </summary>
        /// <remarks>
        /// <c>1.0</c> is a perfectly even split. This is the number #345's design could not
        /// predict: the floor guarantees the mean, so everything above <c>1.0</c> is the
        /// assignment's own imbalance, and a level whose largest cluster runs far ahead of the
        /// mean is the case that would need post-assignment splitting.
        /// </remarks>
        public double Imbalance =>
            ClusterCount <= 0 || ChunkCount <= 0
                ? 0
                : MaxClusterSize / ((double)ChunkCount / ClusterCount);
    }

    /// <summary>Records one level's shape, if the span belongs to this run's build.</summary>
    private void CaptureLevel(Activity stopped)
    {
        if (!string.Equals(stopped.OperationName, "ragnet.raptor.summarize", StringComparison.Ordinal))
        {
            return;
        }

        if (_buildTraceId is { } trace && stopped.TraceId != trace)
        {
            return;
        }

        var shape = new LevelShape(
            ReadIntTag(stopped, "raptor.tree.level") ?? -1,
            ReadIntTag(stopped, "raptor.chunk.count") ?? -1,
            ReadIntTag(stopped, "raptor.cluster.count") ?? 0,
            ReadIntTag(stopped, "raptor.cluster.max.size") ?? 0,
            stopped.GetTagItem("raptor.cluster.maxclusters.overridden") is true,
            stopped.GetTagItem("raptor.cluster.degenerate") is true);

        lock (_levels)
        {
            _levels.Add(shape);
        }
    }

    private static int? ReadIntTag(Activity activity, string name) =>
        activity.GetTagItem(name) as int?;

    /// <summary>Runs RAPTOR retrieval for one query and returns what it found.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="mode">Which <see cref="RaptorRetrievalMode"/> to search under.</param>
    /// <param name="topK">Chunks to return.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The results <see cref="RaptorRetrievalBehavior"/> produced.</returns>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, RaptorRetrievalMode mode, int topK, CancellationToken cancellationToken)
    {
        // CandidateMultiplier and SummaryBoostFactor are pinned here explicitly, at
        // RaptorRetrievalOptions' own shipped defaults (3.0 and 1.2) as of this run, rather than
        // left to fall out of the type's defaults implicitly. raptorboost's pinned figure only
        // means what it says as long as these two values are what it was measured at; a future
        // change to either default would otherwise silently redefine the figure without this run
        // noticing.
        var behavior = new RaptorRetrievalBehavior(new RaptorRetrievalOptions
        {
            Mode = mode,
            CandidateMultiplier = 3.0,
            SummaryBoostFactor = 1.2,
        });
        var context = new RetrievalContext
        {
            Query = query,
            Options = new RetrievalOptions { TopK = topK },
        };

        return await behavior.HandleAsync(context, cancellationToken, RetrieveAsync).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        _embedder.Dispose();
        _summariser.Dispose();

        if (_leafStore is not null)
        {
            await _leafStore.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Chunks and embeds every article, then indexes it: directly, under
    /// <see cref="RaptorTreeScope.Corpus"/> (see the type remarks for why), or through
    /// <see cref="RaptorIngestionBehavior"/> under <see cref="RaptorTreeScope.PerDocument"/>, which
    /// is what builds that scope's per-document trees.
    /// </summary>
    private async Task IngestAsync(IReadOnlyList<BeirDocument> documents, CancellationToken cancellationToken)
    {
        for (var i = 0; i < documents.Count; i++)
        {
            var chunks = await GraphRagSliceIngestion.ChunkAsync(documents[i], cancellationToken)
                .ConfigureAwait(false);
            if (chunks.Count == 0)
            {
                continue;
            }

            var texts = new string[chunks.Count];
            for (var j = 0; j < chunks.Count; j++)
            {
                texts[j] = chunks[j].Text;
            }

            var vectors = await _embedder.GenerateAsync(texts, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var embedded = new EmbeddedChunk[chunks.Count];
            for (var j = 0; j < chunks.Count; j++)
            {
                embedded[j] = new EmbeddedChunk { Chunk = chunks[j], Embedding = vectors[j].Vector };
            }

            LeafCount += chunks.Count;

            if (_scope == RaptorTreeScope.Corpus)
            {
                await IngestLeavesDirectlyAsync(embedded, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await IngestThroughBehaviorAsync(documents[i].Id, embedded, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The <see cref="RaptorTreeScope.Corpus"/> path: writes this document's chunks to the leaf
    /// store and the vector store, and nothing else. See the type remarks for why going through
    /// <see cref="RaptorIngestionBehavior.HandleAsync"/> instead would be both wasteful and
    /// order-dependent, and why skipping it loses no fidelity.
    /// </summary>
    private async Task IngestLeavesDirectlyAsync(
        IReadOnlyList<EmbeddedChunk> embedded, CancellationToken cancellationToken)
    {
        var leaves = new RaptorLeaf[embedded.Count];
        for (var i = 0; i < embedded.Count; i++)
        {
            leaves[i] = new RaptorLeaf(
                embedded[i].Chunk.DocumentId.Value,
                embedded[i].Chunk.ChunkIndex,
                embedded[i].Chunk.Text,
                embedded[i].Embedding.ToArray());
        }

        await _leafStore!.AddLeavesAsync(leaves, cancellationToken).ConfigureAwait(false);
        await _store.StoreAsync(embedded, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <see cref="RaptorTreeScope.PerDocument"/> path: runs this document's chunks through
    /// <see cref="RaptorIngestionBehavior"/>, which is what builds that scope's tree, then indexes
    /// whatever it produced (leaves and, once there are enough of them, that document's own
    /// summaries).
    /// </summary>
    private async Task IngestThroughBehaviorAsync(
        string documentId, IReadOnlyList<EmbeddedChunk> embedded, CancellationToken cancellationToken)
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId(documentId), FileName = documentId },
            GetNextBm25DocId = static () => 0,
        };
        ctx.EmbeddedChunks.AddRange(embedded);

        _ = await _behavior.HandleAsync(ctx, cancellationToken, static (c, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }))
            .ConfigureAwait(false);

        SummaryCount += ctx.EmbeddedChunks.Count - embedded.Count;

        await _store.StoreAsync(ctx.EmbeddedChunks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The dense retrieval RAPTOR's retrieval behavior sits in front of.</summary>
    private async ValueTask<IReadOnlyList<SearchResult>> RetrieveAsync(
        RetrievalContext context, CancellationToken cancellationToken)
    {
        var vectors = await _embedder.GenerateAsync([context.Query], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return await _store.SearchAsync(
            vectors[0].Vector,
            new SearchOptions { TopK = context.Options.TopK, MetadataFilter = context.Options.MetadataFilter },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> behind <see cref="EmbeddingCache"/>, in
    /// the shape RAPTOR's behaviors take an embedder in — the same adapter role
    /// <see cref="CachingEmbeddingGenerator"/> plays for <see cref="GraphRagRun"/>, generalised to
    /// an interface so a test can hand it a fake.
    /// </summary>
    private sealed class CachingEmbedder(
        IEmbeddingGenerator<string, Embedding<float>> inner, EmbeddingCache cache)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(values);

            var texts = new List<string>(values);
            var vectors = await cache.GetOrAddAsync(
                texts,
                async (missing, token) =>
                {
                    var generated = await inner.GenerateAsync(missing, cancellationToken: token)
                        .ConfigureAwait(false);
                    var result = new float[generated.Count][];
                    for (var i = 0; i < generated.Count; i++)
                    {
                        result[i] = generated[i].Vector.ToArray();
                    }

                    return result;
                },
                cancellationToken).ConfigureAwait(false);

            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            for (var i = 0; i < vectors.Count; i++)
            {
                embeddings.Add(new Embedding<float>(vectors[i]));
            }

            return embeddings;
        }

        /// <inheritdoc/>
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // The inner generator is owned by the caller.
        }
    }

    /// <summary>
    /// <see cref="IChatClient"/> that counts how many requests pass through it — the source of
    /// <see cref="SummariserCalls"/>.
    /// </summary>
    private sealed class CountingChatClient(IChatClient inner) : IChatClient
    {
        private long _calls;

        /// <summary>Gets how many requests have been answered.</summary>
        public long Calls => Interlocked.Read(ref _calls);

        /// <inheritdoc/>
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.GetResponseAsync(messages, options, cancellationToken);
        }

        /// <inheritdoc/>
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        /// <inheritdoc/>
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // The inner chat client is owned by the caller.
        }
    }
}
