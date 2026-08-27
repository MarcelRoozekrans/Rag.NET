using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.Retrieval;
using Rag.NET.Telemetry;

namespace Rag.NET.GraphRag;

/// <summary>
/// Global search behavior that performs map-reduce over community reports using an LLM.
/// Position: before RerankingBehavior in the retrieval pipeline.
/// </summary>
public sealed class GraphGlobalSearchBehavior(
    IChatClient chatClient,
    GraphRagGlobalSearchOptions options,
    GraphChunkStore chunkStore,
    IEmbeddingGenerator<string, Embedding<float>> embedder) : IRetrievalBehavior
{
    private const int DefaultBatchSize = 5;

    /// <summary>Community reports fetched when the handed-down candidate set holds none.</summary>
    private const int DefaultReportCandidates = 50;

    /// <summary>The metadata key and value community reports are tagged with at ingestion.</summary>
    private const string GraphTypeKey = "graph_type";
    private const string CommunityReportKind = "community_report";

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);
        var (communityReports, otherResults) = PartitionResults(results);

        var fetched = false;
        if (communityReports.Count == 0)
        {
            communityReports = await FetchCommunityReports(ctx, next, ct).ConfigureAwait(false);
            fetched = true;
        }

        if (communityReports.Count == 0)
            return results;

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.search");
        activity?.SetTag("graphrag.search.mode", "global");
        activity?.SetTag("graphrag.community.count", communityReports.Count);
        activity?.SetTag("graphrag.community.refetched", fetched);

        var synthesized = await MapReduce(ctx, communityReports, ct).ConfigureAwait(false);
        return PrependSynthesized(synthesized, otherResults);
    }

    /// <summary>
    /// Re-enters the retrieval pipeline asking only for community reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what made the behavior reachable at all.</b> Global search is map-reduce over
    /// community reports, and it partitions them out of whatever the retrieval underneath happened
    /// to return — but a corpus produces a few hundred long, general, multi-entity reports against
    /// tens of thousands of short, specific entity and article chunks, and nothing reserved the
    /// reports a slot. Over a sixty-article slice not one appeared in a dense top-500: the map
    /// phase never ran, and the behavior returned its input untouched while looking as though it
    /// had worked. Expecting a general-purpose dense retriever to surface reports among all that is
    /// a category error — a search defined over community reports should ask for community reports.
    /// </para>
    /// <para>
    /// <b>It uses the seams that already exist rather than taking a vector store of its own.</b>
    /// <c>RetrievalOptions.MetadataFilter</c> is already carried through the pipeline and already
    /// applied by the store, so the fetch is the caller's own retrieval with one filter added —
    /// exactly what a caller had to write by hand to reach this behavior before. Any filter the
    /// caller set is preserved; only the graph-type key is imposed, because a global search that
    /// declined to look at community reports would have nothing to reduce.
    /// </para>
    /// <para>
    /// It costs a second pass through the downstream pipeline, which is why it is conditional: a
    /// candidate set that already contains a report never triggers it.
    /// </para>
    /// </remarks>
    private async Task<List<SearchResult>> FetchCommunityReports(
        RetrievalContext ctx,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next,
        CancellationToken ct)
    {
        // Asks the store that holds community reports, rather than re-entering the caller's pipeline
        // with a filter (#247). Since the graph's chunks moved to their own store, that second pass
        // could only ever come back empty — global search would have stopped working, silently,
        // exactly as it did before GlobalReportCandidates existed.
        //
        // It is also strictly cheaper: no second trip through the downstream behaviours, and no
        // reliance on the document store applying a MetadataFilter it no longer has anything to
        // apply it to.
        var reports = await GraphChunkSearch.SearchAsync(
            chunkStore,
            embedder,
            ctx,
            CommunityReportKind,
            options.GlobalReportCandidates ?? DefaultReportCandidates,
            ct).ConfigureAwait(false);

        _ = next;
        return [.. reports];
    }

    private static (List<SearchResult> Communities, List<SearchResult> Others) PartitionResults(
        IReadOnlyList<SearchResult> results)
    {
        var communities = new List<SearchResult>();
        var others = new List<SearchResult>();

        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Chunk.Metadata.TryGetValue(GraphTypeKey, out var gt)
                && gt == CommunityReportKind)
            {
                communities.Add(results[i]);
            }
            else
            {
                others.Add(results[i]);
            }
        }

        return (communities, others);
    }

    private async Task<string> MapReduce(
        RetrievalContext ctx, List<SearchResult> communityReports, CancellationToken ct)
    {
        var shuffled = ShuffleDeterministic(communityReports, ctx.Query);
        var batches = BatchReports(shuffled);
        var client = options.GlobalChatClient ?? chatClient;

        var partialAnswers = await MapPhase(ctx, batches, client, ct).ConfigureAwait(false);
        return await ReducePhase(ctx, partialAnswers, client, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shuffles the reports in an order that depends on the query and on nothing else — the same
    /// query over the same reports gives the same order in every process, forever.
    /// </summary>
    /// <remarks>
    /// <b>It seeded from <c>string.GetHashCode</c> until #241, and that was random per process.</b>
    /// .NET randomises string hash codes at process start by design, so the "deterministic" shuffle
    /// produced a different report order — and therefore different map prompts and a different
    /// synthesised answer — on every run of the same query at temperature 0, and nothing keyed on
    /// those prompts could ever be replayed. The seed is now FNV-1a over the query's UTF-16 code
    /// units, which is a pure function of the text; <see cref="StableSeed"/> is internal so a test
    /// can pin one query's order across processes.
    /// </remarks>
    private static List<SearchResult> ShuffleDeterministic(List<SearchResult> reports, string query)
    {
        var rng = new Random(StableSeed(query));
        return [.. reports.OrderBy(_ => rng.Next())];
    }

    /// <summary>FNV-1a (32-bit) over the query's UTF-16 code units: the same value in every process.</summary>
    internal static int StableSeed(string query)
    {
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;

        var hash = OffsetBasis;
        foreach (var c in query)
        {
            hash ^= c;
            hash *= Prime;
        }

        return unchecked((int)hash);
    }

    private List<List<SearchResult>> BatchReports(List<SearchResult> shuffled)
    {
        var batchSize = options.GlobalBatchSize ?? DefaultBatchSize;
        var batches = new List<List<SearchResult>>();
        for (var i = 0; i < shuffled.Count; i += batchSize)
            batches.Add(shuffled.GetRange(i, Math.Min(batchSize, shuffled.Count - i)));
        return batches;
    }

    private static async Task<List<string>> MapPhase(
        RetrievalContext ctx, List<List<SearchResult>> batches, IChatClient client, CancellationToken ct)
    {
        var partialAnswers = new List<string>(batches.Count);

        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            var batchTexts = string.Join("\n\n", BuildBatchTexts(batch));
            var prompt = $"Given these community summaries, answer the question: {ctx.Query}\n\nCommunity summaries:\n{batchTexts}";

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response.Text))
                partialAnswers.Add(response.Text);
        }

        return partialAnswers;
    }

    private static List<string> BuildBatchTexts(List<SearchResult> batch)
    {
        var texts = new List<string>(batch.Count);
        for (var j = 0; j < batch.Count; j++)
            texts.Add(batch[j].Chunk.Text);
        return texts;
    }

    private static async Task<string> ReducePhase(
        RetrievalContext ctx, List<string> partialAnswers, IChatClient client, CancellationToken ct)
    {
        var reducePrompt = $"Combine these partial answers into a comprehensive final answer:\n\nQuestion: {ctx.Query}\n\nPartial answers:\n{string.Join("\n\n", partialAnswers)}";
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, reducePrompt)],
            cancellationToken: ct).ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }

    private static IReadOnlyList<SearchResult> PrependSynthesized(string synthesizedText, List<SearchResult> others)
    {
        var synthesizedResult = new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = synthesizedText,
                DocumentId = new DocumentId("graph-global-search"),
                ChunkIndex = -1,
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["graph_type"] = "global_answer",
                },
            },
            Score = 1.0,
        };

        var final = new List<SearchResult>(others.Count + 1) { synthesizedResult };
        final.AddRange(others);
        return final.AsReadOnly();
    }
}
