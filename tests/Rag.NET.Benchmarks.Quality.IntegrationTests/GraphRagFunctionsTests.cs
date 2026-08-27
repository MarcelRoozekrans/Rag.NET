using Rag.NET.Benchmarks.Quality;
using Rag.NET.Graph;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The first run of <c>Rag.NET.GraphRag</c> from end to end, ever.
/// <para>
/// <b>Why that sentence is not hyperbole.</b> The package shipped, was marked <c>✅ Done</c> in
/// <c>docs/reference/features.md</c>, and had unit tests — and a dead-settings audit (#108) then
/// found three documented behaviours that did not exist: <c>GraphRagOptions.EntityTypes</c> and
/// <c>.RelationshipTypes</c> were inert (#112, fixed), and the retrieval mode setting was never
/// read (#104, open). None of the three was found by a test, a review or a user. <b>None could
/// have survived a single end-to-end run</b>, because there had never been one. This is that run.
/// </para>
/// <para>
/// <b>The assertions are ordered weakest first, so a failure reads as a diagnosis.</b> Extraction
/// produced something; the something recurs across articles; the recurrence clustered; every cluster
/// carries a report; the clusters are searchable against ground truth; global search does something
/// different from local. Asked in that order, the first red assertion names the stage that broke.
/// Asked in any other order, a failure at the end sends the reader looking for a retrieval defect in
/// a pipeline whose graph was empty all along.
/// </para>
/// <para>
/// <b>The reports are real now (#172).</b> They were the head of their own prompt, echoed back by
/// <see cref="PromptEchoChatClient"/>, for as long as the report prompt was unbounded and there was
/// no model to send it to. Bounding it made one cheap generation run possible, so the reports this
/// guard indexes, retrieves and maps over are what <c>openai/gpt-4o-mini</c> returned at temperature
/// 0 — cached and replayed refuse-on-miss exactly as the extractions are, out of
/// <see cref="GraphExtractionCache.ReportsDirectoryName"/>. Only global search's map-reduce still
/// goes to the stub, and for a different reason: its prompts depend on retrieval order, which is
/// machine-specific.
/// </para>
/// <para>
/// <b>Nothing here is a quality measurement.</b> There is no nDCG, no comparison against the dense
/// baseline, and no claim that GraphRAG helps. That question needs the comparative run
/// <c>BeirReproduction</c>'s <c>multihop-rag</c> / <c>Real</c> entry is the anchor for, and it is
/// deliberately out of scope: "does it function" must not wait on "does it help".
/// </para>
/// <para>
/// <b>Deliberately not asserted: retrieval-mode routing.</b> The design intends a
/// <c>Mode</c> setting on <c>GraphRagGlobalSearchOptions</c> selecting local, global or automatic
/// search — issue #104. Neither the property nor a <c>GraphRagRetrievalMode</c> enum exists in the
/// package today, and neither behavior consults anything of the kind: <c>UseGraphRag</c> registers
/// <c>GraphGlobalSearchBehavior</c> unconditionally, and local search left the pipeline entirely
/// (#316) — its live replacement is <c>IGraphRagSearch</c>, also registered unconditionally, outside
/// the pipeline. This guard invokes global search through the registered behavior and local search
/// through <c>LegacyPageRankLocalSearch</c>, the frozen copy of the deleted <c>GraphLocalSearchBehavior</c>
/// that now lives in this harness, invoked directly rather than through any registration. A test
/// asserting that <c>Mode = Local</c> routes
/// to local search would not compile, and one written against a stub would be red on arrival. A
/// permanently failing test is not a guard; the assertion lands with #104's fix.
/// </para>
/// <para>
/// Gated like every other case here: applicability first (an inapplicable dataset must not blame
/// the environment), then provisioning, then <see cref="BeirRunBudget"/>. It additionally needs
/// <b>both</b> directories of <see cref="GraphExtractionCache"/> filled by the generation tool —
/// <c>--stage extraction</c> and then <c>--stage reports</c> — and, exactly like the Hyde cells, it
/// FAILS refuse-on-miss rather than skipping when either is absent on a machine that opted in. The
/// failure names the missing key and the directory it belongs in, so which of the two stages has
/// not been run is never a guess.
/// </para>
/// </summary>
public sealed class GraphRagFunctionsTests
{
    /// <summary>
    /// How many distinct documents deep the ground-truth assertion looks. Ten, matching
    /// <see cref="BeirHarness.Cutoff"/> — the depth every other figure in this project is quoted at.
    /// </summary>
    /// <remarks>
    /// The candidate set the graph behaviors are handed is far larger, and it has to be for them to
    /// have entities and community reports to work with. Asserting against the whole of it would
    /// make "retrieved a relevant document" mean "it was somewhere in five hundred results", which
    /// a pipeline returning the corpus in arbitrary order also satisfies.
    /// </remarks>
    private const int DocumentCutoff = BeirHarness.Cutoff;

    /// <summary>
    /// The share of all entities the largest community may hold before this guard calls the
    /// clustering degenerate. Twenty-five percent, against a measured 7.3%.
    /// </summary>
    /// <remarks>
    /// <b>A ceiling on the largest community's share, not a floor on the singleton count, because
    /// the share is the number that carries the meaning.</b> Leiden legitimately emits one
    /// community per node it cannot attach to anything, so singletons are largely a property of the
    /// extraction rather than a defect, and a bound on them would fail for an honest reason and
    /// pass for a dishonest one. The two counts are printed side by side because they do not match
    /// and the gap is itself informative: 273 of this slice's entities appear in no relationship at
    /// all, while 396 communities hold one member — so 123 entities have edges and still end up
    /// alone.
    /// <para>
    /// <b>That gap was explained wrongly here for as long as it was written down, and the correct
    /// explanation is a finding about extraction.</b> It said their neighbours had been drawn into
    /// other communities. They have not: those 123 entities are not in the clustering's input at
    /// all. <c>Leiden.BuildAdjacency</c> keeps an edge only when both endpoints resolve to an
    /// extracted entity through <c>GraphNames.Comparer</c>, and over this slice 853 of 16,403
    /// relationships — 5.20% — fail that: 837 name something the entity pass never extracted, and
    /// 16 name the same entity at both ends. For 123 entities <i>every</i> edge is one of those, so
    /// they arrive at Leiden isolated and it correctly emits one community each. 273 isolated plus
    /// 123 stranded is exactly 396, which is why the two numbers reconcile to the singleton count
    /// and not approximately. Over the full 609-article corpus the same drop is 5,492 of 147,021,
    /// 3.74%.
    /// </para>
    /// <para>
    /// <b>#176 asked whether that is a defect worth fixing, and the answer is no — established by
    /// looking at the dropped names rather than counting them (2026-08-26).</b> The 853 drops name
    /// <b>565 distinct</b> unresolved endpoints, and they are not entities the extractor missed.
    /// The most frequent are <c>content policies</c> (10), <c>tasks</c> (10), <c>smart plug</c>
    /// (9), <c>handy tool</c> (8), <c>film</c> (7), <c>ceremony</c> (6), <c>death</c> (5),
    /// <c>decisions</c> (5), <c>protagonist</c> (5), <c>regulations</c> (4) — common nouns — mixed
    /// with descriptive paraphrases of things that <i>are</i> extracted: <c>Falun Gong
    /// practitioners</c> beside the entity <c>Falun Gong</c>, <c>Rachel's husband</c>,
    /// <c>Lars Mapstead's parents</c>, <c>second-gen Amazon Echo Buds</c>.
    /// </para>
    /// <para>
    /// <b>So the obvious fix is the wrong one.</b> Promoting unresolved endpoints into entities
    /// would drive the singleton share down while adding 565 junk nodes named after common nouns —
    /// a better-looking number over a worse graph. The singletons are honest, and what produces
    /// them is the model writing relationship endpoints as prose descriptions instead of the
    /// canonical names it extracted. Any fix belongs in the extraction prompt, and would have to be
    /// measured against retrieval rather than against the singleton count, which is exactly the
    /// metric it would be easiest to move without helping. Nothing is changed on this finding.
    /// </para>
    /// <para>
    /// What cannot be legitimate is one community swallowing the graph: at 89.7% — where this slice
    /// sat while <c>Leiden.BuildAggregatedEdges</c> discarded intra-community weight — every
    /// assertion about clustering below is satisfied while nothing has been clustered. The margin
    /// is wide on purpose: this is a degeneracy detector, not a quality target, and a ceiling set
    /// just above the measurement would go red on a corpus change that means nothing.
    /// </para>
    /// <para>
    /// <b>This ceiling did not catch issue #209, and the reason is worth stating where it will be
    /// read.</b> Unbounded relationship weights collapsed the <i>full</i> corpus to 92.13% in one
    /// community while this slice stayed at 7.3%, because the heaviest weight the model returned
    /// over these sixty articles is 6.0 and the two nine-figure ones are in articles the slice does
    /// not contain. A degeneracy detector on a sixtieth of the corpus is worth having and is not
    /// evidence about the corpus.
    /// </para>
    /// </remarks>
    private const double LargestCommunityShareCeiling = 0.25;

    private readonly ITestOutputHelper _output;

    public GraphRagFunctionsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GraphRag_OverTheMultiHopRagSlice_ExtractsClustersAndRetrievesRelevantDocuments()
    {
        // The descriptor is fetched before any gate because the first gate is a question about the
        // dataset rather than about this machine.
        var descriptor = BeirDatasetDescriptor.ByName(MultiHopRagSlice.DatasetName);

        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.GraphRag),
            $"{descriptor.Name} does not declare the GraphRag protocol applicable, so running it " +
            "would produce a result that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(descriptor.Name, BeirProtocol.GraphRag, out var budgetReason),
            budgetReason);

        await RunTheGuardAsync(descriptor, modelPath, vocabPath, cacheDirectory);
    }

    /// <summary>Builds the graph and puts the six assertions to it, in order.</summary>
    private async Task RunTheGuardAsync(
        BeirDatasetDescriptor descriptor, string modelPath, string vocabPath, string cacheDirectory)
    {
        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);

        var documents = MultiHopRagSlice.Documents(dataset.Documents);
        var queries = MultiHopRagSlice.Queries(dataset.Queries);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        await using var run = await GraphRagRun.BuildAsync(
            documents,
            generator,
            new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity),
            new GraphExtractionCache(
                cacheDirectory,
                GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
                GraphExtractionCacheMode.RefuseOnMiss),
            new GraphExtractionCache(
                cacheDirectory,
                GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
                GraphExtractionCacheMode.RefuseOnMiss,
                GraphExtractionCache.ReportsDirectoryName),
            ct);

        _output.WriteLine(Describe(documents, queries, run));
        _output.WriteLine(DescribeDroppedEndpoints(run));

        AssertExtractionProducedEntitiesAndRelationships(run);
        AssertEntitiesRecurAcrossArticles(run);
        AssertCommunityDetectionClusteredTheGraph(run);
        AssertEveryCommunityCarriesARealReport(run);
        await AssertLocalSearchFindsRelevantDocumentsAsync(run, queries, dataset, ct);
        await AssertGlobalSearchDiffersFromLocalAsync(run, queries[0], ct);
    }

    /// <summary>
    /// Names a sample of the relationship endpoints Leiden drops, alongside the counts.
    /// </summary>
    /// <remarks>
    /// <b>The names are the point, and they answered #176.</b> The counts alone invite the obvious
    /// fix — promote an unresolved endpoint into an entity, and the singleton count falls. The
    /// names show why that would make the graph worse rather than better: they are overwhelmingly
    /// common nouns and descriptive paraphrases, not entities the extractor missed. Ten a sample
    /// rather than forty, because the distribution is a long tail and the top of it is
    /// representative.
    /// </remarks>
    private static string DescribeDroppedEndpoints(GraphRagRun run)
    {
        var known = new HashSet<string>(GraphNames.Comparer);
        foreach (var entity in run.Graph.Entities)
        {
            known.Add(entity.Name);
        }

        var missing = new Dictionary<string, int>(GraphNames.Comparer);
        var selfLoops = 0;
        var dropped = 0;

        foreach (var rel in run.Graph.Relationships)
        {
            var sourceKnown = known.Contains(rel.SourceEntity);
            var targetKnown = known.Contains(rel.TargetEntity);

            if (sourceKnown && targetKnown)
            {
                if (GraphNames.Comparer.Equals(rel.SourceEntity, rel.TargetEntity))
                {
                    selfLoops++;
                    dropped++;
                }

                continue;
            }

            dropped++;
            if (!sourceKnown)
            {
                missing[rel.SourceEntity] = missing.GetValueOrDefault(rel.SourceEntity) + 1;
            }

            if (!targetKnown)
            {
                missing[rel.TargetEntity] = missing.GetValueOrDefault(rel.TargetEntity) + 1;
            }
        }

        var ranked = missing.OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
        var lines = new System.Text.StringBuilder();
        lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"""

            #176 DROPPED ENDPOINTS
              entities extracted    : {run.Graph.Entities.Count}
              relationships         : {run.Graph.Relationships.Count}
              relationships dropped : {dropped} ({(double)dropped / run.Graph.Relationships.Count:P2})
                of which self-loops : {selfLoops}
              distinct unknown names: {ranked.Count}
              top 10 unknown endpoint names by frequency:
            """);

        for (var i = 0; i < ranked.Count && i < 10; i++)
        {
            lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"    {ranked[i].Value,4}x  {ranked[i].Key}");
        }

        return lines.ToString();
    }

    /// <summary>Assertion 1: the extraction stage produced a graph at all.</summary>
    /// <remarks>
    /// Both halves, because they fail for different reasons. No entities means the extraction call
    /// returned nothing usable — an unparseable response, a provider error swallowed as an empty
    /// chunk — while entities without relationships means the model answered the entity half of
    /// the schema and not the relationship half, which leaves every later stage a graph of isolated
    /// nodes that Leiden will faithfully report as one community per entity.
    /// </remarks>
    private static void AssertExtractionProducedEntitiesAndRelationships(GraphRagRun run)
    {
        Assert.True(
            run.Graph.Entities.Count > 0,
            FormattableString.Invariant($"""
                EXTRACTION PRODUCED NO ENTITIES. {run.ChunkCount} chunks went through
                GraphEntityExtractionBehavior and the graph store holds nothing. Every later stage
                is vacuous from here: community detection returns early on an empty graph and local
                search finds no entity chunks to seed traversal from.
                """));

        Assert.True(
            run.Graph.Relationships.Count > 0,
            FormattableString.Invariant($"""
                EXTRACTION PRODUCED {run.Graph.Entities.Count} ENTITIES AND NO RELATIONSHIPS. The
                graph has nodes and no edges, so Leiden's adjacency is empty, every entity stays in
                its own community, and both the clustering assertion and global search below would
                be measuring nothing.
                """));
    }

    /// <summary>
    /// Assertion 2: at least one entity was extracted from two or more different articles.
    /// </summary>
    /// <remarks>
    /// <b>Without this, assertion 3 passes vacuously.</b> Community detection can only find
    /// structure that crosses articles, and a slice whose sixty articles shared no entities would
    /// produce sixty disconnected components — which Leiden would return as communities, truthfully,
    /// while finding nothing. This is also the assertion that says the query-derived slice did its
    /// job: it was built that way precisely because a multi-hop question cites two to four articles
    /// that share subjects.
    /// </remarks>
    private static void AssertEntitiesRecurAcrossArticles(GraphRagRun run)
    {
        var (best, articles) = MostRecurrentEntity(run);

        Assert.True(
            articles > 1,
            FormattableString.Invariant($"""
                NO ENTITY RECURS ACROSS ARTICLES. {run.EntityDocuments.Count} distinct entity names
                were extracted and every one of them appears in exactly one article — the most
                widespread being "{best}". Community detection has nothing to cluster, so the
                assertion after this one would pass on sixty disconnected components. Either the
                slice stopped being query-derived, or extraction is naming the same subject
                differently in every article it appears in.
                """));
    }

    /// <summary>Assertion 3: detection returned several communities, and several of them cluster.</summary>
    /// <remarks>
    /// <para>
    /// Both halves, because they fail for opposite reasons. One community means everything
    /// collapsed into a single cluster, which tells a reader nothing about anything. Communities
    /// that all hold one member mean no clustering happened at all — each entity became a
    /// "community" by default, and a run producing nine thousand singletons would satisfy "more
    /// than one community" and satisfy nothing anybody wanted.
    /// </para>
    /// <para>
    /// <b>The second half asks that several communities cluster, not that every one does, and the
    /// difference is a finding rather than a softening.</b> Leiden emits one community per node it
    /// cannot attach to anything, which is correct behaviour and not a defect — but on this graph
    /// it does so 396 times out of 607. Demanding zero singletons would make this file red on
    /// arrival, so the numbers are printed on every run, what is known about them is written down
    /// here, and the assertion guards what it can.
    /// </para>
    /// <para>
    /// <b>Two defects were found underneath these numbers, and both are fixed.</b> First,
    /// <c>Leiden</c> and <c>PageRank</c> matched relationship endpoints to entity names with
    /// <c>StringComparer.Ordinal</c> while <c>SqliteGraphStore</c>'s <c>name</c> column is
    /// <c>COLLATE NOCASE</c>, so every endpoint whose casing differed from the entity it named was
    /// an edge the store held and the clusterer dropped; they now compare through
    /// <c>GraphNames.Comparer</c>. Recovering those edges moved 655 communities and 475 singletons
    /// to 565 and 396 — and <i>grew</i> the largest community from 7,954 to 8,070, which is what
    /// said the real cause lay elsewhere.
    /// </para>
    /// <para>
    /// It lay in aggregation. <c>Leiden.BuildAggregatedEdges</c> skipped edges whose endpoints fell
    /// in the same community instead of folding them into a self-loop on the super-node, so every
    /// level discarded the intra-community weight modularity's null model is computed from; each
    /// level saw communities far lighter than they were, merging always paid, and the recursion
    /// collapsed whatever was connected into one community. It reproduced with no corpus at all —
    /// ten 10-node cliques ring-bridged by one edge each returned <b>one</b> community of 100 — and
    /// <c>LeidenTests</c> now pins that case, along with the two- and three-clique cases whose
    /// <c>Count &gt;= 1</c> assertion had been tolerating the defect. Folding the weight in dropped
    /// the largest community from 8,070 entities to <b>796</b>, its share of the graph from 89.7%
    /// to 8.8%, and the largest report prompt from 1,806,352 characters to 195,446. Implementing the
    /// Leiden paper's refinement phase (#180) moved the largest community again, to <b>661</b>
    /// entities and 7.3%: it splits the one community that had been holding two halves joined by too
    /// little. <b>The community count and the singleton count did not move at all</b> — 607 and 396
    /// before and after — which is the shape to expect, since the refinement redistributes what was
    /// over-merged rather than finding anything new to cluster.
    /// </para>
    /// </remarks>
    private static void AssertCommunityDetectionClusteredTheGraph(GraphRagRun run)
    {
        var communities = run.Graph.Communities;
        var clustered = communities.Count - CountSingletons(communities);
        var largest = LargestCommunity(run);
        var share = (double)largest / run.Graph.Entities.Count;

        Assert.True(
            communities.Count > 1,
            FormattableString.Invariant($"""
                COMMUNITY DETECTION FOUND {communities.Count} COMMUNITIES over
                {run.Graph.Entities.Count} entities and {run.Graph.Relationships.Count}
                relationships. Global search is map-reduce over community reports, so with fewer
                than two there is nothing to map over and its result is one summary of everything.
                """));

        Assert.True(
            clustered > 1,
            FormattableString.Invariant($"""
                ONLY {clustered} OF {communities.Count} COMMUNITIES HOLD MORE THAN ONE ENTITY. A
                community of one is Leiden reporting an entity it could not attach to anything, not
                a cluster — its report summarises one description, and global search maps over it as
                if it were a theme. Over {run.Graph.Entities.Count} entities and
                {run.Graph.Relationships.Count} relationships, that means the relationship endpoints
                are not joining to the entity names they should: check that the clusterer and the
                graph store agree on how entity names compare.
                """));

        Assert.True(
            share < LargestCommunityShareCeiling,
            FormattableString.Invariant($"""
                THE LARGEST COMMUNITY HOLDS {largest} OF {run.Graph.Entities.Count} ENTITIES
                ({share:P1}), above the {LargestCommunityShareCeiling:P0} this guard allows. A
                community holding most of the graph is not a cluster, it is the absence of one: its
                report is a summary of the entire corpus, global search maps over it as though it
                were a theme, and the prompt to write it grows with the corpus rather than with any
                topic in it. Measured at 7.3%, and at 8.8% when this ceiling was set, against 89.7% before
                Leiden.BuildAggregatedEdges stopped discarding intra-community weight — so a number
                anywhere near the ceiling means the aggregation step has regressed to treating
                super-nodes as though they had no internal edges.
                """));
    }

    /// <summary>
    /// Assertion 4: every community carries a report, and the run replayed one per community.
    /// </summary>
    /// <remarks>
    /// <b>This assertion could not be written while the reports were echoes.</b> It would have been
    /// a statement about <see cref="PromptEchoChatClient"/> — which returns a head of the prompt and
    /// therefore never returns nothing — rather than about GraphRAG. Now that the reports come out
    /// of the cache, a blank one means either that <c>CommunityDetectionBehavior</c> stopped storing
    /// what the client returned, or that a community got no report at all, and both make global
    /// search map over a theme with no text in it.
    /// <para>
    /// The count is asserted beside the text because the two fail differently: a report per
    /// community with one of them blank is a storage defect, while fewer requests than communities
    /// means the behavior did not ask about all of them. Nothing here asserts what a report
    /// <i>says</i> — that is a quality question this file deliberately does not ask, and the
    /// summaries are a hosted model's prose, not a fixture to match.
    /// </para>
    /// </remarks>
    private void AssertEveryCommunityCarriesARealReport(GraphRagRun run)
    {
        var communities = run.Graph.Communities;
        var blank = 0;
        for (var i = 0; i < communities.Count; i++)
        {
            blank += string.IsNullOrWhiteSpace(communities[i].ReportSummary) ? 1 : 0;
        }

        _output.WriteLine(FormattableString.Invariant(
            $"community reports: {run.ReplayedReports} replayed for {communities.Count} communities, {blank} blank, longest prompt {run.LongestReportPrompt} characters"));

        Assert.True(
            blank == 0,
            FormattableString.Invariant($"""
                {blank} OF {communities.Count} COMMUNITIES CARRY A BLANK REPORT. Every report in
                this run was generated once against openai/gpt-4o-mini and replayed from the report
                cache, which refuses to store blank text — so a blank one here is not an empty
                generation. Either CommunityDetectionBehavior stopped writing the client's response
                onto the community, or detection produced a community it never asked about.
                """));

        Assert.True(
            run.ReplayedReports == communities.Count,
            FormattableString.Invariant($"""
                {run.ReplayedReports} REPORT REQUESTS WENT THROUGH THE CACHE FOR
                {communities.Count} COMMUNITIES. It is one call per community by construction, so a
                mismatch means detection ran over a different community set than the one the graph
                store now holds — which would also mean the report prompts, and therefore the cache
                keys, belong to a graph this run did not build.
                """));
    }

    /// <summary>
    /// Assertion 5: local search retrieves a known-relevant document for a slice query.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the whole file a guard rather than a smoke test.</b> Every assertion
    /// above it is satisfied by a pipeline that builds a beautiful graph and retrieves the wrong
    /// documents. The slice was derived from judged queries precisely so this assertion is possible:
    /// the ground truth is qrels, not a plausible-looking result list. The named query is asserted
    /// and the whole slice is reported, so a failure says whether one query is unlucky or the
    /// retrieval path is broken.
    /// </remarks>
    private async Task AssertLocalSearchFindsRelevantDocumentsAsync(
        GraphRagRun run,
        IReadOnlyList<BeirQuery> queries,
        BeirDataset dataset,
        CancellationToken cancellationToken)
    {
        var found = 0;
        var firstQueryFound = false;

        for (var i = 0; i < queries.Count; i++)
        {
            var results = await run.LocalSearchAsync(queries[i].Text, cancellationToken);
            var retrieved = TopDocuments(results);
            var hit = AnyRelevant(retrieved, dataset.Qrels[queries[i].Id]);

            found += hit ? 1 : 0;
            firstQueryFound |= hit && i == 0;
            _output.WriteLine(FormattableString.Invariant(
                $"  local {queries[i].Id}: {results.Count} results, {retrieved.Count} documents in the top {DocumentCutoff}, relevant hit: {hit}"));
        }

        _output.WriteLine(FormattableString.Invariant(
            $"local search found a known-relevant document for {found} of {queries.Count} slice queries"));

        Assert.True(firstQueryFound, LocalSearchFailure(queries[0], dataset, found, queries.Count));
    }

    /// <summary>The message a failed ground-truth assertion leaves behind.</summary>
    private static string LocalSearchFailure(
        BeirQuery query, BeirDataset dataset, int found, int total) =>
        FormattableString.Invariant($"""
            LOCAL SEARCH RETRIEVED NONE OF QUERY {query.Id}'s RELEVANT DOCUMENTS in the top
            {DocumentCutoff}. Its {dataset.Qrels[query.Id].Count} judged documents are all inside the
            slice — MultiHopRagSliceTests asserts exactly that — so they were indexed and they were
            retrievable. Across the whole slice, {found} of {total} queries did find one.
            If that tally is near zero the retrieval path is broken; if it is near {total} this one
            query is the outlier and its own results are the place to look.
            """);

    /// <summary>
    /// Assertion 6: global search returns results, actually maps over community reports, and
    /// produces a different set from local search's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two behaviors sitting on one retrieval must not be interchangeable, and "the two lists
    /// differ" is not enough on its own to establish that. It is satisfied by global search doing
    /// <b>nothing</b>: it returns its input untouched when the candidate set holds no community
    /// report, while the counts differ anyway because the two behaviors do different arithmetic on
    /// the same candidate set. That is not a hypothetical — it is what the first run of
    /// this guard measured, and it is why the map-reduce call count is asserted rather than only
    /// printed.
    /// </para>
    /// <para>
    /// <b>That count difference used to come from local search's deduplication, and no longer does
    /// (#230).</b> It keyed on <c>ChunkIndex</c> alone, so candidates from unrelated documents that
    /// happened to share an index collided and the lower-scoring one was discarded: local search
    /// returned 288–412 of its 500 candidates over this slice, a mean loss of roughly a third,
    /// chosen by a score comparison between documents with nothing to do with each other. Keying on
    /// <c>(DocumentId, ChunkIndex)</c> makes it 500 for all 27 queries — the candidate set arrives
    /// intact — and the counts still differ, because global search partitions the community reports
    /// out and prepends one synthesised result. Every figure this file printed for local search
    /// before that fix was measured through the defect.
    /// </para>
    /// <para>
    /// <b>This is now asserted unfiltered, which it could not be before.</b> Over this slice a
    /// plain dense top-500 once contained no community report at all — hundreds of long
    /// multi-entity reports competing against some 35,800 short, specific entity and article chunks
    /// and losing every slot — so the guard reached the map-reduce by restricting the candidate set
    /// with a metadata filter of its own. A guard doing by hand what the library should do is a
    /// guard testing itself, and that path is gone: <c>GraphGlobalSearchBehavior</c> re-enters
    /// retrieval with its own filter when it is handed no reports, and what is asserted here is the
    /// unfiltered call any caller would make.
    /// </para>
    /// <para>
    /// <b>Whether the refetch fires is measured, not assumed, and it did not on the last synthesised
    /// run.</b> Bounding the report prompt changed what the reports say: capped at 50,000 characters
    /// and filled in PageRank order, they lead with their community's most central entities instead
    /// of an arbitrary prefix of all of them, and the best of them moved from rank 1,098 to 209 —
    /// inside the top-500, so the first retrieval already carried reports and the second was never
    /// needed. Real reports are shorter and read nothing like their prompts, so that rank is not
    /// theirs and either path may run now. Neither is asserted: what is asserted is that the
    /// map-reduce happened. The refetch is the safety net for a corpus where reports do not surface,
    /// and
    /// <c>GraphGlobalSearchBehaviorTests</c> is where it is exercised directly. The run prints the
    /// retrieval count so which of the two paths ran is never a guess.
    /// </para>
    /// <para>
    /// <b>Both of those rank figures were measured against synthesised reports and must not be
    /// quoted about real ones (#172).</b> They were taken while <see cref="PromptEchoChatClient"/>
    /// answered report generation with the first 2,000 characters of the prompt, so a "report" was
    /// its community's own entity descriptions rather than prose. The reports are now a model's,
    /// which changes their length, their vocabulary and therefore where they rank — the run prints
    /// the rank it actually measures on every pass, and that printed number is the one to read.
    /// What never depended on the stub is the structure: a few hundred general reports against tens
    /// of thousands of specific chunks, with nothing reserving them a slot.
    /// </para>
    /// </remarks>
    private async Task AssertGlobalSearchDiffersFromLocalAsync(
        GraphRagRun run, BeirQuery query, CancellationToken cancellationToken)
    {
        var local = await run.LocalSearchAsync(query.Text, cancellationToken);
        var rank = await run.FirstCommunityReportRankAsync(query.Text, cancellationToken);

        var callsBefore = run.GlobalSearchCalls;
        var retrievalsBefore = run.RetrievalCalls;
        var global = await run.GlobalSearchAsync(query.Text, cancellationToken);

        _output.WriteLine(FormattableString.Invariant($"""
            global {query.Id}: local returned {local.Count} results
              over the SAME candidate set as local: {global.Count} results, {run.GlobalSearchCalls - callsBefore} map/reduce calls
              it reached its reports in {run.RetrievalCalls - retrievalsBefore} retrievals (2 = it went back for them itself)
              best community report ranks {rank} in an unfiltered scan of the whole store
            """));

        AssertGlobalSearchRan(run, query, global, callsBefore, rank);

        Assert.False(
            SameResults(local, global),
            FormattableString.Invariant($"""
                GLOBAL AND LOCAL SEARCH RETURNED THE SAME {global.Count} RESULTS for query
                {query.Id}, in the same order — one after a map-reduce over community reports, the
                other after a PageRank blend over entities. Two behaviors producing identical output
                means at least one of them did nothing to what it was given.
                """));
    }

    /// <summary>Asserts global search returned something and got there by doing its own work.</summary>
    private static void AssertGlobalSearchRan(
        GraphRagRun run,
        BeirQuery query,
        IReadOnlyList<SearchResult> global,
        long callsBefore,
        int rank)
    {
        Assert.True(
            global.Count > 0,
            $"GLOBAL SEARCH RETURNED NOTHING for query {query.Id}, which it cannot do by " +
            "construction — it returns the candidate set untouched when there is no community " +
            "report among them, so an empty result means the retrieval underneath it was empty.");

        Assert.True(
            run.GlobalSearchCalls > callsBefore,
            FormattableString.Invariant($"""
                GLOBAL SEARCH MADE NO MAP-REDUCE CALLS for query {query.Id}, over the same
                unfiltered candidate set local search gets. {run.CommunityReportCount} reports were
                embedded and indexed, and the best of them ranks {rank} in an unfiltered scan — far
                below the candidate cutoff, which is exactly why the behavior is supposed to go back
                for them with a metadata filter of its own rather than hope one turns up. So either
                that refetch stopped happening, the graph_type = community_report tag stopped being
                written, or the retrieval standing in for VectorStoreBehavior stopped honouring
                MetadataFilter.
                """));
    }

    /// <summary>The distinct documents a result list names, in score order, cut at the cutoff.</summary>
    private static List<string> TopDocuments(IReadOnlyList<SearchResult> results)
    {
        var documents = new List<string>(DocumentCutoff);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < results.Count && documents.Count < DocumentCutoff; i++)
        {
            var documentId = results[i].Chunk.DocumentId.Value;
            if (seen.Add(documentId))
            {
                documents.Add(documentId);
            }
        }

        return documents;
    }

    /// <summary>Reports whether any retrieved document carries a positive judgement.</summary>
    private static bool AnyRelevant(
        List<string> retrieved, IReadOnlyDictionary<string, int> judgements)
    {
        for (var i = 0; i < retrieved.Count; i++)
        {
            if (judgements.TryGetValue(retrieved[i], out var grade) && grade > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reports whether two result lists name the same chunks in the same order.</summary>
    private static bool SameResults(
        IReadOnlyList<SearchResult> left, IReadOnlyList<SearchResult> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(
                    left[i].Chunk.DocumentId.Value, right[i].Chunk.DocumentId.Value,
                    StringComparison.Ordinal)
                || left[i].Chunk.ChunkIndex != right[i].Chunk.ChunkIndex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Finds the entity extracted from the most distinct articles.</summary>
    private static (string Name, int Articles) MostRecurrentEntity(GraphRagRun run)
    {
        var best = string.Empty;
        var articles = 0;

        foreach (var (name, documents) in run.EntityDocuments)
        {
            if (documents.Count > articles)
            {
                best = name;
                articles = documents.Count;
            }
        }

        return (best, articles);
    }

    /// <summary>Counts communities holding a single entity.</summary>
    private static int CountSingletons(IReadOnlyList<Community> communities)
    {
        var singletons = 0;
        for (var i = 0; i < communities.Count; i++)
        {
            if (communities[i].MemberEntities.Count <= 1)
            {
                singletons++;
            }
        }

        return singletons;
    }

    /// <summary>What the run produced, printed before anything is asserted about it.</summary>
    private static string Describe(
        IReadOnlyList<BeirDocument> documents, IReadOnlyList<BeirQuery> queries, GraphRagRun run)
    {
        var (best, articles) = MostRecurrentEntity(run);

        return FormattableString.Invariant($"""
            === multihop-rag GRAPHRAG (slice) ===
            {documents.Count} articles, {queries.Count} judged queries, {run.ChunkCount} chunks
            {run.ReplayedRequests} extraction requests and {run.ReplayedReports} report requests, every one replayed from the cache
            graph: {run.Graph.Entities.Count} entities, {run.Graph.Relationships.Count} relationships
            entity recurrence: {run.EntityDocuments.Count} distinct names, most widespread "{best}" in {articles} articles
            communities: {run.Graph.Communities.Count}, of which {CountSingletons(run.Graph.Communities)} hold one entity
            largest community: {LargestCommunity(run)} entities, {(double)LargestCommunity(run) / run.Graph.Entities.Count:P1} of the graph (ceiling {LargestCommunityShareCeiling:P0})
            isolated entities: {IsolatedEntities(run)} of {run.Graph.Entities.Count} have no relationship -- recorded, not asserted; see the constant above
            indexed: {run.ChunkCount} article chunks, {run.GraphChunkCount} entity/relationship chunks, {run.CommunityReportCount} community reports ({run.ReplayedReports} replayed from the report cache, generated once against openai/gpt-4o-mini)
            largest community report prompt: {run.LongestReportPrompt} characters
            """);
    }

    /// <summary>How many entities no relationship in the graph names at either end.</summary>
    /// <remarks>
    /// <b>Printed and never asserted, deliberately.</b> These are 273 of the 396 entities Leiden
    /// turns into singleton communities — the other 123 have edges that <c>BuildAdjacency</c> drops,
    /// which the constant above works through — and they are a property of what extraction produced
    /// rather than of how the graph was clustered: a model naming a subject once, in one article,
    /// in one phrasing.
    /// A pass/fail bound would hide movement inside its band, and movement is the whole of what is
    /// interesting here — if an extraction change halves this number, that is a result worth
    /// seeing, and if it doubles it, that is a regression worth seeing, and neither is a reason for
    /// this file to go red.
    /// </remarks>
    private static int IsolatedEntities(GraphRagRun run)
    {
        var connected = new HashSet<string>(GraphNames.Comparer);
        var relationships = run.Graph.Relationships;

        for (var i = 0; i < relationships.Count; i++)
        {
            _ = connected.Add(relationships[i].SourceEntity);
            _ = connected.Add(relationships[i].TargetEntity);
        }

        var isolated = 0;
        var entities = run.Graph.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            isolated += connected.Contains(entities[i].Name) ? 0 : 1;
        }

        return isolated;
    }

    /// <summary>How many entities the largest community holds.</summary>
    private static int LargestCommunity(GraphRagRun run)
    {
        var largest = 0;
        for (var i = 0; i < run.Graph.Communities.Count; i++)
        {
            largest = Math.Max(largest, run.Graph.Communities[i].MemberEntities.Count);
        }

        return largest;
    }
}
