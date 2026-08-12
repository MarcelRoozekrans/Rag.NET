using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Chunking;
using Rag.NET.Graph;
using Rag.NET.GraphRag;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// Drives <see cref="GraphEntityExtractionBehavior"/> over a slice of MultiHop-RAG articles — the
/// one path both the generation run and the GraphRAG guard go through.
/// <para>
/// <b>Shared, not copied, because the cache key is the prompt.</b> The prompt is a function of the
/// extraction template, the configured type constraints, and the chunk text — so a tool that
/// chunked at a different size, or in a different order, or from <c>Text</c> rather than
/// <c>RetrievalText</c>, would compute keys nothing had ever written under. The guard would then
/// fail refuse-on-miss on its very first chunk and the failure would look exactly like an empty
/// cache. One driver makes that impossible instead of merely unlikely.
/// </para>
/// <para>
/// <b>The chunking is the library's own defaults</b> — <see cref="RecursiveChunkingStrategy"/> at
/// stock <see cref="ChunkingOptions"/>, over <see cref="BeirDocument.RetrievalText"/> — which is
/// the same thing <c>BeirRealChunkingTests</c> feeds the dense run. Two protocols over one corpus
/// should differ in the protocol, not in how the text was cut up.
/// </para>
/// </summary>
public static class GraphRagSliceIngestion
{
    /// <summary>
    /// The extraction configuration both runs use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stock <see cref="GraphRagOptions"/>, deliberately: entity and relationship types left open,
    /// <c>GleaningPasses</c> left at 1. "Does GraphRAG function" is a question about what the
    /// library does when nobody configures it, and every knob turned here would be a knob the
    /// answer secretly depended on.
    /// </para>
    /// <para>
    /// It is a method rather than a shared instance because <see cref="GraphRagOptions"/> is
    /// mutable, and one instance handed to two runs is one instance either of them could quietly
    /// change under the other.
    /// </para>
    /// </remarks>
    /// <returns>A fresh options instance at the library's defaults.</returns>
    public static GraphRagOptions CreateOptions() => new();

    /// <summary>
    /// Chunks one article exactly as the dense real-protocol run does.
    /// </summary>
    /// <param name="document">The article.</param>
    /// <param name="cancellationToken">Cancels the chunking.</param>
    /// <returns>Its chunks, in order.</returns>
    public static async Task<IReadOnlyList<TextChunk>> ChunkAsync(
        BeirDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var strategy = new RecursiveChunkingStrategy();
        var options = new ChunkingOptions();
        var section = new DocumentSection
        {
            Text = document.RetrievalText,
            DocumentId = new DocumentId(document.Id),
        };

        var chunks = new List<TextChunk>();
        await foreach (var chunk in strategy.ChunkAsync(section, options, cancellationToken))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    /// <summary>
    /// Runs entity extraction over one article's chunks, writing entities and relationships into
    /// <paramref name="graphStore"/> and returning the entity and relationship chunks the behavior
    /// embedded.
    /// </summary>
    /// <param name="document">The article.</param>
    /// <param name="chunks">Its chunks, from <see cref="ChunkAsync"/>.</param>
    /// <param name="chatClient">The cached extraction client.</param>
    /// <param name="embedder">Embeds the entity and relationship descriptions.</param>
    /// <param name="graphStore">Receives the extracted entities and relationships.</param>
    /// <param name="options">The extraction configuration, from <see cref="CreateOptions"/>.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The embedded graph chunks this article contributed.</returns>
    /// <remarks>
    /// The behavior is invoked directly rather than through a built pipeline, because a pipeline
    /// would also parse a stream, chunk, embed the chunks and store them — four behaviors whose
    /// correctness is not in question here and whose configuration would be four more things this
    /// run secretly depended on. What is exercised is exactly the GraphRAG stage: the same class,
    /// the same options, the same graph store, in the same order.
    /// </remarks>
    public static async Task<IReadOnlyList<EmbeddedChunk>> ExtractAsync(
        BeirDocument document,
        IReadOnlyList<TextChunk> chunks,
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IGraphStore graphStore,
        GraphRagOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunks);

        var context = CreateContext(document, chunks);
        var behavior = new GraphEntityExtractionBehavior(chatClient, embedder, graphStore, options);

        _ = await behavior.HandleAsync(context, cancellationToken, Terminal);

        return context.EmbeddedChunks;
    }

    /// <summary>
    /// The document id the community-detection pass runs under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Community detection is an ingestion behavior, so it needs a document to be ingesting, and
    /// the reports it embeds are tagged with that document's id. It gets a synthetic one rather
    /// than a real article's for two reasons. A real id would attribute every community report to
    /// one arbitrary article, and the guard's ground-truth assertion joins retrieved document ids
    /// against qrels — a community report answering as that article would be a false positive
    /// nobody could see. And <c>InMemoryVectorStore</c> keys on (document id, chunk index), while
    /// both the entity chunks and the community reports use negative indices counting from -1, so
    /// sharing an id would have each report silently overwrite an entity.
    /// </para>
    /// <para>
    /// A URI-shaped id because every real id in this corpus is an article URL, and something that
    /// cannot be mistaken for one is exactly what is wanted.
    /// </para>
    /// </remarks>
    public const string CommunityDocumentId = "graphrag://communities";

    /// <summary>
    /// Runs community detection, PageRank and community-report generation over whatever the
    /// graph store now holds, and returns the embedded report chunks.
    /// </summary>
    /// <param name="chatClient">The cached extraction client; it writes the report prompts too.</param>
    /// <param name="embedder">Embeds the reports.</param>
    /// <param name="graphStore">The graph built by <see cref="ExtractAsync"/> over the whole slice.</param>
    /// <param name="options">The configuration, from <see cref="CreateOptions"/>.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>One embedded chunk per community report.</returns>
    /// <remarks>
    /// <b>Once, over the finished graph — not once per document.</b> As an ingestion behavior it
    /// would run on every document, re-detecting communities over a graph that grows underneath it
    /// and regenerating every report each time: sixty passes over the same sixty articles, at one
    /// LLM call per community per pass. The result of the last pass is the only one that survives
    /// — <c>SetCommunitiesAsync</c> deletes and replaces — so all but the final pass is money spent
    /// on output that is then thrown away. Running it once, after the slice is fully extracted, is
    /// what that behaviour converges to.
    /// </remarks>
    public static async Task<IReadOnlyList<EmbeddedChunk>> DetectCommunitiesAsync(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IGraphStore graphStore,
        GraphRagOptions options,
        CancellationToken cancellationToken)
    {
        var context = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(CommunityDocumentId),
                FileName = CommunityDocumentId,
            },
            GetNextBm25DocId = () => 0,
        };

        var behavior = new CommunityDetectionBehavior(chatClient, embedder, graphStore, options);
        _ = await behavior.HandleAsync(context, cancellationToken, Terminal);

        return context.EmbeddedChunks;
    }

    /// <summary>
    /// Builds the ingestion context the behavior reads: the article's identity and its chunks.
    /// </summary>
    /// <remarks>
    /// <see cref="Stream.Null"/> and a constant BM25 id because the behavior touches neither —
    /// both are required by <see cref="IngestionContext"/> for the stages this run does not have.
    /// The document id is the article URL, which is what <c>MultiHopRagConversion</c> assigns and
    /// therefore what the qrels judge, so an entity's <c>SourceDocumentId</c> joins straight back
    /// to ground truth.
    /// </remarks>
    private static IngestionContext CreateContext(
        BeirDocument document, IReadOnlyList<TextChunk> chunks)
    {
        var context = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(document.Id),
                FileName = document.Id,
            },
            GetNextBm25DocId = () => 0,
        };

        for (var i = 0; i < chunks.Count; i++)
        {
            context.Chunks.Add(chunks[i]);
        }

        return context;
    }

    /// <summary>The end of the pipeline: nothing follows the extraction stage in this run.</summary>
    private static ValueTask<IngestionResult> Terminal(
        IngestionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new IngestionResult
        {
            DocumentId = context.Metadata.DocumentId,
            ChunksStored = context.EmbeddedChunks.Count,
        });
}
