using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Rag.NET.GraphRag;
using Rag.NET.Models;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// The GraphRAG generation tool. Selects a corpus of MultiHop-RAG articles, chunks it exactly as
/// the guard will, drives one of GraphRAG's two LLM stages over it through OpenRouter, and writes
/// every response into <see cref="GraphExtractionCache"/>.
/// <para>
/// <b>The cached text is the experiment.</b> Hosted LLMs are not bit-deterministic even at
/// temperature 0, so the GraphRAG guard never calls a model: it reads what this tool wrote,
/// verbatim, forever. That is also why this tool is resumable — every response already on disk is a
/// hit and costs nothing — so an interrupted run continues rather than regenerating text it can
/// never reproduce.
/// </para>
/// <para>
/// <b>Two stages, and the default is the one the tool always did.</b> <c>--stage extraction</c>
/// drives <c>GraphEntityExtractionBehavior</c> over every chunk, a call plus a gleaning pass each.
/// <c>--stage reports</c> rebuilds the graph by replaying those extractions <b>refuse-on-miss</b> —
/// it can never extract anything itself — and then drives <c>CommunityDetectionBehavior</c> once
/// over the finished graph, one call per community, into the report directory of the same cache.
/// Both stages cost themselves against the cache before they spend anything.
/// </para>
/// <para>
/// <b>Two corpora, and the default is the one that is already paid for.</b>
/// <c>--corpus slice</c> — the default, and what an invocation with no arguments has always meant —
/// is the pinned sixty-article slice <c>GraphRagFunctionsTests</c> reads, guarded so that a walk
/// landing anywhere but exactly sixty is refused rather than extracted. <c>--corpus full</c> is
/// every article in the corpus: roughly 41,000 requests, hours of wall clock, and real money. The
/// slice is a subset of the full corpus and chunks identically inside it, so a full run re-uses
/// every extraction the slice already bought — <see cref="PrintPlanAsync"/> counts exactly how many
/// before anything is spent.
/// </para>
/// <para>
/// Usage:
/// <c>dotnet run [--stage extraction|reports] [--corpus slice|full] [--max-documents N] [--plan-only]</c>,
/// with <see cref="BeirDatasetCache.CacheDirectoryVariable"/> pointing at the BEIR cache and
/// <c>OPENROUTER_API_KEY</c> holding the key. The key is read from the environment and never
/// logged. <c>--max-documents</c> bounds either corpus and exists for the smoke run: verify the
/// plumbing on two or three articles before spending the whole budget. <c>--plan-only</c> prints
/// what a run would cost and stops without reading the key at all, which is how a number is
/// obtained on a machine that must not be able to spend.
/// </para>
/// <para>
/// <b>Documents run concurrently, chunks within a document do not.</b> The gleaning pass's prompt
/// embeds the previous extraction, so a document's two calls per chunk are inherently sequential;
/// articles are independent and there are dozens to hundreds of them. Each concurrent article gets
/// a throwaway in-memory graph store, because <c>SqliteGraphStore</c> holds one connection and is
/// not thread-safe — and because nothing this tool extracts is kept. The guard rebuilds the graph
/// from the cache, into one store, sequentially.
/// </para>
/// </summary>
internal static class Program
{
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    /// <summary>How many articles are extracted at once.</summary>
    /// <remarks>
    /// Sixty articles at roughly twenty chunks each and two calls per chunk is a few thousand
    /// sequential requests, which is hours. Twelve at a time is minutes and stays well inside
    /// OpenRouter's per-key concurrency for this model; the retry in
    /// <see cref="CachedGraphRagClient"/> absorbs the rate-limit responses that do arrive.
    /// <para>
    /// <b>It applies to extraction only.</b> The report stage's calls are made by
    /// <c>CommunityDetectionBehavior</c>, whose own bound is
    /// <c>GraphRagOptions.CommunityReportConcurrency</c> — until #226 that loop was sequential
    /// and a report run was its community count times one round trip. The report stage takes
    /// the library's default unless <see cref="GraphExtractionRunOptions.ReportConcurrency"/>
    /// overrides it, which exists so the bound can be measured against the provider rather than
    /// assumed; it never touches a prompt, so the cache keys are the guard's either way.
    /// </para>
    /// </remarks>
    private const int Concurrency = 12;

    public static async Task<int> Main(string[] args)
    {
        var options = GraphExtractionRunOptions.Parse(args);
        if (options is null)
        {
            await Console.Error.WriteLineAsync(GraphExtractionRunOptions.Usage);
            return 2;
        }

        var cacheRoot = BeirDatasetCache.ResolveCacheDirectoryFromEnvironment();
        if (cacheRoot is null)
        {
            await Console.Error.WriteLineAsync(
                $"Set {BeirDatasetCache.CacheDirectoryVariable} to the directory the BEIR datasets " +
                "and the extraction cache live in.");
            return 2;
        }

        // Not read at all in plan-only mode: a run that generates nothing needs no key, and a tool
        // that demanded one anyway would make "what would this cost" impossible to ask on a machine
        // that must not be able to spend.
        var apiKey = options.PlanOnly ? null : Environment.GetEnvironmentVariable(ApiKeyVariable);
        if (!options.PlanOnly && string.IsNullOrWhiteSpace(apiKey))
        {
            await Console.Error.WriteLineAsync(
                $"Set {ApiKeyVariable} to an OpenRouter API key. It is read from the environment " +
                "and never logged.");
            return 2;
        }

        return options.Stage switch
        {
            GraphRagGenerationStage.Extraction => await RunExtractionAsync(cacheRoot, apiKey, options),
            GraphRagGenerationStage.Reports => await RunReportsAsync(cacheRoot, apiKey, options),
            _ => throw new InvalidOperationException(
                $"There is no third stage, and {options.Stage} has no branch here. A stage that " +
                "fell through to whichever branch is written first would spend the budget on the " +
                "one nobody named."),
        };
    }

    /// <summary>The identity every entry this tool writes is salted with.</summary>
    /// <remarks>
    /// One string for both stages: the reports are generated by the same model at the same
    /// temperature as the extractions, and they are kept apart by their directory rather than by
    /// their identity — see <see cref="GraphExtractionCache.ReportsDirectoryName"/>.
    /// </remarks>
    private static string Identity =>
        GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature);

    /// <summary>Loads the corpus, fills the extraction cache, and reports what it cost.</summary>
    private static async Task<int> RunExtractionAsync(
        string cacheRoot, string? apiKey, GraphExtractionRunOptions options)
    {
        var selection = await LoadCorpusAsync(cacheRoot, options);
        var chunked = await ChunkAsync(selection.Documents);

        var cache = new GraphExtractionCache(cacheRoot, Identity, GraphExtractionCacheMode.Fill);
        await PrintPlanAsync(cache, selection, chunked);

        if (options.PlanOnly)
        {
            PrintPlanOnly();
            return 0;
        }

        using var model = CreateChatClient(apiKey!);
        using var client = new CachedGraphRagClient(
            cache, model, GraphExtractionModelIdentity.ExtractionTemperature);

        var startedAt = Stopwatch.GetTimestamp();
        await ExtractAllAsync(chunked, client, (completed, document) =>
            Report(completed, chunked.Count, client, document));
        await DescribeTheGraphAsync(chunked, client);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        PrintSummary(client, chunked, elapsed);
        return 0;
    }

    /// <summary>
    /// Rebuilds the whole run's graph into one store — every extraction a cache hit by now — and
    /// reports what it contains, including <b>what a community report would cost to generate</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No LLM calls: this is the printout the report stage is planned from.</b> It says how many
    /// communities the graph holds and how large their report prompts are before the entity
    /// descriptions are even bounded — the numbers that once said reports could not be generated at
    /// all. They could not: while <c>Leiden.BuildAggregatedEdges</c> discarded intra-community
    /// weight, one community held most of the graph and its prompt ran past any model's context.
    /// Fixing the clustering and bounding the prompt with
    /// <c>GraphRagOptions.MaxCommunityReportPromptLength</c> changed that, which is why
    /// <c>--stage reports</c> exists; this remains the cheapest place to see the shape regress.
    /// </para>
    /// <para>
    /// The rebuild is sequential and in corpus order, which matters: entity descriptions merge by
    /// concatenation, so ingestion order decides what every merged description says — and therefore
    /// what every report prompt, and every report cache key, is computed from. The guard and the
    /// report stage project the slice the same way.
    /// </para>
    /// </remarks>
    private static async Task DescribeTheGraphAsync(
        IReadOnlyList<ChunkedDocument> chunked, CachedGraphRagClient client)
    {
        await using var graphStore = new SqliteGraphStore(":memory:");
        using var embedder = new StubEmbeddingGenerator();

        var snapshot = await RebuildGraphAsync(chunked, client, graphStore, embedder);
        PrintGraph(snapshot, Leiden.Detect(snapshot));
    }

    /// <summary>
    /// Replays every article's extraction into <b>one</b> store, sequentially and in corpus order,
    /// and returns the finished graph.
    /// </summary>
    /// <param name="chunked">The articles and their chunks, in corpus order.</param>
    /// <param name="client">
    /// The cached client the extractions come from. The report stage hands it a refuse-on-miss
    /// cache and no model, so this cannot generate anything — a report stage that re-extracted
    /// would be describing a graph built out of two generation runs.
    /// </param>
    /// <param name="graphStore">The store the whole corpus is merged into.</param>
    /// <param name="embedder">Embeds the entity chunks nobody here reads.</param>
    /// <returns>The graph every community report is written from.</returns>
    /// <remarks>
    /// Sequential and in corpus order because entity descriptions merge by concatenation: the order
    /// documents arrive in decides what every merged description says, and those descriptions are
    /// inside the report prompts the cache is keyed on. <c>ExtractAllAsync</c>'s concurrency is
    /// safe only because it gives each article a throwaway store; here there is one graph, and its
    /// contents have to match the guard's byte for byte.
    /// </remarks>
    private static async Task<GraphSnapshot> RebuildGraphAsync(
        IReadOnlyList<ChunkedDocument> chunked,
        CachedGraphRagClient client,
        IGraphStore graphStore,
        StubEmbeddingGenerator embedder)
    {
        var options = GraphRagSliceIngestion.CreateOptions();

        for (var i = 0; i < chunked.Count; i++)
        {
            _ = await GraphRagSliceIngestion.ExtractAsync(
                chunked[i].Document, chunked[i].Chunks, client, embedder, graphStore, options,
                CancellationToken.None);
        }

        return await graphStore.GetFullGraphAsync();
    }

    /// <summary>
    /// Rebuilds the graph from cached extractions and generates the community report the cache is
    /// missing for each community — <b>and nothing else</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Extraction is replayed refuse-on-miss, deliberately without a model.</b> The reports
    /// describe a specific graph, and a stage that could quietly extract an article the cache does
    /// not cover would write reports about a graph nothing can rebuild — the guard would then
    /// replay reports whose communities it cannot reproduce, and every miss would look like an
    /// empty cache. Missing extractions must be an error here, and are.
    /// </para>
    /// <para>
    /// <b>Community detection runs once over the finished graph, through the same shared driver the
    /// guard uses.</b> The cache key is the rendered report prompt, so a second copy that built the
    /// prompt from its own idea of the graph would compute keys nothing ever wrote under.
    /// </para>
    /// </remarks>
    private static async Task<int> RunReportsAsync(
        string cacheRoot, string? apiKey, GraphExtractionRunOptions options)
    {
        var selection = await LoadCorpusAsync(cacheRoot, options);
        var chunked = await ChunkAsync(selection.Documents);

        var extractions = new GraphExtractionCache(
            cacheRoot, Identity, GraphExtractionCacheMode.RefuseOnMiss);
        var reports = new GraphExtractionCache(
            cacheRoot, Identity, GraphExtractionCacheMode.Fill,
            GraphExtractionCache.ReportsDirectoryName);

        await using var graphStore = new SqliteGraphStore(":memory:");
        using var embedder = new StubEmbeddingGenerator();
        using var replay = new CachedGraphRagClient(
            extractions, inner: null, GraphExtractionModelIdentity.ExtractionTemperature);

        Console.WriteLine(FormattableString.Invariant(
            $"Rebuilding the graph from {extractions.EntryDirectory}, refuse-on-miss — no model, no extraction…"));
        var snapshot = await RebuildGraphAsync(chunked, replay, graphStore, embedder);
        Console.WriteLine(FormattableString.Invariant(
            $"Graph: {snapshot.Entities.Count} entities, {snapshot.Relationships.Count} relationships, from {replay.Calls} replayed extraction requests."));

        var graphRagOptions = CreateReportOptions(options);
        var planned = await PrintReportPlanAsync(reports, graphStore, embedder, graphRagOptions);
        if (options.PlanOnly)
        {
            PrintPlanOnly();
            return 0;
        }

        return await GenerateReportsAsync(
            reports, graphStore, embedder, apiKey!, planned, graphRagOptions);
    }

    /// <summary>
    /// The guard's own options, with the report concurrency overridden when the command line asks.
    /// </summary>
    /// <remarks>
    /// The override changes how many calls are in flight and nothing about any prompt, so the
    /// cache keys this run writes under are the keys the guard computes at any concurrency —
    /// <c>CommunityDetectionBehavior</c> builds every prompt before it sends one, in the same
    /// order regardless of the bound. Anything else about the options stays the guard's, because
    /// a report written from a differently configured graph would be replayed by a guard that
    /// cannot rebuild it.
    /// </remarks>
    private static GraphRagOptions CreateReportOptions(GraphExtractionRunOptions runOptions)
    {
        var options = GraphRagSliceIngestion.CreateOptions();
        if (runOptions.ReportConcurrency is { } concurrency)
        {
            options.CommunityReportConcurrency = concurrency;
        }

        return options;
    }

    /// <summary>Says why the run stopped where it did, so a plan is never mistaken for a failure.</summary>
    private static void PrintPlanOnly() =>
        Console.WriteLine(
            $"{GraphExtractionRunOptions.PlanOnlyOption}: nothing was generated, no model was " +
            $"constructed and {ApiKeyVariable} was never read. Re-run without the flag to spend " +
            "what the plan above states.");

    /// <summary>
    /// States what the report run will do <b>before it does any of it</b>: how many communities the
    /// graph holds, how many of their reports are already on disk, and how many this run pays for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is counted rather than derived, by running the real detection pass against
    /// <see cref="GraphReportPlanProbe"/>, which calls no model. A community's prompt is what its
    /// key is computed from, and which of its members fit inside
    /// <c>GraphRagOptions.MaxCommunityReportPromptLength</c> is decided inside the behavior — so
    /// anything short of building the prompts would be guessing at every key.
    /// </para>
    /// <para>
    /// <b>The pass leaves the graph store as it found it, in every way that matters here.</b> It
    /// writes PageRank scores and a set of placeholder reports, both of which the paying pass
    /// immediately overwrites; what it cannot change is the entities and relationships the prompts
    /// are built from, so the keys it counted are the keys the next pass computes.
    /// </para>
    /// </remarks>
    /// <returns>How many reports this run will generate.</returns>
    private static async Task<long> PrintReportPlanAsync(
        GraphExtractionCache reports,
        IGraphStore graphStore,
        StubEmbeddingGenerator embedder,
        GraphRagOptions options)
    {
        Console.WriteLine("Costing the plan against the report cache — no model calls…");

        using var probe = new GraphReportPlanProbe(reports);
        _ = await GraphRagSliceIngestion.DetectCommunitiesAsync(
            probe, embedder, graphStore, options, CancellationToken.None);

        Console.WriteLine(FormattableString.Invariant(
            $"Plan: {probe.Communities} communities, one report each. Already cached: {probe.Cached}. This run generates {probe.Uncached} and pays for those only."));
        Console.WriteLine(FormattableString.Invariant(
            $"CommunityDetectionBehavior keeps {options.CommunityReportConcurrency} report call(s) in flight (GraphRagOptions.CommunityReportConcurrency; {GraphExtractionRunOptions.ReportConcurrencyOption} overrides it), so budget roughly {probe.Uncached} / {options.CommunityReportConcurrency} round trips end to end — the provider's rate limit is the real ceiling."));
        Console.WriteLine(FormattableString.Invariant(
            $"Cache identity {reports.ModelIdentity}, entries in {reports.EntryDirectory}."));

        return probe.Uncached;
    }

    /// <summary>Generates the missing reports and prints what the run cost.</summary>
    /// <remarks>
    /// The rate it prints — seconds per <b>generated</b> report — is the figure that measures the
    /// concurrency bound against the provider: cached reports cost nothing and would flatter it,
    /// so they are excluded from the divisor and named separately.
    /// </remarks>
    private static async Task<int> GenerateReportsAsync(
        GraphExtractionCache reports,
        IGraphStore graphStore,
        StubEmbeddingGenerator embedder,
        string apiKey,
        long planned,
        GraphRagOptions options)
    {
        using var model = CreateChatClient(apiKey);
        using var client = new CachedGraphRagClient(
            reports, model, GraphExtractionModelIdentity.ExtractionTemperature);
        using var progress = new ReportProgressChatClient(client, planned);

        var startedAt = Stopwatch.GetTimestamp();
        var generated = await GraphRagSliceIngestion.DetectCommunitiesAsync(
            progress, embedder, graphStore, options, CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Console.WriteLine(FormattableString.Invariant(
            $"Done: {client.Calls} report requests over {generated.Count} communities — {reports.Hits} served from cache, {reports.Misses} generated — in {elapsed.TotalSeconds:F1} s at {options.CommunityReportConcurrency} in flight."));
        if (reports.Misses > 0)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"Rate: {elapsed.TotalSeconds / reports.Misses:F2} s per generated report ({reports.Misses} generated, cached ones excluded); {client.Retries} rate-limit or transient retries."));
        }

        Console.WriteLine(FormattableString.Invariant(
            $"Longest report prompt this run built: {client.LongestPrompt} characters, against the {options.MaxCommunityReportPromptLength}-character bound."));

        // Issue #200: what this run actually cost, in the run's own output rather than reasoned
        // backwards from cache-file sizes afterwards.
        Console.WriteLine(client.DescribeUsage());
        return 0;
    }

    /// <summary>Prints the graph's shape and the community-report prompt sizes it implies.</summary>
    private static void PrintGraph(GraphSnapshot snapshot, IReadOnlyList<Community> communities)
    {
        var singletons = 0;
        var largest = 0;
        var longestPrompt = 0L;
        var totalPrompt = 0L;

        var descriptions = new Dictionary<string, int>(snapshot.Entities.Count, StringComparer.Ordinal);
        for (var i = 0; i < snapshot.Entities.Count; i++)
        {
            descriptions[snapshot.Entities[i].Name] = snapshot.Entities[i].Description.Length;
        }

        for (var i = 0; i < communities.Count; i++)
        {
            var members = communities[i].MemberEntities;
            singletons += members.Count <= 1 ? 1 : 0;
            largest = Math.Max(largest, members.Count);

            var prompt = 0L;
            for (var j = 0; j < members.Count; j++)
            {
                prompt += descriptions.GetValueOrDefault(members[j]) + members[j].Length;
            }

            longestPrompt = Math.Max(longestPrompt, prompt);
            totalPrompt += prompt;
        }

        Console.WriteLine(FormattableString.Invariant(
            $"Graph: {snapshot.Entities.Count} entities, {snapshot.Relationships.Count} relationships, {communities.Count} communities ({singletons} singletons, largest holds {largest})."));
        Console.WriteLine(FormattableString.Invariant(
            $"Community report prompts: {totalPrompt} characters in total, longest {longestPrompt} — entity descriptions alone, before the relationship block and the instructions."));
    }

    /// <summary>Runs every article's extraction, <see cref="Concurrency"/> at a time.</summary>
    /// <param name="chunked">The articles and their chunks.</param>
    /// <param name="client">
    /// What answers each request: the caching client for the run that pays, or
    /// <see cref="GraphExtractionPlanProbe"/> for the dry pass that costs it first. One method for
    /// both, because a plan produced by a second walk over the chunks would be a plan for a
    /// different run than the one about to start.
    /// </param>
    /// <param name="report">Called as each article finishes, or <see langword="null"/> to say nothing.</param>
    private static Task ExtractAllAsync(
        IReadOnlyList<ChunkedDocument> chunked,
        IChatClient client,
        Action<int, ChunkedDocument>? report)
    {
        var options = GraphRagSliceIngestion.CreateOptions();
        var completed = 0;

        return Parallel.ForEachAsync(
            chunked,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency },
            async (document, cancellationToken) =>
            {
                await using var graphStore = new SqliteGraphStore(":memory:");
                using var embedder = new StubEmbeddingGenerator();

                _ = await GraphRagSliceIngestion.ExtractAsync(
                    document.Document, document.Chunks, client, embedder, graphStore, options,
                    cancellationToken);

                report?.Invoke(Interlocked.Increment(ref completed), document);
            });
    }

    /// <summary>Prints one article's completion, with the running cache tallies.</summary>
    private static void Report(
        int completed, int total, CachedGraphRagClient client, ChunkedDocument document) =>
        Console.WriteLine(FormattableString.Invariant(
            $"[{completed}/{total}] {document.Chunks.Count} chunks — {document.Document.Id} (totals: {client.Cache.Hits} cached, {client.Cache.Misses} generated)"));

    /// <summary>
    /// Loads the whole converted dataset and takes the articles the chosen corpus names.
    /// </summary>
    /// <remarks>
    /// <b>The dataset was always loaded whole.</b> <c>BeirLoader.Load</c> reads every article the
    /// conversion wrote; what used to happen next was an unconditional filter down to the slice.
    /// The selection below is that filter made optional — same load, same order, same chunking —
    /// which is why the slice's cached extractions are still cache hits inside a full run.
    /// </remarks>
    /// <exception cref="InvalidDataException">The slice walk did not reach its target.</exception>
    private static async Task<GraphExtractionCorpusSelection> LoadCorpusAsync(
        string cacheRoot, GraphExtractionRunOptions options)
    {
        var descriptor = BeirDatasetDescriptor.ByName(MultiHopRagSource.DatasetName);
        var directory = await new BeirDatasetCache(cacheRoot).EnsureAsync(descriptor);
        var dataset = BeirLoader.Load(directory, "test", BeirLoader.DefaultTitleTextSeparator);

        var selection = GraphExtractionCorpusSelection.Select(
            dataset, options.Corpus, options.MaxDocuments);

        Console.WriteLine(selection.Describe());
        return selection;
    }

    /// <summary>Chunks every article up front, so the plan can state what the run will cost.</summary>
    private static async Task<IReadOnlyList<ChunkedDocument>> ChunkAsync(
        IReadOnlyList<BeirDocument> documents)
    {
        var chunked = new List<ChunkedDocument>(documents.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            chunked.Add(new ChunkedDocument(
                documents[i],
                await GraphRagSliceIngestion.ChunkAsync(documents[i], CancellationToken.None)));
        }

        return chunked;
    }

    /// <summary>
    /// States what the run will do <b>before it does any of it</b>: how many articles, how many
    /// chunks, how many requests that implies, and how many of those requests are already on disk
    /// and therefore free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The last of those four is the one that decides anything.</b> A full-corpus run is hours
    /// of wall clock and a bill, and most of the difference between "this costs twelve dollars" and
    /// "this costs ten" is how much of the corpus a previous run already bought — the sixty slice
    /// articles chunk identically inside the full corpus, so their extractions are hits. Printing
    /// the estimate after the spend would be a receipt; printing it here is a decision.
    /// </para>
    /// <para>
    /// It is counted rather than derived, by replaying the whole extraction against
    /// <see cref="GraphExtractionPlanProbe"/>, which calls no model. Only the first request of each
    /// chunk has a key that can be computed up front; the gleaning follow-up's prompt embeds the
    /// previous extraction, so anything short of walking the chain would be guessing at half of the
    /// requests.
    /// </para>
    /// </remarks>
    private static async Task PrintPlanAsync(
        GraphExtractionCache cache,
        GraphExtractionCorpusSelection selection,
        IReadOnlyList<ChunkedDocument> chunked)
    {
        var chunks = CountChunks(chunked);
        var callsPerChunk = 1 + GraphRagSliceIngestion.CreateOptions().GleaningPasses;
        var requests = (long)chunks * callsPerChunk;

        Console.WriteLine(FormattableString.Invariant(
            $"Plan: {chunked.Count} articles, {chunks} chunks, {callsPerChunk} calls per chunk = {requests} requests at most."));
        Console.WriteLine(FormattableString.Invariant(
            $"Costing the plan against the cache — {selection.Corpus} corpus, no model calls…"));

        var (cached, uncached) = await ProbeCacheAsync(cache, chunked);
        Console.WriteLine(FormattableString.Invariant(
            $"Already cached: {cached} of {cached + uncached} requests. This run generates {uncached} and pays for those only."));
        Console.WriteLine(FormattableString.Invariant(
            $"Cache identity {cache.ModelIdentity}, entries in {cache.EntryDirectory}."));
    }

    /// <summary>Replays the whole extraction against the cache alone, counting hits and misses.</summary>
    private static async Task<(long Cached, long Uncached)> ProbeCacheAsync(
        GraphExtractionCache cache, IReadOnlyList<ChunkedDocument> chunked)
    {
        using var probe = new GraphExtractionPlanProbe(cache);
        await ExtractAllAsync(chunked, probe, report: null);

        return (probe.Cached, probe.Uncached);
    }

    /// <summary>How many chunks the selected articles were cut into, in total.</summary>
    private static int CountChunks(IReadOnlyList<ChunkedDocument> chunked)
    {
        var chunks = 0;
        for (var i = 0; i < chunked.Count; i++)
        {
            chunks += chunked[i].Chunks.Count;
        }

        return chunks;
    }

    private static void PrintSummary(
        CachedGraphRagClient client, IReadOnlyList<ChunkedDocument> chunked, TimeSpan elapsed)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"Done: {client.Calls} requests over {chunked.Count} articles — {client.Cache.Hits} served from cache, {client.Cache.Misses} generated — in {elapsed.TotalSeconds:F1} s."));
        // Issue #200: the extraction stage's own cost, printed by the run that incurred it.
        Console.WriteLine(client.DescribeUsage());
    }

    private static IChatClient CreateChatClient(string apiKey) =>
        new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = OpenRouterEndpoint })
            .GetChatClient(GraphExtractionModelIdentity.ModelName)
            .AsIChatClient();

    /// <summary>One article and the chunks the library's default strategy cut it into.</summary>
    private sealed record ChunkedDocument(BeirDocument Document, IReadOnlyList<TextChunk> Chunks);
}
