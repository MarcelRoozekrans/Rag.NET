using System.Globalization;
using Microsoft.Extensions.AI;
using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.GraphRag;

/// <summary>
/// Ingestion behavior that runs Leiden community detection, computes PageRank,
/// generates LLM community reports, and embeds them as chunks.
/// </summary>
public sealed class CommunityDetectionBehavior(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IGraphStore graphStore,
    GraphRagOptions options) : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx,
        CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (!options.Enabled)
        {
            return await next(ctx, ct).ConfigureAwait(false);
        }

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.communities");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);

        var snapshot = await graphStore.GetFullGraphAsync(ct).ConfigureAwait(false);

        if (snapshot.Entities.Count == 0)
        {
            activity?.SetTag("graphrag.community.count", 0);
            return await next(ctx, ct).ConfigureAwait(false);
        }

        // Run Leiden community detection
        var communities = Leiden.Detect(snapshot);

        // Compute PageRank and update entities
        var ranks = PageRank.Compute(snapshot);
        await UpdateEntitiesWithPageRank(snapshot.Entities, ranks, ct).ConfigureAwait(false);

        // Generate community reports via LLM
        var client = options.SummarizationChatClient ?? chatClient;
        var updatedCommunities = await GenerateCommunityReports(
            communities, snapshot, client, ct).ConfigureAwait(false);

        // Store communities
        await graphStore.SetCommunitiesAsync(updatedCommunities, ct).ConfigureAwait(false);
        activity?.SetTag("graphrag.community.count", updatedCommunities.Count);

        // Embed community reports
        await EmbedCommunityReports(ctx, updatedCommunities, ct).ConfigureAwait(false);

        return await next(ctx, ct).ConfigureAwait(false);
    }

    private async Task UpdateEntitiesWithPageRank(
        IReadOnlyList<GraphEntity> entities,
        IReadOnlyDictionary<string, double> ranks,
        CancellationToken ct)
    {
        for (int i = 0; i < entities.Count; i++)
        {
            if (ranks.TryGetValue(entities[i].Name, out var score))
            {
                entities[i].PageRankScore = score;
            }
        }

        await graphStore.AddEntitiesAsync(entities, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Community>> GenerateCommunityReports(
        IReadOnlyList<Community> communities,
        GraphSnapshot snapshot,
        IChatClient client,
        CancellationToken ct)
    {
        var entityLookup = new Dictionary<string, GraphEntity>(StringComparer.Ordinal);
        for (int i = 0; i < snapshot.Entities.Count; i++)
        {
            entityLookup[snapshot.Entities[i].Name] = snapshot.Entities[i];
        }

        var updated = new List<Community>(communities.Count);

        for (int i = 0; i < communities.Count; i++)
        {
            var community = communities[i];
            var memberSet = new HashSet<string>(community.MemberEntities, StringComparer.Ordinal);

            // Build entity descriptions
            var entityDescriptions = new List<string>();
            for (int j = 0; j < community.MemberEntities.Count; j++)
            {
                var name = community.MemberEntities[j];
                if (entityLookup.TryGetValue(name, out var entity))
                {
                    entityDescriptions.Add($"- {entity.Name} ({entity.Type}): {entity.Description}");
                }
            }

            // Build relationship descriptions between members
            var relationshipDescriptions = new List<string>();
            for (int j = 0; j < snapshot.Relationships.Count; j++)
            {
                var rel = snapshot.Relationships[j];
                if (memberSet.Contains(rel.SourceEntity) && memberSet.Contains(rel.TargetEntity))
                {
                    relationshipDescriptions.Add($"- {rel.SourceEntity} -> {rel.TargetEntity}: {rel.Description}");
                }
            }

            var prompt = options.CommunityReportPrompt
                .Replace("{entities}", string.Join("\n", entityDescriptions))
                .Replace("{relationships}", string.Join("\n", relationshipDescriptions));

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: ct).ConfigureAwait(false);

            var report = response.Text ?? string.Empty;
            updated.Add(new Community(community.Id, community.Level, community.MemberEntities, report));
        }

        return updated;
    }

    private async Task EmbedCommunityReports(
        IngestionContext ctx,
        IReadOnlyList<Community> communities,
        CancellationToken ct)
    {
        var reportTexts = new List<string>(communities.Count);
        for (int i = 0; i < communities.Count; i++)
        {
            reportTexts.Add(communities[i].ReportSummary ?? string.Empty);
        }

        var embeddings = await embedder.GenerateAsync(reportTexts, cancellationToken: ct).ConfigureAwait(false);

        for (int i = 0; i < communities.Count; i++)
        {
            var community = communities[i];
            ctx.EmbeddedChunks.Add(new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = community.ReportSummary ?? string.Empty,
                    DocumentId = ctx.Metadata.DocumentId,
                    ChunkIndex = -(ctx.EmbeddedChunks.Count + 1),
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["graph_type"] = "community_report",
                        ["community_id"] = community.Id.ToString(CultureInfo.InvariantCulture),
                        ["community_level"] = community.Level.ToString(CultureInfo.InvariantCulture),
                    },
                },
                Embedding = embeddings[i].Vector,
            });
        }
    }
}
