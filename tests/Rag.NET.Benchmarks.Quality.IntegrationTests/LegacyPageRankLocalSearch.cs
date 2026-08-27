using Microsoft.Extensions.AI;
using Rag.NET.Graph;
using Rag.NET.GraphRag;
using Rag.NET.Models;
using Rag.NET.Retrieval;
using Rag.NET.Telemetry;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// A frozen copy of the deleted <c>GraphLocalSearchBehavior</c>, kept so the figures measured
/// through it stay reproducible.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a measurement fixture, not an implementation. Do not improve it.</b> It was removed
/// from <c>Rag.NET.GraphRag</c> on 2026-08-27 because it blends PageRank into dense retrieval
/// scores, which is not in Microsoft's local search at all — that blend was the entire −0.02761
/// nDCG@10 charged to GraphRAG in Milestone 5.2. The shipped local search is
/// <c>Rag.NET.GraphRag.LocalSearch.IGraphRagSearch</c>, measured at 0.3459 overall and 0.8603 on
/// inference.
/// </para>
/// <para>
/// Three pinned figures execute this code and nothing else can reproduce them:
/// <c>MultiHopRagAnswerReproduction</c>'s local arm (0.2102 at weight 0.3),
/// <c>BeirReproduction</c>'s GraphRag nDCG (0.56897), and the blend ablation in
/// <c>BeirGraphRagCorpusTests</c>. <b>Any behavioural change here silently changes what those
/// three numbers mean</b>, without failing anything — which is why
/// <c>LegacyPageRankLocalSearchTests</c> moved here with it.
/// </para>
/// </remarks>
internal sealed class LegacyPageRankLocalSearch(
    IGraphStore graphStore,
    LegacyPageRankOptions options,
    GraphChunkStore chunkStore,
    IEmbeddingGenerator<string, Embedding<float>> embedder) : IRetrievalBehavior
{
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        // Seeds from the store that holds entity chunks, not from the caller's results (#247).
        //
        // It used to sift entity chunks out of what retrieval returned, which only worked because
        // those chunks were mixed into the document store — the very thing that cost -0.21 answer
        // accuracy. With the stores separated there are none to sift, so the seeds are fetched.
        var entityResults = await GraphChunkSearch.SearchAsync(
            chunkStore, embedder, ctx, "entity", options.LocalTopEntities, ct).ConfigureAwait(false);

        if (entityResults.Count == 0)
            return results;

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.search");
        activity?.SetTag("graphrag.search.mode", "local");
        activity?.SetTag("graphrag.entity.count", entityResults.Count);

        // At w = 0 the blend is the identity, so every PageRank score the walk collects is read by
        // nothing. Skipped rather than harvested — issuing graph queries and discarding the answers
        // is the exact waste #239 point 3 removed from this method, and defaulting the weight to 0
        // would have reintroduced it one level up.
        //
        // BlendAndDeduplicate still runs, and that is deliberate: it also DEDUPLICATES, keyed on
        // (DocumentId, ChunkIndex), which is load-bearing on its own — see its remarks and #231.
        // Returning `results` here instead would silently restore the duplicate-candidate defect.
        var pageRankByName = options.PageRankWeight == 0
            ? EmptyPageRank
            : await TraverseGraph(entityResults, ct).ConfigureAwait(false);

        activity?.SetTag("graphrag.pagerank.blended", options.PageRankWeight != 0);

        return BlendAndDeduplicate(results, pageRankByName);
    }

    /// <summary>No PageRank scores, for the default <c>PageRankWeight = 0</c> path.</summary>
    /// <remarks>
    /// Shared and empty rather than a fresh dictionary per query: nothing writes to it, and at the
    /// default this is every query.
    /// </remarks>
    private static readonly Dictionary<string, double> EmptyPageRank =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, double>> TraverseGraph(
        IReadOnlyList<SearchResult> entityResults, CancellationToken ct)
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

            // GetRelationshipsAsync and GetCommunitiesForEntityAsync were awaited here and their
            // results discarded (#239, point 3). Removed rather than wired in: what to do with
            // relationships and community reports is a behaviour question — Microsoft's local search
            // feeds them to the model as context, and this behaviour does not — and #239 points 1
            // and 2 own that decision. Issuing the queries and dropping the answers is not a
            // half-step toward it; it is the cost with none of the benefit.
            //
            // Priced before deleting, so this is not a tidy-up justified by taste: on the
            // MultiHop-RAG corpus the pair ran once per seed entity, each a full scan of a
            // 147,021-row relationships table, which the budget cell measured at roughly 45,000
            // scans per query pass. Nothing read the results, so nothing observable changes.
        }

        return pageRankByName;
    }

    /// <summary>
    /// Blends PageRank into each candidate's score and collapses duplicates, keeping the
    /// highest-scoring instance of each chunk.
    /// </summary>
    /// <remarks>
    /// Keyed on <c>(DocumentId, ChunkIndex)</c> — the pair that identifies a chunk, and the same
    /// key <c>InMemoryVectorStore</c>, <c>RrfMerger</c> and every other dedup path in the pipeline
    /// use. A chunk index alone is a position within one document, not an identity: article chunks
    /// run <c>0..n</c> and <c>GraphEntityExtractionBehavior</c> assigns entity and relationship
    /// chunks negative indices per document, so index <c>0</c> and index <c>-1</c> exist in every
    /// document of a corpus. Keying on it alone made candidates from unrelated documents collide,
    /// and discarded the lower-scoring one — a silent loss of roughly a third of the candidate set
    /// over a multi-document corpus, chosen by a score comparison across documents that have
    /// nothing to do with each other.
    /// </remarks>
    private IReadOnlyList<SearchResult> BlendAndDeduplicate(
        IReadOnlyList<SearchResult> results,
        Dictionary<string, double> pageRankByName)
    {
        var combined = new Dictionary<(string DocId, int ChunkIndex), SearchResult>();

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var score = ComputeScore(result, pageRankByName);
            var updated = result with { Score = score };
            var key = (result.Chunk.DocumentId.Value, result.Chunk.ChunkIndex);

            if (!combined.TryGetValue(key, out var existing) || existing.Score < score)
                combined[key] = updated;
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
