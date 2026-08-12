using Rag.NET.Benchmarks.Quality;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Graph;
using Rag.NET.GraphRag;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Storage;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// One end-to-end pass of <c>Rag.NET.GraphRag</c> over the pinned MultiHop-RAG slice: extract,
/// detect communities, index, and search — the thing <see cref="GraphRagFunctionsTests"/> asserts
/// about.
/// <para>
/// <b>Every LLM call that decides what the graph contains is replayed, not made.</b> Extraction
/// comes out of <see cref="GraphExtractionCache"/> in refuse-on-miss mode, so this class calls no
/// model at all and a machine without the cache fails loudly rather than quietly running a
/// different experiment. Community reports and global search's map-reduce go to
/// <see cref="PromptEchoChatClient"/> instead — one because the library cannot generate them at
/// this scale, one because caching them would make the guard machine-specific. That file has the
/// measurements behind both.
/// </para>
/// <para>
/// <b>The ingestion driver is shared with the generation tool</b>
/// (<see cref="GraphRagSliceIngestion"/>), because the cache key is the rendered prompt: a second
/// copy that chunked differently would compute keys nothing had written under, and the failure
/// would look exactly like an empty cache.
/// </para>
/// </summary>
internal sealed class GraphRagSliceRun : IAsyncDisposable
{
    /// <summary>
    /// How many chunks the vector search hands the graph behaviors before they do their work.
    /// </summary>
    /// <remarks>
    /// Far above a production top-k, and both behaviors need it to be. Local search seeds its
    /// traversal from whichever results carry <c>graph_type = entity</c>, and global search does
    /// nothing at all unless a <c>graph_type = community_report</c> chunk is among them — so a
    /// top-5 over a store holding tens of thousands of chunks would exercise neither, and the guard
    /// would pass while measuring the dense retriever. This is the pipeline's candidate set, not
    /// its answer.
    /// </remarks>
    private const int BaseTopK = 500;

    /// <summary>Document chunks embedded per call — a working-set bound, nothing more.</summary>
    private const int SlabSize = 512;

    /// <summary>
    /// A top-k larger than the store, for the diagnostic that asks where a community report ranks.
    /// <c>InMemoryVectorStore</c> bounds its heap by the corpus, so this reads "everything".
    /// </summary>
    private const int WholeStore = 1_000_000;

    /// <summary>The metadata value <c>CommunityDetectionBehavior</c> tags its reports with.</summary>
    private const string CommunityReportKind = "community_report";

    private readonly OnnxEmbeddingGenerator _generator;
    private readonly EmbeddingCache _embeddings;
    private readonly CachingEmbeddingGenerator _embedder;
    private readonly CachedGraphExtractionClient _chat;
    private readonly PromptEchoChatClient _reportChat = new();
    private readonly PromptEchoChatClient _globalChat = new();
    private readonly SqliteGraphStore _graphStore = new(":memory:");
    private readonly InMemoryVectorStore _vectorStore = new();
    private readonly GraphRagOptions _options = GraphRagSliceIngestion.CreateOptions();
    private readonly GraphRagRetrievalOptions _retrievalOptions = new();
    private readonly Dictionary<string, HashSet<string>> _entityDocuments =
        new(StringComparer.OrdinalIgnoreCase);

    private GraphRagSliceRun(
        OnnxEmbeddingGenerator generator, EmbeddingCache embeddings, GraphExtractionCache extractions)
    {
        _generator = generator;
        _embeddings = embeddings;
        _embedder = new CachingEmbeddingGenerator(generator, embeddings);
        _chat = new CachedGraphExtractionClient(
            extractions, inner: null, GraphExtractionModelIdentity.ExtractionTemperature);
    }

    /// <summary>Gets the finished graph: entities, relationships and communities.</summary>
    public GraphSnapshot Graph { get; private set; } = new([], [], []);

    /// <summary>Gets which slice articles each entity name was extracted from.</summary>
    /// <remarks>
    /// Read off the entity chunks the extraction behavior embedded, whose <c>DocumentId</c> is the
    /// article they came from — rather than off the graph store, whose entity table merges
    /// same-named entities into one row and keeps only the first article's id. The merge is what
    /// makes cross-article recurrence <i>useful</i>; it is also what makes it invisible from the
    /// store, so the guard counts it here where each occurrence is still separate.
    /// Case-insensitive, because the store's <c>name</c> column is <c>COLLATE NOCASE</c> and two
    /// spellings that merge there are one entity here too.
    /// </remarks>
    public IReadOnlyDictionary<string, HashSet<string>> EntityDocuments => _entityDocuments;

    /// <summary>Gets how many text chunks the slice's articles were cut into.</summary>
    public int ChunkCount { get; private set; }

    /// <summary>Gets how many entity and relationship chunks extraction embedded and indexed.</summary>
    public int GraphChunkCount { get; private set; }

    /// <summary>Gets how many community reports were generated, embedded and indexed.</summary>
    public int CommunityReportCount { get; private set; }

    /// <summary>Gets how many extraction and report requests were served from the cache.</summary>
    public long ReplayedRequests => _chat.Calls;

    /// <summary>Gets how many map and reduce calls global search has made through the stub.</summary>
    public long GlobalSearchCalls => _globalChat.Calls;

    /// <summary>Gets how many community reports were synthesised rather than generated.</summary>
    /// <remarks>
    /// One per community, and every one of them a deterministic head of the report prompt rather
    /// than a model's summary — because the prompt for this graph's largest community runs to
    /// 976,425 characters and there is no model to send it to. See
    /// <see cref="PromptEchoChatClient"/>.
    /// </remarks>
    public long SynthesisedReports => _reportChat.Calls;

    /// <summary>
    /// Extracts the slice into a graph, detects communities over it, and indexes everything.
    /// </summary>
    /// <param name="documents">The slice's articles, in corpus order.</param>
    /// <param name="generator">The embedder; the caller disposes it.</param>
    /// <param name="embeddings">The vector cache.</param>
    /// <param name="extractions">The extraction cache, opened refuse-on-miss.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The finished run, ready to search.</returns>
    public static async Task<GraphRagSliceRun> BuildAsync(
        IReadOnlyList<BeirDocument> documents,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        GraphExtractionCache extractions,
        CancellationToken cancellationToken)
    {
        var run = new GraphRagSliceRun(generator, embeddings, extractions);
        await run.IngestAsync(documents, cancellationToken);
        await run.DetectCommunitiesAsync(cancellationToken);

        return run;
    }

    /// <summary>Runs local search for one query and returns what it produced.</summary>
    public async Task<IReadOnlyList<SearchResult>> LocalSearchAsync(
        string query, CancellationToken cancellationToken)
    {
        var behavior = new GraphLocalSearchBehavior(_graphStore, _retrievalOptions);

        return await behavior.HandleAsync(CreateContext(query), cancellationToken, RetrieveAsync);
    }

    /// <summary>
    /// Runs global search for one query over the same candidate set local search gets.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> GlobalSearchAsync(
        string query, CancellationToken cancellationToken)
    {
        var behavior = new GraphGlobalSearchBehavior(_globalChat, _retrievalOptions);

        return await behavior.HandleAsync(CreateContext(query), cancellationToken, RetrieveAsync);
    }

    /// <summary>
    /// Runs global search for one query over a candidate set restricted to community reports.
    /// </summary>
    /// <remarks>
    /// <b>This is the only way the map-reduce runs at all, and that is a finding rather than a
    /// convenience.</b> <c>GraphGlobalSearchBehavior</c> partitions <c>graph_type =
    /// community_report</c> chunks out of whatever the retrieval underneath it returned, and does
    /// nothing whatsoever when there are none. Over this slice there are 655 reports among some
    /// 35,800 indexed chunks, and they are long multi-entity texts competing against short,
    /// specific entity descriptions — so a plain dense top-500 contains no report at all and global
    /// search returns its input untouched. The filter here is the store's own
    /// <c>SearchOptions.MetadataFilter</c>, not a special path: it is what a caller would have to
    /// do to reach the behavior, and the library offers no way to configure it.
    /// </remarks>
    public async Task<IReadOnlyList<SearchResult>> GlobalSearchOverReportsAsync(
        string query, CancellationToken cancellationToken)
    {
        var behavior = new GraphGlobalSearchBehavior(_globalChat, _retrievalOptions);

        return await behavior.HandleAsync(
            CreateContext(query), cancellationToken, RetrieveCommunityReportsAsync);
    }

    /// <summary>
    /// Where the best-scoring community report sits in the full ranking for a query, or -1 when
    /// none is indexed. The diagnostic behind <see cref="GlobalSearchOverReportsAsync"/>'s remark.
    /// </summary>
    public async Task<int> FirstCommunityReportRankAsync(
        string query, CancellationToken cancellationToken)
    {
        var vectors = await BeirHarness.EmbedAsync(
            _generator, _embeddings, [query], cancellationToken);
        var everything = await _vectorStore.SearchAsync(
            vectors[0], new SearchOptions { TopK = WholeStore }, cancellationToken);

        for (var i = 0; i < everything.Count; i++)
        {
            if (everything[i].Chunk.Metadata.TryGetValue("graph_type", out var kind)
                && kind == CommunityReportKind)
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _vectorStore.Dispose();
        _chat.Dispose();
        _reportChat.Dispose();
        _globalChat.Dispose();
        _embedder.Dispose();
        await _graphStore.DisposeAsync();
    }

    /// <summary>
    /// Chunks and extracts every article, indexing the chunks themselves alongside the entity and
    /// relationship chunks extraction produced.
    /// </summary>
    /// <remarks>
    /// The article chunks are indexed too, and they have to be: without them the store holds only
    /// entity descriptions and community reports, and "local search retrieved a relevant document"
    /// would be a statement about which entities happened to be summarised rather than about
    /// retrieval. A real pipeline stores both; this indexes both.
    /// </remarks>
    private async Task IngestAsync(
        IReadOnlyList<BeirDocument> documents, CancellationToken cancellationToken)
    {
        for (var i = 0; i < documents.Count; i++)
        {
            var chunks = await GraphRagSliceIngestion.ChunkAsync(documents[i], cancellationToken);
            ChunkCount += chunks.Count;

            var graphChunks = await GraphRagSliceIngestion.ExtractAsync(
                documents[i], chunks, _chat, _embedder, _graphStore, _options, cancellationToken);
            GraphChunkCount += graphChunks.Count;

            RecordEntityDocuments(graphChunks);
            await _vectorStore.StoreAsync(graphChunks, cancellationToken);
            await IndexArticleChunksAsync(chunks, cancellationToken);
        }
    }

    /// <summary>Notes which article each extracted entity name came from.</summary>
    private void RecordEntityDocuments(IReadOnlyList<EmbeddedChunk> graphChunks)
    {
        for (var i = 0; i < graphChunks.Count; i++)
        {
            var chunk = graphChunks[i].Chunk;
            if (!chunk.Metadata.TryGetValue("graph_entity_name", out var name))
            {
                continue;
            }

            var entity = name.ToString();
            if (!_entityDocuments.TryGetValue(entity, out var documents))
            {
                documents = new HashSet<string>(StringComparer.Ordinal);
                _entityDocuments[entity] = documents;
            }

            _ = documents.Add(chunk.DocumentId.Value);
        }
    }

    /// <summary>Embeds and indexes one article's own chunks, through the vector cache.</summary>
    private async Task IndexArticleChunksAsync(
        IReadOnlyList<TextChunk> chunks, CancellationToken cancellationToken)
    {
        for (var start = 0; start < chunks.Count; start += SlabSize)
        {
            var end = Math.Min(start + SlabSize, chunks.Count);
            var texts = new string[end - start];
            for (var i = start; i < end; i++)
            {
                texts[i - start] = chunks[i].Text;
            }

            var vectors = await BeirHarness.EmbedAsync(
                _generator, _embeddings, texts, cancellationToken);

            var stored = new EmbeddedChunk[end - start];
            for (var i = start; i < end; i++)
            {
                stored[i - start] = new EmbeddedChunk
                {
                    Chunk = chunks[i],
                    Embedding = vectors[i - start],
                };
            }

            await _vectorStore.StoreAsync(stored, cancellationToken);
        }
    }

    /// <summary>Runs community detection once over the finished graph and indexes the reports.</summary>
    private async Task DetectCommunitiesAsync(CancellationToken cancellationToken)
    {
        var reports = await GraphRagSliceIngestion.DetectCommunitiesAsync(
            _reportChat, _embedder, _graphStore, _options, cancellationToken);

        CommunityReportCount = reports.Count;
        await _vectorStore.StoreAsync(reports, cancellationToken);

        Graph = await _graphStore.GetFullGraphAsync(cancellationToken);
    }

    /// <summary>The retrieval context both searches run in.</summary>
    private static RetrievalContext CreateContext(string query) =>
        new()
        {
            Query = query,
            Options = new RetrievalOptions { TopK = BaseTopK },
        };

    /// <summary>
    /// The dense retrieval both graph behaviors sit in front of: embed the query through the cache,
    /// scan the store, return the candidates.
    /// </summary>
    private async ValueTask<IReadOnlyList<SearchResult>> RetrieveAsync(
        RetrievalContext context, CancellationToken cancellationToken)
    {
        var vectors = await BeirHarness.EmbedAsync(
            _generator, _embeddings, [context.Query], cancellationToken);

        return await _vectorStore.SearchAsync(
            vectors[0], new SearchOptions { TopK = BaseTopK }, cancellationToken);
    }

    /// <summary>The same retrieval, restricted to community reports by the store's own filter.</summary>
    private async ValueTask<IReadOnlyList<SearchResult>> RetrieveCommunityReportsAsync(
        RetrievalContext context, CancellationToken cancellationToken)
    {
        var vectors = await BeirHarness.EmbedAsync(
            _generator, _embeddings, [context.Query], cancellationToken);

        return await _vectorStore.SearchAsync(
            vectors[0],
            new SearchOptions
            {
                TopK = BaseTopK,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["graph_type"] = CommunityReportKind,
                },
            },
            cancellationToken);
    }
}
