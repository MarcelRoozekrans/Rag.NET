using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Raptor.Math;
using Rag.NET.Raptor.Store;
using Rag.NET.Telemetry;

namespace Rag.NET.Raptor;

/// <summary>
/// Ingestion behavior that builds a RAPTOR tree of recursive summaries.
/// Position: after EmbeddingBehavior, before StorageBehavior.
/// </summary>
public sealed class RaptorIngestionBehavior(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    RaptorOptions options,
    IRaptorLeafStore? leafStore = null) : IIngestionBehavior
{
    private readonly Lock _buildGate = new();
    private int _leavesAtLastBuild = -1;

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (!options.Enabled)
            return await next(ctx, ct).ConfigureAwait(false);

        if (options.TreeScope == RaptorTreeScope.Corpus)
        {
            await PersistLeavesAsync(ctx, ct).ConfigureAwait(false);

            var leafCount = await leafStore!.CountAsync(ct).ConfigureAwait(false);
            if (ShouldBuild(leafCount))
            {
                var leaves = await leafStore.GetAllLeavesAsync(ct).ConfigureAwait(false);
                if (leaves.Count > 1)
                {
                    await BuildTreeAsync(
                        ctx,
                        ToEmbeddedChunks(leaves),
                        new DocumentId(RaptorCorpusDocumentId.Value),
                        firstChunkIndex: 0,
                        activity: null,
                        ct).ConfigureAwait(false);
                }
            }

            return await next(ctx, ct).ConfigureAwait(false);
        }

        if (ctx.EmbeddedChunks.Count < options.MinChunksForRaptor)
            return await next(ctx, ct).ConfigureAwait(false);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.raptor.build");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);

        var summaryCount = await BuildTreeAsync(
            ctx,
            new List<EmbeddedChunk>(ctx.EmbeddedChunks),
            ctx.Metadata.DocumentId,
            firstChunkIndex: ctx.EmbeddedChunks.Count,
            activity,
            ct).ConfigureAwait(false);

        activity?.SetTag("raptor.summary.count", summaryCount);

        if (!options.StoreLeafChunks)
        {
            var summaries = ctx.EmbeddedChunks
                .Where(c => c.Chunk.Metadata.ContainsKey("raptor_level"))
                .ToList();
            ctx.EmbeddedChunks.Clear();
            ctx.EmbeddedChunks.AddRange(summaries);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides whether the corpus has grown enough since the last build to justify another.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>CommunityDetectionBehavior.ShouldDetect</c> deliberately — including the lock and
    /// the -1 sentinel for "no build has run yet" — so that a reader of one recognises the other.
    /// Always true for the first build and whenever the threshold is zero or lower, so a rebuild on
    /// every ingest is available by configuration rather than only by forking.
    /// </remarks>
    /// <param name="leafCount">Leaves currently in the leaf store.</param>
    /// <returns>Whether to build the corpus tree for this ingest.</returns>
    private bool ShouldBuild(int leafCount)
    {
        lock (_buildGate)
        {
            if (_leavesAtLastBuild < 0 || options.CorpusGrowthThreshold <= 0)
            {
                _leavesAtLastBuild = leafCount;
                return true;
            }

            var required = _leavesAtLastBuild * (1 + options.CorpusGrowthThreshold);
            if (leafCount < required)
                return false;

            _leavesAtLastBuild = leafCount;
            return true;
        }
    }

    /// <summary>
    /// Clusters every stored leaf and appends the resulting summaries to <paramref name="ctx"/>,
    /// regardless of the growth threshold, resetting the threshold's baseline.
    /// </summary>
    /// <remarks>
    /// The entry point <c>RaptorTreeRebuilder</c> uses. Deliberately the same code path as
    /// ingestion: a rebuild that clustered its own way would be a second implementation of the
    /// thing under measurement, free to drift from the one that runs during ingest.
    /// </remarks>
    /// <param name="ctx">Receives the summary chunks.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many summaries the build produced; zero when the store holds fewer than two leaves.</returns>
    internal async Task<int> BuildCorpusTreeNowAsync(IngestionContext ctx, CancellationToken ct)
    {
        if (leafStore is null)
            throw new InvalidOperationException("Corpus tree building requires an IRaptorLeafStore.");

        var leaves = await leafStore.GetAllLeavesAsync(ct).ConfigureAwait(false);
        if (leaves.Count < 2)
            return 0;

        lock (_buildGate)
        {
            _leavesAtLastBuild = leaves.Count;
        }

        return await BuildTreeAsync(
            ctx,
            ToEmbeddedChunks(leaves),
            new DocumentId(RaptorCorpusDocumentId.Value),
            firstChunkIndex: 0,
            activity: null,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the level loop over <paramref name="seed"/> and appends every summary produced to
    /// <paramref name="ctx"/>. Shared by the per-document and corpus paths so the two cannot drift.
    /// </summary>
    /// <param name="ctx">Receives the summaries.</param>
    /// <param name="seed">The level-0 chunks to cluster.</param>
    /// <param name="summaryDocumentId">
    /// The document id every summary is filed under. The per-document path passes the ingesting
    /// document's id; the corpus path passes <see cref="RaptorCorpusDocumentId.Value"/>. Explicit
    /// rather than read from <paramref name="ctx"/>, because a corpus summary filed under whichever
    /// document happened to trigger the build is a corpus-level summary attributed to one arbitrary
    /// article — the defect <c>GraphProjectionRebuilder</c>'s remarks describe.
    /// </param>
    /// <param name="firstChunkIndex">The index the first summary takes; it counts up from there across every level (#332).</param>
    /// <param name="activity">
    /// The caller's <c>ragnet.raptor.build</c> span, tagged with the depth reached once the loop
    /// ends. Null for the corpus path, which builds outside any such span.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many summaries were appended.</returns>
    private async Task<int> BuildTreeAsync(
        IngestionContext ctx, List<EmbeddedChunk> seed, DocumentId summaryDocumentId,
        int firstChunkIndex, System.Diagnostics.Activity? activity, CancellationToken ct)
    {
        var currentLevel = seed;
        var allSummaries = new List<EmbeddedChunk>();
        var level = 0;
        // Tracked separately from level: level counts the attempt about to run (it labels
        // BuildLevelAsync's telemetry and each summary's raptor_level), but an attempt that
        // returns null produced no summaries at that level number, so it must not count toward
        // the depth the tree actually reached.
        var depthReached = 0;
        // Summary indices continue past the seed and keep counting across levels. They must not
        // restart per level: ctx.EmbeddedChunks is not appended to until after this loop, so a
        // per-level index made level 2's first summary collide with level 1's (#332).
        var nextChunkIndex = firstChunkIndex;

        while (currentLevel.Count > 1 && (options.MaxTreeDepth is null || level < options.MaxTreeDepth))
        {
            level++;
            var summaryChunks = await BuildLevelAsync(currentLevel, summaryDocumentId, level, nextChunkIndex, ct).ConfigureAwait(false);
            if (summaryChunks is null)
                break;

            depthReached = level;
            nextChunkIndex += summaryChunks.Count;
            allSummaries.AddRange(summaryChunks);
            currentLevel = summaryChunks;
        }

        activity?.SetTag("raptor.tree.depth", depthReached);
        ctx.EmbeddedChunks.AddRange(allSummaries);
        return allSummaries.Count;
    }

    /// <summary>Maps stored leaves back to the embedded-chunk shape clustering runs on.</summary>
    /// <remarks>
    /// Keeps each leaf's own document id and chunk index — clustering only reads the vectors, and a
    /// leaf's identity should not be rewritten just because it is being read back from the store.
    /// </remarks>
    /// <param name="leaves">The leaves to convert.</param>
    /// <returns>One <see cref="EmbeddedChunk"/> per leaf.</returns>
    private static List<EmbeddedChunk> ToEmbeddedChunks(IReadOnlyList<RaptorLeaf> leaves)
    {
        var result = new List<EmbeddedChunk>(leaves.Count);
        foreach (var leaf in leaves)
        {
            result.Add(new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = leaf.Text,
                    DocumentId = new DocumentId(leaf.DocumentId),
                    ChunkIndex = leaf.ChunkIndex,
                },
                Embedding = leaf.Embedding,
            });
        }

        return result;
    }

    /// <summary>
    /// Copies this document's leaf chunks into the leaf store, so a later corpus-wide rebuild can
    /// read them back. <c>MinChunksForRaptor</c> is deliberately not applied: it decides whether one
    /// document is worth a tree of its own, and under corpus scope a short document still
    /// contributes its chunks to the corpus.
    /// </summary>
    private async Task PersistLeavesAsync(IngestionContext ctx, CancellationToken ct)
    {
        if (leafStore is null)
        {
            throw new InvalidOperationException(
                "RaptorOptions.TreeScope is Corpus but no IRaptorLeafStore is registered. " +
                "Register one with UseRaptor(..., leafStorePath: \"...\"), or set TreeScope to PerDocument.");
        }

        if (ctx.EmbeddedChunks.Count == 0)
            return;

        var leaves = BuildLeaves(ctx.EmbeddedChunks);
        await leafStore.AddLeavesAsync(leaves, ct).ConfigureAwait(false);
    }

    // Split out of PersistLeavesAsync (rather than iterating ctx.EmbeddedChunks with
    // CollectionsMarshal.AsSpan inline) because that method is async: a Span<T> local is not
    // allowed to be live across an await point, and the analyzer flags one even when the actual
    // await happens after the span goes out of scope. Kept synchronous, so the span is safe here.
    private static List<RaptorLeaf> BuildLeaves(List<EmbeddedChunk> chunks)
    {
        var span = CollectionsMarshal.AsSpan(chunks);
        var leaves = new List<RaptorLeaf>(span.Length);
        foreach (ref readonly var chunk in span)
        {
            leaves.Add(new RaptorLeaf(
                chunk.Chunk.DocumentId.Value,
                chunk.Chunk.ChunkIndex,
                chunk.Chunk.Text,
                chunk.Embedding.ToArray()));
        }

        return leaves;
    }

    private async Task<List<EmbeddedChunk>?> BuildLevelAsync(
        List<EmbeddedChunk> currentLevel, DocumentId summaryDocumentId, int level, int baseChunkIndex, CancellationToken ct)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.raptor.summarize");
        activity?.SetTag("raptor.tree.level", level);
        activity?.SetTag("raptor.chunk.count", currentLevel.Count);

        var embeddings = ExtractEmbeddings(currentLevel);

        var targetDims = System.Math.Min(options.ReducedDimensionality, embeddings[0].Length);
        var reduced = embeddings[0].Length > targetDims
            ? Umap.Fit(embeddings, targetDims)
            : embeddings;

        var k = SelectClusterCount(reduced, currentLevel.Count, activity);
        if (k is null)
            return null;

        var gmm = GaussianMixtureModel.Fit(reduced, k.Value);
        var clusters = GroupByClusters(currentLevel, gmm.Assignments);
        activity?.SetTag("raptor.cluster.count", clusters.Count);

        var client = options.SummaryChatClient ?? chatClient;
        var emb = options.SummaryEmbedder ?? embedder;
        var summaryChunks = new List<EmbeddedChunk>();

        for (var i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            var summaryChunk = await SummarizeClusterAsync(
                cluster.Chunks, cluster.ClusterId, summaryDocumentId, level, baseChunkIndex + summaryChunks.Count, client, emb, ct)
                .ConfigureAwait(false);
            summaryChunks.Add(summaryChunk);
        }

        return summaryChunks;
    }

    /// <summary>
    /// Picks the cluster count for a level, or reports that the level should not be built at all.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="BuildLevelAsync"/> (MA0051) rather than only for its own sake: the
    /// two rejection cases below both belong together — one level, one decision — regardless of
    /// which arm of the <c>k</c> assignment produced the candidate.
    /// </remarks>
    /// <param name="reduced">The level's (possibly UMAP-reduced) embeddings, for auto-selected k.</param>
    /// <param name="count">The level's chunk count.</param>
    /// <param name="activity">The level's summarize span, tagged with why a rejection happened.</param>
    /// <returns>The cluster count to fit with, or null when the level should not be built.</returns>
    private int? SelectClusterCount(float[][] reduced, int count, System.Diagnostics.Activity? activity)
    {
        // Min(MaxClusters, count) alone caps every level at exactly MaxClusters once the tree has
        // shrunk to that size, and a level whose k equals its own count is what the second check
        // below rejects — so a fixed MaxClusters would build depth 1 and never recurse again,
        // forever, regardless of #333. Min(..., count - 1) forces a strict decrease every level
        // instead, which is what actually guarantees the loop can still terminate (count shrinks
        // by at least one per level) without ever tripping the degenerate check.
        var k = options.MaxClusters.HasValue
            ? System.Math.Min(options.MaxClusters.Value, count - 1)
            : GaussianMixtureModel.SelectK(reduced, maxK: System.Math.Min(count, 10));

        if (k <= 1)
        {
            activity?.SetTag("raptor.cluster.count", 0);
            return null;
        }

        // A level that would produce as many summaries as it consumed cannot terminate the tree
        // loop: the next level clusters the same count into the same count, forever, at one LLM
        // call per cluster per level. Detected here, before any summarisation, so a degenerate
        // level costs nothing. #333 is this exact case — GaussianMixtureModel.SelectK returns
        // k = n for distinct points because a singleton cluster's variance floors to 1e-6 and its
        // log-density then dwarfs the BIC penalty. This guard is deliberately written against the
        // symptom rather than that cause, so a future clustering regression of the same shape is
        // bounded too. The MaxClusters branch above can no longer trigger this: Min(..., count - 1)
        // keeps its k strictly below count, so only auto-selected k reaches here.
        if (k >= count)
        {
            activity?.SetTag("raptor.cluster.degenerate", true);
            activity?.SetTag("raptor.cluster.count", 0);
            return null;
        }

        return k;
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
        List<EmbeddedChunk> childChunks, int clusterId, DocumentId summaryDocumentId,
        int level, int chunkIndex, IChatClient client,
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
                DocumentId = summaryDocumentId,
                ChunkIndex = chunkIndex,
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
