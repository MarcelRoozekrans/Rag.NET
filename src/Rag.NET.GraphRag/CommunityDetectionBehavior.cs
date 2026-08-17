using System.Globalization;
using System.Text;
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
    /// <summary>
    /// Characters held back from the content budget for the truncation notices, so the finished
    /// prompt stays inside <see cref="GraphRagOptions.MaxCommunityReportPromptLength"/> rather than
    /// overshooting it by whatever the notices happen to cost.
    /// </summary>
    private const int NoteReserve = 320;

    /// <summary>Entity count at the last detection, or -1 when none has run.</summary>
    /// <remarks>
    /// Instance state on a type registered as a singleton, so it survives across the documents of an
    /// ingest — which is the whole point. Guarded by <see cref="_detectionGate"/> because concurrent
    /// <c>IngestAsync</c> calls share the instance, and two ingests racing here would otherwise both
    /// read the stale count and both detect.
    /// </remarks>
    private long _entitiesAtLastDetection = -1;

    private readonly Lock _detectionGate = new();

    /// <summary>
    /// Decides whether the graph has grown enough since the last detection to justify another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always true for the first detection and whenever the threshold is zero, so the pre-#300
    /// behaviour is available by configuration rather than only by forking.
    /// </para>
    /// <para>
    /// <b>Claims the slot before returning true.</b> The count is recorded here rather than after the
    /// work completes, so a second document arriving while the first is still detecting does not also
    /// detect. The cost of that choice is that a failed detection does not retry until the graph grows
    /// again — acceptable, because detection is idempotent and the next growth increment repeats it,
    /// and because <see cref="GraphProjectionRebuilder"/> exists for when it must be current now.
    /// </para>
    /// </remarks>
    /// <param name="entityCount">Entities currently in the graph.</param>
    /// <returns>Whether to run detection for this document.</returns>
    private bool ShouldDetect(int entityCount)
    {
        lock (_detectionGate)
        {
            if (_entitiesAtLastDetection < 0 || options.CommunityDetectionGrowthThreshold <= 0)
            {
                _entitiesAtLastDetection = entityCount;
                return true;
            }

            var required = _entitiesAtLastDetection * (1 + options.CommunityDetectionGrowthThreshold);
            if (entityCount < required)
            {
                return false;
            }

            _entitiesAtLastDetection = entityCount;
            return true;
        }
    }

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

        // #300: detection is a whole-graph operation on a per-document behaviour, so this ran once
        // per ingested document against a graph growing throughout. Nothing was gained by the
        // repetition — detection is a pure function of the graph and each run overwrites the last,
        // so only the final run's output survives.
        //
        // The load above still happens, because the entity count IS the growth signal and there is
        // no cheaper count on IGraphStore. What the threshold saves is Leiden, PageRank, the score
        // write-back and the report generation — the expensive part. A count method on the store
        // would let the load be skipped too; that is a wider interface change than this needs.
        if (snapshot.Entities.Count == 0)
        {
            activity?.SetTag("graphrag.community.count", 0);
            return await next(ctx, ct).ConfigureAwait(false);
        }

        if (!ShouldDetect(snapshot.Entities.Count))
        {
            activity?.SetTag("graphrag.community.skipped", true);
            activity?.SetTag("graphrag.community.entity.count", snapshot.Entities.Count);
            return await next(ctx, ct).ConfigureAwait(false);
        }

        await DetectAsync(ctx, snapshot, activity, ct).ConfigureAwait(false);

        return await next(ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs detection unconditionally and appends the report chunks to <paramref name="ctx"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entry point <see cref="GraphProjectionRebuilder"/> uses. It bypasses the growth threshold
    /// — a rebuild is the caller saying "make this current now", which is the whole reason the
    /// threshold is safe to have — and it resets the threshold's baseline, so an ingest continuing
    /// afterwards debounces from the state the rebuild left rather than from a stale count.
    /// </para>
    /// <para>
    /// <b>Deliberately the same code path as ingestion.</b> A rebuild that recomputed communities its
    /// own way would be a second implementation of the thing under measurement, free to drift from
    /// the one that runs during ingest.
    /// </para>
    /// </remarks>
    /// <param name="ctx">Receives the community-report chunks.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of communities detected; zero when the graph is empty.</returns>
    internal async Task<int> DetectNowAsync(IngestionContext ctx, CancellationToken ct)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.communities.rebuild");

        var snapshot = await graphStore.GetFullGraphAsync(ct).ConfigureAwait(false);
        if (snapshot.Entities.Count == 0)
        {
            activity?.SetTag("graphrag.community.count", 0);
            return 0;
        }

        lock (_detectionGate)
        {
            _entitiesAtLastDetection = snapshot.Entities.Count;
        }

        return await DetectAsync(ctx, snapshot, activity, ct).ConfigureAwait(false);
    }

    /// <summary>Leiden, PageRank, reports, and the chunks that carry them — the work itself.</summary>
    /// <param name="ctx">Receives the community-report chunks.</param>
    /// <param name="snapshot">The graph to detect over.</param>
    /// <param name="activity">The span to tag, when one is recording.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of communities detected.</returns>
    private async Task<int> DetectAsync(
        IngestionContext ctx,
        GraphSnapshot snapshot,
        System.Diagnostics.Activity? activity,
        CancellationToken ct)
    {
        // Run Leiden community detection, with the caller's settings rather than the defaults.
        var communities = Leiden.Detect(snapshot, options.Leiden);

        // Compute PageRank and persist the scores -- only the scores.
        var ranks = PageRank.Compute(snapshot);
        await graphStore.SetPageRankScoresAsync(ranks, ct).ConfigureAwait(false);

        // Generate community reports via LLM
        var client = options.SummarizationChatClient ?? chatClient;
        var generated = await GenerateCommunityReports(
            communities, snapshot, ranks, client, ct).ConfigureAwait(false);

        var updatedCommunities = generated.Communities;

        // Store communities
        await graphStore.SetCommunitiesAsync(updatedCommunities, ct).ConfigureAwait(false);
        activity?.SetTag("graphrag.community.count", updatedCommunities.Count);
        activity?.SetTag("graphrag.community.report.truncated", generated.TruncatedReports);
        activity?.SetTag("graphrag.community.report.concurrency", options.CommunityReportConcurrency);

        // Embed community reports
        await EmbedCommunityReports(ctx, updatedCommunities, ct).ConfigureAwait(false);

        return updatedCommunities.Count;
    }

    /// <summary>What one pass of report generation produced, and how much of it did not fit.</summary>
    /// <param name="Communities">The communities, each carrying the report written for it.</param>
    /// <param name="TruncatedReports">How many reports lost members or relationships to the bound.</param>
    private sealed record ReportGeneration(
        IReadOnlyList<Community> Communities, int TruncatedReports);

    /// <summary>
    /// Writes one report per community: every prompt first, in order, then the model calls with
    /// at most <see cref="GraphRagOptions.CommunityReportConcurrency"/> in flight, each answer
    /// written back by index to the community whose prompt produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This loop awaited one community at a time until #226, and the shape of the fix is what
    /// keeps it deterministic.</b> The prompts are built sequentially and up front, so their
    /// content and their order are exactly what the sequential loop produced — the community order
    /// Leiden returned, PageRank order inside each, the ordinal tie-break in
    /// <see cref="OrderByCentrality"/> — and that ordering is what the report cache is keyed on.
    /// Only the calls run in parallel, and each result lands in <c>reports[i]</c> for the
    /// <c>prompts[i]</c> that produced it, so which call finished first decides nothing about
    /// which community carries which report or about the order the list is returned in.
    /// </para>
    /// <para>
    /// <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
    /// over the indices rather than a semaphore around <see cref="Task.WhenAll(Task[])"/>, because
    /// a corpus can hold thousands of communities and it schedules the bound's worth of workers
    /// rather than one task per community waiting on a gate.
    /// </para>
    /// </remarks>
    private async Task<ReportGeneration> GenerateCommunityReports(
        IReadOnlyList<Community> communities,
        GraphSnapshot snapshot,
        IReadOnlyDictionary<string, double> ranks,
        IChatClient client,
        CancellationToken ct)
    {
        var entityLookup = BuildEntityLookup(snapshot);
        var prompts = new string[communities.Count];
        var truncated = 0;

        for (int i = 0; i < communities.Count; i++)
        {
            prompts[i] = BuildReportPrompt(
                communities[i], snapshot, entityLookup, ranks, out var wasTruncated);
            truncated += wasTruncated ? 1 : 0;
        }

        var reports = new string[communities.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, communities.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.CommunityReportConcurrency,
                CancellationToken = ct,
            },
            async (i, token) =>
            {
                var response = await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, prompts[i])],
                    cancellationToken: token).ConfigureAwait(false);

                reports[i] = response.Text ?? string.Empty;
            }).ConfigureAwait(false);

        var updated = new List<Community>(communities.Count);
        for (int i = 0; i < communities.Count; i++)
        {
            var community = communities[i];
            updated.Add(new Community(community.Id, community.Level, community.MemberEntities, reports[i]));
        }

        return new ReportGeneration(updated, truncated);
    }

    /// <summary>Indexes the snapshot's entities by name, the way the store compares names.</summary>
    /// <remarks>
    /// <see cref="GraphNames.Comparer"/>, not <see cref="StringComparer.Ordinal"/>: the member set
    /// derived from this is matched against relationship endpoints, and an endpoint spelled with
    /// different casing from the entity it names is one the store still joins. Comparing ordinally
    /// dropped those relationships out of the report, the same way it dropped them out of the
    /// clustering.
    /// </remarks>
    private static Dictionary<string, GraphEntity> BuildEntityLookup(GraphSnapshot snapshot)
    {
        var entityLookup = new Dictionary<string, GraphEntity>(
            snapshot.Entities.Count, GraphNames.Comparer);

        for (int i = 0; i < snapshot.Entities.Count; i++)
        {
            entityLookup[snapshot.Entities[i].Name] = snapshot.Entities[i];
        }

        return entityLookup;
    }

    /// <summary>
    /// Renders one community into a report prompt, within
    /// <see cref="GraphRagOptions.MaxCommunityReportPromptLength"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bound exists because this prompt used to have none at all.</b> Every member's whole
    /// merged description went into one message, so its size was a property of the corpus rather
    /// than of the code: over a sixty-article slice, while Leiden was over-merging, one prompt
    /// reached 1,806,352 characters — some 450,000 tokens against a 128,000-token context, with no
    /// model to send it to. Fixing the clustering brought that to 195,446, which fits; nothing
    /// about the code made it fit, and a larger corpus would regrow it.
    /// </para>
    /// <para>
    /// <b>It truncates rather than throwing, and spends the budget by centrality.</b> Throwing
    /// would fail an ingestion that used to work, on data rather than on configuration, which is a
    /// regression however principled it looks. Members are emitted in PageRank order so the bound
    /// costs the least-central entities first — the ranks are already computed, two calls up in
    /// <see cref="HandleAsync"/> — and the loss is stated in the prompt itself rather than left for
    /// the model to misread as a complete picture. Three quarters of the content budget goes to
    /// entities and whatever they leave goes to relationships, so a community can never spend the
    /// whole prompt describing its members and never mention how they connect.
    /// </para>
    /// </remarks>
    private string BuildReportPrompt(
        Community community,
        GraphSnapshot snapshot,
        Dictionary<string, GraphEntity> entityLookup,
        IReadOnlyDictionary<string, double> ranks,
        out bool truncated)
    {
        var template = options.CommunityReportPrompt;
        var contentBudget = Math.Max(
            0, options.MaxCommunityReportPromptLength - template.Length - NoteReserve);

        var members = OrderByCentrality(community.MemberEntities, ranks);
        var entities = SelectEntityLines(
            members, entityLookup, contentBudget - (contentBudget / 4), out var entitiesShown);
        var relationships = SelectRelationshipLines(
            community, snapshot, contentBudget - entities.Length,
            out var relationshipsShown, out var relationshipTotal);

        truncated = entitiesShown < members.Count || relationshipsShown < relationshipTotal;

        return template
            .Replace("{entities}", AnnounceEntities(entities, entitiesShown, members.Count))
            .Replace(
                "{relationships}",
                AnnounceRelationships(relationships, relationshipsShown, relationshipTotal));
    }

    /// <summary>Orders member names by PageRank, most central first, ties broken by name.</summary>
    /// <remarks>
    /// The tie-break is not decoration: entities with no edges all score the graph's flat baseline,
    /// so without it the order among them would depend on the store's row order and two runs over
    /// the same corpus could produce different reports.
    /// </remarks>
    private static List<string> OrderByCentrality(
        IReadOnlyList<string> members, IReadOnlyDictionary<string, double> ranks)
    {
        var ordered = new List<string>(members);
        ordered.Sort((left, right) =>
        {
            var byRank = ranks.GetValueOrDefault(right, 0.0).CompareTo(ranks.GetValueOrDefault(left, 0.0));

            return byRank != 0 ? byRank : string.CompareOrdinal(left, right);
        });

        return ordered;
    }

    /// <summary>Renders member descriptions, most central first, until the budget is spent.</summary>
    /// <remarks>
    /// The first line goes in whatever it costs. A budget small enough to exclude even the most
    /// central entity would otherwise produce a report prompt describing nothing at all, and an
    /// empty report is worse than an oversized one — it is indistinguishable from a community that
    /// genuinely holds nothing.
    /// </remarks>
    private static string SelectEntityLines(
        List<string> members,
        Dictionary<string, GraphEntity> entityLookup,
        int budget,
        out int shown)
    {
        var builder = new StringBuilder();
        shown = 0;

        for (int i = 0; i < members.Count; i++)
        {
            if (!entityLookup.TryGetValue(members[i], out var entity))
            {
                continue;
            }

            var line = $"- {entity.Name} ({entity.Type}): {entity.Description}";
            if (builder.Length > 0 && builder.Length + line.Length + 1 > budget)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
            shown++;
        }

        return builder.ToString();
    }

    /// <summary>Renders relationships between members, stopping at the remaining budget.</summary>
    /// <remarks>
    /// Counting continues past the stopping point so <paramref name="total"/> is the community's
    /// real relationship count rather than the count of what happened to fit — the prompt says how
    /// much was left out, and it can only say so honestly if the whole is known.
    /// </remarks>
    private static string SelectRelationshipLines(
        Community community,
        GraphSnapshot snapshot,
        int budget,
        out int shown,
        out int total)
    {
        var memberSet = new HashSet<string>(community.MemberEntities, GraphNames.Comparer);
        var builder = new StringBuilder();
        var full = false;
        shown = 0;
        total = 0;

        for (int i = 0; i < snapshot.Relationships.Count; i++)
        {
            var rel = snapshot.Relationships[i];
            if (!memberSet.Contains(rel.SourceEntity) || !memberSet.Contains(rel.TargetEntity))
            {
                continue;
            }

            total++;
            if (full)
            {
                continue;
            }

            var line = $"- {rel.SourceEntity} -> {rel.TargetEntity}: {rel.Description}";
            if (builder.Length + line.Length + 1 > budget)
            {
                full = true;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
            shown++;
        }

        return builder.ToString();
    }

    /// <summary>The tail every truncation notice ends with, so the numbers are all that vary.</summary>
    private const string OmissionNote =
        "the rest were omitted to keep this report prompt within its configured length.)";

    /// <summary>Appends the entity block's truncation notice, when there is one to make.</summary>
    private static string AnnounceEntities(string block, int shown, int total) =>
        shown >= total
            ? block
            : block + FormattableString.Invariant(
                $"\n({shown} of this community's {total} entities are shown, most central first; {OmissionNote}");

    /// <summary>Appends the relationship block's truncation notice, when there is one to make.</summary>
    private static string AnnounceRelationships(string block, int shown, int total) =>
        shown >= total
            ? block
            : block + FormattableString.Invariant(
                $"\n({shown} of this community's {total} relationships are shown; {OmissionNote}");

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
