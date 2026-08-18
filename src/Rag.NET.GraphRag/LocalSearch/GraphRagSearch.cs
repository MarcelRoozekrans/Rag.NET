using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Graph;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;

namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// Microsoft's local search: select entities by embedding similarity, collect everything the graph
/// attaches to them, assemble a context window under a token budget, answer from it.
/// </summary>
/// <remarks>
/// <para>
/// The five collection steps below are step 3 of the 2026-03-30 design, which never shipped. See
/// <c>docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md</c> for the reading of upstream
/// this follows and for the deviations it cannot avoid.
/// </para>
/// <para>
/// Every store read is a batch over the whole selection rather than a loop over it. Per-entity
/// reads would be twenty round trips per query against a graph the size of MultiHop-RAG's, which is
/// the shape of cost #300 removed from ingestion and should not reappear at query time.
/// </para>
/// </remarks>
public sealed partial class GraphRagSearch : IGraphRagSearch
{
    private readonly IGraphStore _graphStore;
    private readonly GraphChunkStore _chunkStore;
    private readonly IVectorStore _documentStore;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IChatClient _chatClient;
    private readonly LocalSearchContextOptions _options;
    private readonly LocalSearchContextBuilder _builder;
    private readonly ILogger _logger;

    /// <summary>Creates the search.</summary>
    /// <param name="graphStore">Where entities, relationships and communities live.</param>
    /// <param name="chunkStore">The graph's own vector store, holding entity chunks (#247).</param>
    /// <param name="documentStore">The document store, read by key for source chunks.</param>
    /// <param name="embedder">Embeds the query for entity selection.</param>
    /// <param name="chatClient">Generates the answer.</param>
    /// <param name="options">Budget and ranking settings.</param>
    /// <param name="logger">Optional; absent means no diagnostics, not a failure.</param>
    public GraphRagSearch(
        IGraphStore graphStore,
        GraphChunkStore chunkStore,
        IVectorStore documentStore,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IChatClient chatClient,
        LocalSearchContextOptions options,
        ILogger<GraphRagSearch>? logger = null)
    {
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _builder = new LocalSearchContextBuilder(options);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<LocalSearchContext> BuildLocalContextAsync(
        string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.search");
        activity?.SetTag("graphrag.search.mode", "local");

        var entities = await SelectEntitiesAsync(query, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("graphrag.entity.count", entities.Count);

        if (entities.Count == 0)
        {
            LogNoEntitiesMatched(_logger, query);
            return _builder.Build(new LocalSearchInputs { SelectedEntities = [] });
        }

        var inputs = await CollectAsync(entities, cancellationToken).ConfigureAwait(false);
        var context = _builder.Build(inputs);

        activity?.SetTag("graphrag.context.tokens", context.TokenCount);
        activity?.SetTag("graphrag.context.relationships", context.Relationships.Rendered);
        activity?.SetTag("graphrag.context.sources", context.Sources.Rendered);
        return context;
    }

    /// <inheritdoc/>
    public async Task<LocalSearchAnswer> LocalSearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        var context = await BuildLocalContextAsync(query, cancellationToken).ConfigureAwait(false);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, LocalSearchPrompt.Build(context.Text, _options.ResponseType)),
            new(ChatRole.User, query),
        };

        var response = await _chatClient
            .GetResponseAsync(messages, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new LocalSearchAnswer(response.Text ?? string.Empty, context);
    }

    /// <summary>Selects the entities the query maps to, most similar first.</summary>
    /// <remarks>
    /// <para>
    /// Searches entity-description embeddings in the graph's own chunk store, asking for
    /// <see cref="LocalSearchContextOptions.TopKEntities"/> ×
    /// <see cref="LocalSearchContextOptions.EntityOversampleScaler"/> hits — and, like upstream,
    /// <b>does not truncate back</b>, so the default returns up to 20 entities for a top-k of 10.
    /// </para>
    /// <para>
    /// The hit is not the entity. Entity chunks are written per document, so one carries the
    /// description that document's extraction produced; the merged row — the union of source chunks
    /// and the description assembled across every document mentioning it — exists only in the graph
    /// store. So the hits give the names and the order, and the store gives the entities.
    /// </para>
    /// </remarks>
    /// <param name="query">The user's question.</param>
    /// <param name="ct">Cancels the embedding call and the searches.</param>
    /// <returns>The selected entities, in similarity order.</returns>
    private async Task<IReadOnlyList<GraphEntity>> SelectEntitiesAsync(string query, CancellationToken ct)
    {
        var topK = _options.TopKEntities * _options.EntityOversampleScaler;
        var embeddings = await _embedder.GenerateAsync([query], cancellationToken: ct).ConfigureAwait(false);

        var hits = await _chunkStore.Store.SearchAsync(
            embeddings[0].Vector,
            new SearchOptions
            {
                TopK = topK,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    [GraphChunkSearch.GraphTypeKey] = "entity",
                },
            },
            ct).ConfigureAwait(false);

        var names = NamesInHitOrder(hits);
        if (names.Count == 0)
        {
            return [];
        }

        var entities = await _graphStore.GetEntitiesAsync(names, ct).ConfigureAwait(false);
        return InSelectionOrder(names, entities);
    }

    /// <summary>Reads entity names off the hits, keeping search order and dropping repeats.</summary>
    /// <remarks>
    /// Repeats are expected, not exceptional: one entity mentioned in five documents has five entity
    /// chunks, all matching a query about it. Keeping the first is what makes the selection ten to
    /// twenty distinct entities rather than one entity five times.
    /// </remarks>
    /// <param name="hits">Search results over entity chunks.</param>
    /// <returns>Distinct entity names, most similar first.</returns>
    private static List<string> NamesInHitOrder(IReadOnlyList<SearchResult> hits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(hits.Count);

        for (var i = 0; i < hits.Count; i++)
        {
            if (hits[i].Chunk.Metadata.TryGetValue("graph_entity_name", out var name) &&
                name.ToString() is { Length: > 0 } value &&
                seen.Add(value))
            {
                names.Add(value);
            }
        }

        return names;
    }

    /// <summary>Puts fetched entities back into the order the search returned their names in.</summary>
    /// <remarks>
    /// The store returns rows in whatever order its query produced, and selection order is
    /// load-bearing twice over downstream: it is the entity table's order, and it is the primary
    /// sort key for source chunks. A name with no row — deleted since the chunk was written — is
    /// skipped.
    /// </remarks>
    /// <param name="names">Names in selection order.</param>
    /// <param name="entities">Entities in store order.</param>
    /// <returns>The entities in selection order.</returns>
    private static List<GraphEntity> InSelectionOrder(
        List<string> names, IReadOnlyList<GraphEntity> entities)
    {
        var byName = new Dictionary<string, GraphEntity>(entities.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entities.Count; i++)
        {
            byName[entities[i].Name] = entities[i];
        }

        var ordered = new List<GraphEntity>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            if (byName.TryGetValue(names[i], out var entity))
            {
                ordered.Add(entity);
            }
        }

        return ordered;
    }

    /// <summary>Fetches everything the selected entities reach.</summary>
    /// <param name="entities">Selected entities, in selection order.</param>
    /// <param name="ct">Cancels the store reads.</param>
    /// <returns>The builder's inputs.</returns>
    private async Task<LocalSearchInputs> CollectAsync(
        IReadOnlyList<GraphEntity> entities, CancellationToken ct)
    {
        var names = new List<string>(entities.Count);
        for (var i = 0; i < entities.Count; i++)
        {
            names.Add(entities[i].Name);
        }

        var relationships = await _graphStore.GetRelationshipsAsync(names, ct).ConfigureAwait(false);
        var communities = await _graphStore
            .GetCommunitiesForEntitiesAsync(names, ct).ConfigureAwait(false);
        var degrees = await _graphStore
            .GetDegreesAsync(DegreeSubjects(names, relationships), ct).ConfigureAwait(false);
        var sourceChunks = await FetchSourceChunksAsync(entities, ct).ConfigureAwait(false);

        return new LocalSearchInputs
        {
            SelectedEntities = entities,
            Relationships = relationships,
            Communities = communities,
            SourceChunks = sourceChunks,
            EntityDegrees = degrees,
        };
    }

    /// <summary>Names whose degree the relationship ranking needs.</summary>
    /// <remarks>
    /// Both endpoints of every candidate relationship, not just the selected entities — a
    /// relationship's rank is the sum of its two endpoints' degrees, and one of those endpoints is
    /// outside the selection for every out-of-network edge. Asking only about the selection would
    /// give every out-of-network relationship the same rank and flatten the ordering that decides
    /// which ones the model sees.
    /// </remarks>
    /// <param name="names">Selected entity names.</param>
    /// <param name="relationships">Candidate relationships.</param>
    /// <returns>Distinct names to ask degrees for.</returns>
    private static List<string> DegreeSubjects(
        List<string> names, IReadOnlyList<GraphRelationship> relationships)
    {
        var subjects = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < relationships.Count; i++)
        {
            _ = subjects.Add(relationships[i].SourceEntity);
            _ = subjects.Add(relationships[i].TargetEntity);
        }

        return [.. subjects];
    }

    /// <summary>Resolves the selected entities' provenance ids to the chunks they name.</summary>
    /// <remarks>
    /// Needs <see cref="IChunkLookup"/> on the document store, because these chunks are chosen by
    /// graph provenance and no query vector returns exactly them. A store without the capability
    /// leaves the Sources section empty — half the token budget — so it is logged once per query
    /// rather than left to be inferred from a short answer.
    /// </remarks>
    /// <param name="entities">Selected entities.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>Chunks by provenance id.</returns>
    private async Task<IReadOnlyDictionary<string, TextChunk>> FetchSourceChunksAsync(
        IReadOnlyList<GraphEntity> entities, CancellationToken ct)
    {
        var empty = new Dictionary<string, TextChunk>(StringComparer.Ordinal);

        if (_documentStore is not IChunkLookup { SupportsChunkLookup: true } lookup)
        {
            LogNoChunkLookup(_logger, _documentStore.GetType().Name);
            return empty;
        }

        var keys = new List<ChunkKey>();
        for (var i = 0; i < entities.Count; i++)
        {
            foreach (var id in entities[i].SourceChunkIds)
            {
                if (GraphChunkId.TryParse(id, out var key))
                {
                    keys.Add(key);
                }
            }
        }

        if (keys.Count == 0)
        {
            return empty;
        }

        var chunks = await lookup.GetChunksAsync(keys, ct).ConfigureAwait(false);
        for (var i = 0; i < chunks.Count; i++)
        {
            empty[GraphChunkId.Format(chunks[i])] = chunks[i];
        }

        return empty;
    }

    [LoggerMessage(
        EventId = 1236894401,
        EventName = "graphrag_local_search_no_entities",
        Level = LogLevel.Debug,
        Message = "Local search matched no entities for '{Query}', so its context is empty. The graph holds no entity chunks, or none passed the metadata filter")]
    private static partial void LogNoEntitiesMatched(ILogger logger, string query);

    [LoggerMessage(
        EventId = 1902337715,
        EventName = "graphrag_local_search_no_chunk_lookup",
        Level = LogLevel.Warning,
        Message = "The registered vector store {StoreType} cannot read chunks by key (IChunkLookup), so local search's Sources section is empty — half of its token budget goes unspent. See issue #318")]
    private static partial void LogNoChunkLookup(ILogger logger, string storeType);
}
