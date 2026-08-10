using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Raptor.Math;
using Rag.NET.Telemetry;

namespace Rag.NET.Raptor;

/// <summary>
/// Ingestion behavior that builds a RAPTOR tree of recursive summaries.
/// Position: after EmbeddingBehavior, before StorageBehavior.
/// </summary>
public sealed class RaptorIngestionBehavior(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    RaptorOptions options) : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (!options.Enabled || ctx.EmbeddedChunks.Count < options.MinChunksForRaptor)
            return await next(ctx, ct).ConfigureAwait(false);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.raptor.build");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);

        var currentLevel = new List<EmbeddedChunk>(ctx.EmbeddedChunks);
        var allSummaries = new List<EmbeddedChunk>();
        var level = 0;

        while (currentLevel.Count > 1 && (options.MaxTreeDepth is null || level < options.MaxTreeDepth))
        {
            level++;
            var summaryChunks = await BuildLevelAsync(currentLevel, ctx, level, ct).ConfigureAwait(false);
            if (summaryChunks is null)
                break;

            allSummaries.AddRange(summaryChunks);
            currentLevel = summaryChunks;
        }

        activity?.SetTag("raptor.tree.depth", level);
        activity?.SetTag("raptor.summary.count", allSummaries.Count);

        if (!options.StoreLeafChunks)
            ctx.EmbeddedChunks.Clear();

        ctx.EmbeddedChunks.AddRange(allSummaries);

        return await next(ctx, ct).ConfigureAwait(false);
    }

    private async Task<List<EmbeddedChunk>?> BuildLevelAsync(
        List<EmbeddedChunk> currentLevel, IngestionContext ctx, int level, CancellationToken ct)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.raptor.summarize");
        activity?.SetTag("raptor.tree.level", level);
        activity?.SetTag("raptor.chunk.count", currentLevel.Count);

        var embeddings = ExtractEmbeddings(currentLevel);

        var targetDims = System.Math.Min(options.ReducedDimensionality, embeddings[0].Length);
        var reduced = embeddings[0].Length > targetDims
            ? Umap.Fit(embeddings, targetDims)
            : embeddings;

        var k = options.MaxClusters.HasValue
            ? System.Math.Min(options.MaxClusters.Value, currentLevel.Count)
            : GaussianMixtureModel.SelectK(reduced, maxK: System.Math.Min(currentLevel.Count, 10));

        if (k <= 1)
        {
            activity?.SetTag("raptor.cluster.count", 0);
            return null;
        }

        var gmm = GaussianMixtureModel.Fit(reduced, k);
        var clusters = GroupByClusters(currentLevel, gmm.Assignments);
        activity?.SetTag("raptor.cluster.count", clusters.Count);

        var client = options.SummaryChatClient ?? chatClient;
        var emb = options.SummaryEmbedder ?? embedder;
        var summaryChunks = new List<EmbeddedChunk>();

        for (var i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            var summaryChunk = await SummarizeClusterAsync(
                cluster.Chunks, cluster.ClusterId, ctx, level, summaryChunks.Count, client, emb, ct).ConfigureAwait(false);
            summaryChunks.Add(summaryChunk);
        }

        return summaryChunks;
    }

    private static float[][] ExtractEmbeddings(List<EmbeddedChunk> chunks)
    {
        var result = new float[chunks.Count][];
        var span = CollectionsMarshal.AsSpan(chunks);
        for (var i = 0; i < span.Length; i++)
            result[i] = span[i].Embedding.ToArray();
        return result;
    }

    private static List<ClusterGroup> GroupByClusters(List<EmbeddedChunk> chunks, int[] assignments)
    {
        var dict = new Dictionary<int, List<EmbeddedChunk>>();
        for (var i = 0; i < assignments.Length; i++)
        {
            var key = assignments[i];
            if (!dict.TryGetValue(key, out var list))
            {
                list = [];
                dict[key] = list;
            }
            list.Add(chunks[i]);
        }

        var result = new List<ClusterGroup>(dict.Count);
        foreach (var (key, value) in dict)
            result.Add(new ClusterGroup(key, value));
        return result;
    }

    private async Task<EmbeddedChunk> SummarizeClusterAsync(
        List<EmbeddedChunk> childChunks, int clusterId, IngestionContext ctx,
        int level, int summaryIndex, IChatClient client,
        IEmbeddingGenerator<string, Embedding<float>> emb, CancellationToken ct)
    {
        var concatenated = ConcatenateChunkTexts(childChunks);
        var prompt = options.SummaryPrompt.Replace("{chunks}", concatenated, StringComparison.Ordinal);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct).ConfigureAwait(false);
        var summaryText = response.Text ?? string.Empty;

        var summaryEmbeddings = await emb.GenerateAsync(
            [summaryText], cancellationToken: ct).ConfigureAwait(false);

        var childIds = BuildChildIds(childChunks);
        return new EmbeddedChunk
        {
            Chunk = new TextChunk
            {
                Text = summaryText,
                DocumentId = ctx.Metadata.DocumentId,
                ChunkIndex = ctx.EmbeddedChunks.Count + summaryIndex,
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["raptor_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["raptor_cluster_id"] = clusterId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["raptor_child_ids"] = childIds,
                },
            },
            Embedding = summaryEmbeddings[0].Vector,
        };
    }

    private static string ConcatenateChunkTexts(List<EmbeddedChunk> chunks)
    {
        var span = CollectionsMarshal.AsSpan(chunks);
        var parts = new string[span.Length];
        for (var i = 0; i < span.Length; i++)
            parts[i] = span[i].Chunk.Text;
        return string.Join("\n\n---\n\n", parts);
    }

    private static string BuildChildIds(List<EmbeddedChunk> chunks)
    {
        var span = CollectionsMarshal.AsSpan(chunks);
        var ids = new string[span.Length];
        for (var i = 0; i < span.Length; i++)
            ids[i] = span[i].Chunk.ChunkIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(",", ids);
    }

    private readonly record struct ClusterGroup(int ClusterId, List<EmbeddedChunk> Chunks);
}
