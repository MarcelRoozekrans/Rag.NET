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
/// produced something; the something recurs across articles; the recurrence clustered; the clusters
/// are searchable against ground truth; global search does something different from local. Asked in
/// that order, the first red assertion names the stage that broke. Asked in any other order, a
/// failure at the end sends the reader looking for a retrieval defect in a pipeline whose graph was
/// empty all along.
/// </para>
/// <para>
/// <b>Nothing here is a quality measurement.</b> There is no nDCG, no comparison against the dense
/// baseline, and no claim that GraphRAG helps. That question needs the comparative run
/// <c>BeirReproduction</c>'s <c>multihop-rag</c> / <c>Real</c> entry is the anchor for, and it is
/// deliberately out of scope: "does it function" must not wait on "does it help".
/// </para>
/// <para>
/// <b>Deliberately not asserted: retrieval-mode routing.</b> The design intends a
/// <c>Mode</c> setting on <c>GraphRagRetrievalOptions</c> selecting local, global or automatic
/// search — issue #104. Neither the property nor a <c>GraphRagRetrievalMode</c> enum exists in the
/// package today, and neither behavior consults anything of the kind: <c>UseGraphRag</c> registers
/// both <c>GraphLocalSearchBehavior</c> and <c>GraphGlobalSearchBehavior</c> unconditionally, and
/// this guard therefore invokes each one directly. A test asserting that <c>Mode = Local</c> routes
/// to local search would not compile, and one written against a stub would be red on arrival. A
/// permanently failing test is not a guard; the assertion lands with #104's fix.
/// </para>
/// <para>
/// Gated like every other case here: applicability first (an inapplicable dataset must not blame
/// the environment), then provisioning, then <see cref="BeirRunBudget"/>. It additionally needs
/// <see cref="GraphExtractionCache"/> filled by the generation tool — and, exactly like the Hyde
/// cells, it FAILS refuse-on-miss rather than skipping when the cache is absent on a machine that
/// opted in.
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
    /// the share is the number that carries the meaning.</b> The clusterer legitimately emits one
    /// community per node it cannot attach to anything, so singletons are largely a property of the
    /// extraction rather than a defect, and a bound on them would fail for an honest reason and
    /// pass for a dishonest one. The two counts are printed side by side because they do not match
    /// and the gap is itself informative: 273 of this slice's entities appear in no relationship at
    /// all, while 396 communities hold one member — so some 123 entities have edges and still end
    /// up alone, their neighbours having been drawn elsewhere. What cannot be legitimate is one
    /// community swallowing the graph: at 89.7% — where this slice sat
    /// while <c>LouvainWithRefinement.BuildAggregatedEdges</c> discarded intra-community weight — every assertion
    /// about clustering below is satisfied while nothing has been clustered. The margin is wide on
    /// purpose: this is a degeneracy detector, not a quality target, and a ceiling set just above
    /// the measurement would go red on a corpus change that means nothing.
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

    /// <summary>Builds the graph and puts the five assertions to it, in order.</summary>
    private async Task RunTheGuardAsync(
        BeirDatasetDescriptor descriptor, string modelPath, string vocabPath, string cacheDirectory)
    {
        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);

        var documents = MultiHopRagSlice.Documents(dataset.Documents);
        var queries = MultiHopRagSlice.Queries(dataset.Queries);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        await using var run = await GraphRagSliceRun.BuildAsync(
            documents,
            generator,
            new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity),
            new GraphExtractionCache(
                cacheDirectory,
                GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
                GraphExtractionCacheMode.RefuseOnMiss),
            ct);

        _output.WriteLine(Describe(documents, queries, run));

        AssertExtractionProducedEntitiesAndRelationships(run);
        AssertEntitiesRecurAcrossArticles(run);
        AssertCommunityDetectionClusteredTheGraph(run);
        await AssertLocalSearchFindsRelevantDocumentsAsync(run, queries, dataset, ct);
        await AssertGlobalSearchDiffersFromLocalAsync(run, queries[0], ct);
    }

    /// <summary>Assertion 1: the extraction stage produced a graph at all.</summary>
    /// <remarks>
    /// Both halves, because they fail for different reasons. No entities means the extraction call
    /// returned nothing usable — an unparseable response, a provider error swallowed as an empty
    /// chunk — while entities without relationships means the model answered the entity half of
    /// the schema and not the relationship half, which leaves every later stage a graph of isolated
    /// nodes that the clusterer will faithfully report as one community per entity.
    /// </remarks>
    private static void AssertExtractionProducedEntitiesAndRelationships(GraphRagSliceRun run)
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
                graph has nodes and no edges, so the clusterer's adjacency is empty, every entity stays in
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
    /// produce sixty disconnected components — which the clusterer would return as communities, truthfully,
    /// while finding nothing. This is also the assertion that says the query-derived slice did its
    /// job: it was built that way precisely because a multi-hop question cites two to four articles
    /// that share subjects.
    /// </remarks>
    private static void AssertEntitiesRecurAcrossArticles(GraphRagSliceRun run)
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
    /// difference is a finding rather than a softening.</b> The clusterer emits one community per node it
    /// cannot attach to anything, which is correct behaviour and not a defect — but on this graph
    /// it does so 396 times out of 607. Demanding zero singletons would make this file red on
    /// arrival, so the numbers are printed on every run, what is known about them is written down
    /// here, and the assertion guards what it can.
    /// </para>
    /// <para>
    /// <b>Two defects were found underneath these numbers, and both are fixed.</b> First,
    /// <c>LouvainWithRefinement</c> and <c>PageRank</c> matched relationship endpoints to entity names with
    /// <c>StringComparer.Ordinal</c> while <c>SqliteGraphStore</c>'s <c>name</c> column is
    /// <c>COLLATE NOCASE</c>, so every endpoint whose casing differed from the entity it named was
    /// an edge the store held and the clusterer dropped; they now compare through
    /// <c>GraphNames.Comparer</c>. Recovering those edges moved 655 communities and 475 singletons
    /// to 565 and 396 — and <i>grew</i> the largest community from 7,954 to 8,070, which is what
    /// said the real cause lay elsewhere.
    /// </para>
    /// <para>
    /// It lay in aggregation. <c>LouvainWithRefinement.BuildAggregatedEdges</c> skipped edges whose endpoints fell
    /// in the same community instead of folding them into a self-loop on the super-node, so every
    /// level discarded the intra-community weight modularity's null model is computed from; each
    /// level saw communities far lighter than they were, merging always paid, and the recursion
    /// collapsed whatever was connected into one community. It reproduced with no corpus at all —
    /// ten 10-node cliques ring-bridged by one edge each returned <b>one</b> community of 100 — and
    /// <c>LouvainWithRefinementTests</c> now pins that case, along with the two- and three-clique cases whose
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
    private static void AssertCommunityDetectionClusteredTheGraph(GraphRagSliceRun run)
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
                community of one is the clusterer reporting an entity it could not attach to anything, not
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
                LouvainWithRefinement.BuildAggregatedEdges stopped discarding intra-community weight — so a number
                anywhere near the ceiling means the aggregation step has regressed to treating
                super-nodes as though they had no internal edges.
                """));
    }

    /// <summary>
    /// Assertion 4: local search retrieves a known-relevant document for a slice query.
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
        GraphRagSliceRun run,
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
    /// Assertion 5: global search returns results, actually maps over community reports, and
    /// produces a different set from local search's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two behaviors sitting on one retrieval must not be interchangeable, and "the two lists
    /// differ" is not enough on its own to establish that. It is satisfied by global search doing
    /// <b>nothing</b>: it returns its input untouched when the candidate set holds no community
    /// report, and local search's deduplication shortens the same list, so the counts differ while
    /// neither behavior has done any work. That is not a hypothetical — it is what the first run of
    /// this guard measured, and it is why the map-reduce call count is asserted rather than only
    /// printed.
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
    /// <b>On this slice the refetch does not actually fire, and the reason is worth recording.</b>
    /// Bounding the report prompt changed what the reports say: capped at 50,000 characters and
    /// filled in PageRank order, they now lead with their community's most central entities instead
    /// of an arbitrary prefix of all of them, and the best of them moved from rank 1,098 to 209 —
    /// inside the top-500, so the first retrieval already carries reports and the second is never
    /// needed. The refetch remains the safety net for a corpus where they do not surface, and
    /// <c>GraphGlobalSearchBehaviorTests</c> is where it is exercised directly. The run prints the
    /// retrieval count so which of the two paths ran is never a guess.
    /// </para>
    /// <para>
    /// Both of those rank figures are partly properties of this harness rather than of GraphRAG:
    /// <see cref="PromptEchoChatClient"/> answers with the first 2,000 characters of the prompt, so
    /// a "report" here is its community's own entity descriptions rather than the prose a model
    /// would return. What does not depend on the stub is the structure — a few hundred general
    /// reports against tens of thousands of specific chunks, with nothing reserving them a slot.
    /// </para>
    /// </remarks>
    private async Task AssertGlobalSearchDiffersFromLocalAsync(
        GraphRagSliceRun run, BeirQuery query, CancellationToken cancellationToken)
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
        GraphRagSliceRun run,
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
    private static (string Name, int Articles) MostRecurrentEntity(GraphRagSliceRun run)
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
        IReadOnlyList<BeirDocument> documents, IReadOnlyList<BeirQuery> queries, GraphRagSliceRun run)
    {
        var (best, articles) = MostRecurrentEntity(run);

        return FormattableString.Invariant($"""
            === multihop-rag GRAPHRAG (slice) ===
            {documents.Count} articles, {queries.Count} judged queries, {run.ChunkCount} chunks
            {run.ReplayedRequests} extraction and report requests, every one replayed from the cache
            graph: {run.Graph.Entities.Count} entities, {run.Graph.Relationships.Count} relationships
            entity recurrence: {run.EntityDocuments.Count} distinct names, most widespread "{best}" in {articles} articles
            communities: {run.Graph.Communities.Count}, of which {CountSingletons(run.Graph.Communities)} hold one entity
            largest community: {LargestCommunity(run)} entities, {(double)LargestCommunity(run) / run.Graph.Entities.Count:P1} of the graph (ceiling {LargestCommunityShareCeiling:P0})
            isolated entities: {IsolatedEntities(run)} of {run.Graph.Entities.Count} have no relationship -- recorded, not asserted; see the constant above
            indexed: {run.ChunkCount} article chunks, {run.GraphChunkCount} entity/relationship chunks, {run.CommunityReportCount} community reports ({run.SynthesisedReports} synthesised, not generated -- see PromptEchoChatClient)
            largest community report prompt: {run.LongestReportPrompt} characters
            """);
    }

    /// <summary>How many entities no relationship in the graph names at either end.</summary>
    /// <remarks>
    /// <b>Printed and never asserted, deliberately.</b> These are the entities the clusterer turns into
    /// singleton communities, and they are a property of what extraction produced rather than of
    /// how the graph was clustered: a model naming a subject once, in one article, in one phrasing.
    /// A pass/fail bound would hide movement inside its band, and movement is the whole of what is
    /// interesting here — if an extraction change halves this number, that is a result worth
    /// seeing, and if it doubles it, that is a regression worth seeing, and neither is a reason for
    /// this file to go red.
    /// </remarks>
    private static int IsolatedEntities(GraphRagSliceRun run)
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
    private static int LargestCommunity(GraphRagSliceRun run)
    {
        var largest = 0;
        for (var i = 0; i < run.Graph.Communities.Count; i++)
        {
            largest = Math.Max(largest, run.Graph.Communities[i].MemberEntities.Count);
        }

        return largest;
    }
}
