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
    GraphRagRetrievalOptions options) : IRetrievalBehavior
{
    private const int DefaultBatchSize = 5;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);
        var (communityReports, otherResults) = PartitionResults(results);

        if (communityReports.Count == 0)
            return results;

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.search");
        activity?.SetTag("graphrag.search.mode", "global");
        activity?.SetTag("graphrag.community.count", communityReports.Count);

        var synthesized = await MapReduce(ctx, communityReports, ct).ConfigureAwait(false);
        return PrependSynthesized(synthesized, otherResults);
    }

    private static (List<SearchResult> Communities, List<SearchResult> Others) PartitionResults(
        IReadOnlyList<SearchResult> results)
    {
        var communities = new List<SearchResult>();
        var others = new List<SearchResult>();

        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Chunk.Metadata.TryGetValue("graph_type", out var gt)
                && gt == "community_report")
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

    private static List<SearchResult> ShuffleDeterministic(List<SearchResult> reports, string query)
    {
        var seed = string.GetHashCode(query, StringComparison.Ordinal);
        var rng = new Random(seed);
        return [.. reports.OrderBy(_ => rng.Next())];
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
