using Rag.NET.Graph;
using Rag.NET.Models;
using Rag.NET.Retrieval;
using Rag.NET.Telemetry;

namespace Rag.NET.GraphRag;

/// <summary>
/// Local search behavior that traverses entity neighbors, relationships, and community reports
/// from the graph store, then blends PageRank with vector similarity scores.
/// Position: before RerankingBehavior in the retrieval pipeline.
/// </summary>
public sealed class GraphLocalSearchBehavior(
    IGraphStore graphStore,
    GraphRagRetrievalOptions options) : IRetrievalBehavior
{
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        var entityResults = CollectTopEntities(results);
        if (entityResults.Count == 0)
            return results;

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.search");
        activity?.SetTag("graphrag.search.mode", "local");
        activity?.SetTag("graphrag.entity.count", entityResults.Count);

        var pageRankByName = await TraverseGraph(entityResults, ct).ConfigureAwait(false);
        return BlendAndDeduplicate(results, pageRankByName);
    }

    private List<SearchResult> CollectTopEntities(IReadOnlyList<SearchResult> results)
    {
        return results
            .Where(r => r.Chunk.Metadata.TryGetValue("graph_type", out var gt)
                        && gt == "entity")
            .OrderByDescending(r => r.Score)
            .Take(options.LocalTopEntities)
            .ToList();
    }

    private async Task<Dictionary<string, double>> TraverseGraph(
        List<SearchResult> entityResults, CancellationToken ct)
    {
        var pageRankByName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (var idx = 0; idx < entityResults.Count; idx++)
        {
            if (!entityResults[idx].Chunk.Metadata.TryGetValue("graph_entity_name", out var nameValue))
                continue;

            var name = nameValue.ToString();
            var neighbors = await graphStore.GetNeighborsAsync(name, options.LocalSearchDepth, ct).ConfigureAwait(false);
            for (var i = 0; i < neighbors.Count; i++)
                pageRankByName[neighbors[i].Name] = neighbors[i].PageRankScore;

            await graphStore.GetRelationshipsAsync(name, ct).ConfigureAwait(false);
            await graphStore.GetCommunitiesForEntityAsync(name, ct).ConfigureAwait(false);
        }

        return pageRankByName;
    }

    private IReadOnlyList<SearchResult> BlendAndDeduplicate(
        IReadOnlyList<SearchResult> results,
        Dictionary<string, double> pageRankByName)
    {
        var combined = new Dictionary<int, SearchResult>();

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var score = ComputeScore(result, pageRankByName);
            var updated = result with { Score = score };

            if (!combined.TryGetValue(result.Chunk.ChunkIndex, out var existing)
                || existing.Score < score)
            {
                combined[result.Chunk.ChunkIndex] = updated;
            }
        }

        return combined.Values
            .OrderByDescending(r => r.Score)
            .ToList()
            .AsReadOnly();
    }

    private double ComputeScore(SearchResult result, Dictionary<string, double> pageRankByName)
    {
        if (result.Chunk.Metadata.TryGetValue("graph_type", out var graphType)
            && graphType == "entity"
            && result.Chunk.Metadata.TryGetValue("graph_entity_name", out var eName)
            && pageRankByName.TryGetValue(eName.ToString(), out var pageRank))
        {
            return (1 - options.PageRankWeight) * result.Score + options.PageRankWeight * pageRank;
        }

        return result.Score;
    }
}
