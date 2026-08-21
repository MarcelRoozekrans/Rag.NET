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
/// <b>The critical behaviour is not ingesting normally.</b> At the shipped
/// <see cref="RaptorOptions.CorpusGrowthThreshold"/> of 0.10, ingesting 609 articles triggers 48
/// whole-corpus rebuilds, each re-clustering every leaf so far and summarising it. This class
/// suppresses that debounce during ingestion (<see cref="CorpusGrowthThreshold"/>) and instead
/// calls <see cref="RaptorTreeRebuilder.RebuildAsync"/> exactly once, after every document is in.
/// </para>
/// <para>
/// <b>The embedder is an interface, not the concrete <c>OnnxEmbeddingGenerator</c> the design
/// sketched.</b> <c>OnnxEmbeddingGenerator</c>'s public constructor loads a real ONNX model and
/// vocabulary file from disk, and <see cref="GraphRagRun"/>'s own equivalent is exercised only by
/// tests that already require those files. This class's ingestion behaviour needs the debounce
/// covered by a fast-tier test with no model and no corpus — so the parameter is
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, which the real <c>OnnxEmbeddingGenerator</c>
/// implements just as well as a fake does. Nothing about the shape callers see changes: the real
/// harness still hands this an <c>OnnxEmbeddingGenerator</c> instance.
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
    /// <summary>
    /// The growth threshold ingestion runs under: the validated maximum, not the shipped default.
    /// </summary>
    /// <remarks>
    /// The default of 0.10 rebuilds whenever the corpus grows 10%, which is right for a live corpus
    /// and ruinous for a bulk load: 609 articles trigger 48 whole-corpus rebuilds, each
    /// re-clustering everything so far and summarising it. One early build still happens — the
    /// debounce's baseline starts at -1, so the first document ingested always builds — but it is
    /// over one document's chunks and costs almost nothing, and its output is discarded when
    /// <see cref="RaptorTreeRebuilder.RebuildAsync"/> deletes and replaces the corpus tree. The tree
    /// that is measured comes from that single call, made once ingestion finishes.
    /// </remarks>
    private const double CorpusGrowthThreshold = 100.0;

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

        var options = new RaptorOptions
        {
            TreeScope = scope,
            CorpusGrowthThreshold = CorpusGrowthThreshold,
        };

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
    /// <see cref="RaptorTreeRebuilder.RebuildAsync"/> call's return value — not an accumulation
    /// across ingestion, because that call deletes and replaces whatever the debounce's one cheap
    /// early build produced. Under <see cref="RaptorTreeScope.PerDocument"/> there is no rebuild, so
    /// this accumulates the summaries each document's own ingestion produced.
    /// </remarks>
    public int SummaryCount { get; private set; }

    /// <summary>
    /// Gets how many times this run called <see cref="RaptorTreeRebuilder.RebuildAsync"/>.
    /// </summary>
    /// <remarks>
    /// <b>Not a count of every tree build this run performed.</b> The first document ingested under
    /// <see cref="RaptorTreeScope.Corpus"/> always triggers one cheap build over its own chunks —
    /// the debounce's baseline starts at -1, so <c>RaptorIngestionBehavior.ShouldBuild</c> is always
    /// true the first time it is asked — and that build is real but is not what this counts. A
    /// benchmark's cost is the corpus-wide rebuild, made exactly once after ingestion finishes; a
    /// property named for "every tree build" would read <c>1</c> here for the wrong reason and hide
    /// that a second, trivial build happened first. Always <c>0</c> under
    /// <see cref="RaptorTreeScope.PerDocument"/>, which builds during ingestion and never rebuilds.
    /// </remarks>
    public int CorpusRebuildCount => _corpusRebuildCount;

    /// <summary>Gets how many requests <see cref="RaptorIngestionBehavior"/> sent the summariser.</summary>
    public long SummariserCalls => _summariser.Calls;

    /// <summary>Runs RAPTOR retrieval for one query and returns what it found.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="mode">Which <see cref="RaptorRetrievalMode"/> to search under.</param>
    /// <param name="topK">Chunks to return.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The results <see cref="RaptorRetrievalBehavior"/> produced.</returns>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, RaptorRetrievalMode mode, int topK, CancellationToken cancellationToken)
    {
        var behavior = new RaptorRetrievalBehavior(new RaptorRetrievalOptions { Mode = mode });
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

    /// <summary>Chunks, embeds and indexes every article, running RAPTOR ingestion over each.</summary>
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

            var ctx = new IngestionContext
            {
                Stream = Stream.Null,
                Metadata = new DocumentMetadata
                {
                    DocumentId = new DocumentId(documents[i].Id),
                    FileName = documents[i].Id,
                },
                GetNextBm25DocId = static () => 0,
            };

            for (var j = 0; j < chunks.Count; j++)
            {
                ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunks[j], Embedding = vectors[j].Vector });
            }

            LeafCount += chunks.Count;

            _ = await _behavior.HandleAsync(ctx, cancellationToken, static (c, _) => ValueTask.FromResult(
                new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }))
                .ConfigureAwait(false);

            if (_scope == RaptorTreeScope.PerDocument)
            {
                SummaryCount += ctx.EmbeddedChunks.Count - chunks.Count;
            }

            await _store.StoreAsync(ctx.EmbeddedChunks, cancellationToken).ConfigureAwait(false);
        }
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
    /// an interface so a fast-tier test can hand it a fake.
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
