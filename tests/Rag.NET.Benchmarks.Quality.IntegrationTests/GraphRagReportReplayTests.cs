using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Graph;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The community-report cache, end to end, over a graph small enough to fit in this file: generate
/// once against a deterministic fake, then replay refuse-on-miss and get the same reports back with
/// nothing generated. <b>No LLM, no network, no dataset, no ONNX model.</b>
/// <para>
/// <b>It is the property the whole of #172 rests on, and it is not obvious.</b> Caching a stage
/// only works if the same graph produces the same prompts twice, and a report prompt is built from
/// a community's members in PageRank order — where entities with no edges all share the flat
/// baseline score, so without the ordinal tie-break in
/// <c>CommunityDetectionBehavior.OrderByCentrality</c> the order would fall back to whatever the
/// store returned. That would not fail loudly. It would miss on some keys, some of the time, on
/// some machines. Here the second pass runs against a cache that <i>cannot</i> generate, so a
/// prompt that differs by one character fails the test by name.
/// </para>
/// <para>
/// It lives beside <see cref="GraphRagFunctionsTests"/> because it needs both the generation tool's
/// driver and the GraphRAG package, and it needs neither of that test's gates: the guard replays a
/// cache a machine may not have, while this one fills its own in a temporary directory.
/// </para>
/// </summary>
public sealed class GraphRagReportReplayTests : IDisposable
{
    private const string Identity = "fake-model@t0.0";

    private readonly string _root;

    public GraphRagReportReplayTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ragnet-graph-reports-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
            }

            Thread.Sleep(50);
        }
    }

    [Fact]
    public async Task ReportsGeneratedOnce_ReplayFromTheCache_WithoutReachingTheModel()
    {
        var model = new CountingChatClient();
        var generated = await DetectAsync(Fill(), model);

        Assert.NotEmpty(generated);
        Assert.Equal(generated.Count, model.Calls);
        Assert.All(generated, report => Assert.StartsWith("REPORT", report, StringComparison.Ordinal));

        // The second pass builds the same graph from scratch and is handed no model at all, so
        // every one of these texts came off disk under a key the first pass computed.
        var replayed = await DetectAsync(Refuse(), model: null);

        Assert.Equal(generated, replayed);
        Assert.Equal(generated.Count, model.Calls);
    }

    [Fact]
    public async Task AnEmptyReportCache_FailsRefuseOnMiss_NamingTheKeyAndTheStageThatFillsIt()
    {
        // What the GraphRAG guard does on a machine where the extractions were generated and the
        // reports were not. The message has to distinguish that from an empty cache, because the
        // two are one command apart and nothing else in the run can tell them apart.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DetectAsync(Refuse(), model: null));

        Assert.Contains("No cached GraphRAG response", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            GraphExtractionCache.ReportsDirectoryName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("--stage reports", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingTheGraph_MissesRatherThanReusingAReportAboutTheOldOne()
    {
        // The property that makes a stale cache loud. A report is a summary of specific entities,
        // so an entity whose description changed must not be summarised by text written before it
        // did — the key is the rendered prompt, and the prompt carries the descriptions verbatim.
        var model = new CountingChatClient();
        _ = await DetectAsync(Fill(), model);
        var afterFirst = model.Calls;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DetectAsync(Refuse(), model: null, descriptionSuffix: " (revised)"));

        Assert.Contains("No cached GraphRAG response", exception.Message, StringComparison.Ordinal);
        Assert.Equal(afterFirst, model.Calls);
    }

    [Fact]
    public async Task TheLongestPromptIsMeasured_OnAReplayRunToo()
    {
        // LongestReportPrompt is a deliberately tracked figure, and it moved onto this client when
        // the reports stopped being echoed. Measuring it only when the model is called would make
        // it read zero on every run the guard makes.
        var model = new CountingChatClient();
        using var filling = new CachedGraphRagClient(Fill(), model, temperature: 0f);
        _ = await DetectAsync(filling);

        using var replaying = new CachedGraphRagClient(Refuse(), inner: null, temperature: 0f);
        _ = await DetectAsync(replaying);

        Assert.True(filling.LongestPrompt > 0);
        Assert.Equal(filling.LongestPrompt, replaying.LongestPrompt);
    }

    [Fact]
    public async Task ThePlanProbe_CountsTheKeysThePayingPassThenComputes()
    {
        // The tool costs a report run by driving detection against GraphReportPlanProbe and then
        // driving it again, over the SAME graph store, against the real client. That second pass
        // runs after the probe has written PageRank scores and a set of placeholder reports, so
        // "the plan counted the keys the run computes" is a claim about a mutated store — and if it
        // were false, the plan would be a number about a run nobody makes.
        await using var graphStore = new SqliteGraphStore(":memory:");
        using var embedder = new StubEmbeddingGenerator();
        var ct = TestContext.Current.CancellationToken;
        var options = GraphRagSliceIngestion.CreateOptions();

        await PopulateAsync(graphStore, descriptionSuffix: string.Empty, ct);

        using var probe = new GraphReportPlanProbe(Fill());
        _ = await GraphRagSliceIngestion.DetectCommunitiesAsync(
            probe, embedder, graphStore, options, ct);

        Assert.Equal(0, probe.Cached);
        Assert.True(probe.Uncached > 1);

        // The paying pass, on the store the probe just wrote to.
        var model = new CountingChatClient();
        using var client = new CachedGraphRagClient(Fill(), model, temperature: 0f);
        _ = await GraphRagSliceIngestion.DetectCommunitiesAsync(
            client, embedder, graphStore, options, ct);

        Assert.Equal(probe.Uncached, model.Calls);

        // And a second plan over the now-full cache costs the run at nothing, which is the
        // statement a resumed run rests on.
        using var after = new GraphReportPlanProbe(Fill());
        _ = await GraphRagSliceIngestion.DetectCommunitiesAsync(
            after, embedder, graphStore, options, ct);

        Assert.Equal(probe.Uncached, after.Cached);
        Assert.Equal(0, after.Uncached);
    }

    /// <summary>Builds the graph, runs detection through a cached client, and returns the reports.</summary>
    private async Task<IReadOnlyList<string>> DetectAsync(
        GraphExtractionCache cache, IChatClient? model, string descriptionSuffix = "")
    {
        using var client = new CachedGraphRagClient(cache, model, temperature: 0f);

        return await DetectAsync(client, descriptionSuffix);
    }

    /// <summary>The same, for a caller that wants to inspect the client afterwards.</summary>
    private async Task<IReadOnlyList<string>> DetectAsync(
        CachedGraphRagClient client, string descriptionSuffix = "")
    {
        await using var graphStore = new SqliteGraphStore(":memory:");
        using var embedder = new StubEmbeddingGenerator();
        var ct = TestContext.Current.CancellationToken;

        await PopulateAsync(graphStore, descriptionSuffix, ct);

        _ = await GraphRagSliceIngestion.DetectCommunitiesAsync(
            client, embedder, graphStore, GraphRagSliceIngestion.CreateOptions(), ct);

        var snapshot = await graphStore.GetFullGraphAsync(ct);
        var reports = new List<string>(snapshot.Communities.Count);
        for (var i = 0; i < snapshot.Communities.Count; i++)
        {
            reports.Add(snapshot.Communities[i].ReportSummary ?? string.Empty);
        }

        return reports;
    }

    /// <summary>
    /// Two triangles joined by one edge: enough structure for Leiden to return more than one
    /// community, and small enough that every report prompt is readable.
    /// </summary>
    private static async Task PopulateAsync(
        IGraphStore graphStore, string descriptionSuffix, CancellationToken cancellationToken)
    {
        string[] left = ["Anna", "Bruno", "Carla"];
        string[] right = ["Diego", "Elena", "Farid"];

        var entities = new List<GraphEntity>(left.Length + right.Length);
        AddPeople(entities, left, descriptionSuffix);
        AddPeople(entities, right, descriptionSuffix);

        await graphStore.AddEntitiesAsync(entities, cancellationToken);
        await graphStore.AddRelationshipsAsync(
            [
                .. Clique(left),
                .. Clique(right),
                new GraphRelationship("Carla", "Diego", "know each other slightly", 0.1),
            ],
            cancellationToken);
    }

    /// <summary>Adds one entity per name, each with a description the report prompt will carry.</summary>
    private static void AddPeople(
        List<GraphEntity> entities, IReadOnlyList<string> names, string suffix)
    {
        for (var i = 0; i < names.Count; i++)
        {
            entities.Add(
                new GraphEntity(names[i], "PERSON", "A person named " + names[i] + suffix));
        }
    }

    /// <summary>Every ordered pair of three names, as heavy mutual edges.</summary>
    private static List<GraphRelationship> Clique(IReadOnlyList<string> names)
    {
        var relationships = new List<GraphRelationship>(names.Count * (names.Count - 1) / 2);
        for (var i = 0; i < names.Count; i++)
        {
            for (var j = i + 1; j < names.Count; j++)
            {
                relationships.Add(
                    new GraphRelationship(names[i], names[j], "work together closely", 5.0));
            }
        }

        return relationships;
    }

    // ── Token usage accounting (issue #200) ─────────────────────────────────

    /// <remarks>
    /// <para>
    /// <b>The instrument that tells a paid run what it cost, exercised before it is trusted.</b>
    /// #200's argument is that the 60-article run on 2026-08-12 made 4,088 requests and produced no
    /// cost figure at all, so the only estimate available was reasoned backwards from cache-file
    /// sizes on disk — "paying for a measurement and not getting one". An accounting bug reproduces
    /// that outcome exactly, and would only surface after the money was gone.
    /// </para>
    /// <para>
    /// Driven through the real <see cref="CachedGraphRagClient"/> rather than a copy of its
    /// arithmetic: the usage is recorded inside <c>CallOnceAsync</c>, which is only reached through
    /// a genuine cache miss and a live client, and a test of a mirrored implementation would prove
    /// only that the mirror adds up.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LiveCalls_AccumulateTheTokenUsageTheModelReported()
    {
        var model = new UsageReportingChatClient(input: 100, output: 20, total: 120);
        using var client = new CachedGraphRagClient(Fill(), model, temperature: 0f);

        _ = await DetectAsync(client, "-usage-accumulates");

        Assert.True(client.CallsWithUsage > 0, "the run made no live calls, so there is nothing to account for");
        Assert.Equal(model.Calls, client.CallsWithUsage);
        Assert.Equal(0, client.CallsWithoutUsage);
        Assert.Equal(model.Calls * 100, client.InputTokens);
        Assert.Equal(model.Calls * 20, client.OutputTokens);
        Assert.Equal(model.Calls * 120, client.TotalTokens);
    }

    /// <remarks>
    /// A replay run calls nothing, so it must report nothing rather than reporting a stale or
    /// inherited figure. This is the run shape every guard in this repository uses, so a cost line
    /// that lied here would be the one seen most often.
    /// </remarks>
    [Fact]
    public async Task AReplayRun_ReportsThatItCostNothing()
    {
        var model = new UsageReportingChatClient(input: 100, output: 20, total: 120);
        using var filling = new CachedGraphRagClient(Fill(), model, temperature: 0f);
        _ = await DetectAsync(filling, "-usage-replay");

        using var replaying = new CachedGraphRagClient(Refuse(), inner: null, temperature: 0f);
        _ = await DetectAsync(replaying, "-usage-replay");

        Assert.Equal(0, replaying.CallsWithUsage);
        Assert.Equal(0, replaying.TotalTokens);
        Assert.Contains("cost nothing", replaying.DescribeUsage(), StringComparison.Ordinal);
    }

    /// <remarks>
    /// The failure worth preventing: a response carrying no usage must not be counted as a
    /// zero-cost call. <c>ChatResponse.Usage</c> is optional in Microsoft.Extensions.AI and a
    /// provider may omit it — folding those in as zero understates the bill by exactly the amount
    /// nobody can see, which is #200's failure mode wearing a different hat.
    /// </remarks>
    [Fact]
    public async Task CallsReportingNoUsage_AreCountedSeparatelyAndWarnedAbout()
    {
        var model = new UsageReportingChatClient(input: 100, output: 20, total: 120, reportUsage: false);
        using var client = new CachedGraphRagClient(Fill(), model, temperature: 0f);

        _ = await DetectAsync(client, "-usage-absent");

        Assert.True(client.CallsWithoutUsage > 0);
        Assert.Equal(0, client.CallsWithUsage);
        Assert.Equal(0, client.TotalTokens);

        var described = client.DescribeUsage();
        Assert.Contains("WARNING", described, StringComparison.Ordinal);
        Assert.Contains("floor", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model that reports a fixed usage per call — optionally none at all.
    /// </summary>
    /// <remarks>
    /// Its text mirrors <see cref="CountingChatClient"/>'s so the community-report path accepts it;
    /// only the usage differs, which is the variable under test.
    /// </remarks>
    private sealed class UsageReportingChatClient(
        long input, long output, long total, bool reportUsage = true) : IChatClient
    {
        private long _calls;

        public long Calls => Interlocked.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            _ = Interlocked.Increment(ref _calls);

            var prompt = GraphExtractionPrompt.Render(new List<ChatMessage>(messages));
            var response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"report for {prompt.Length} characters"));

            if (reportUsage)
            {
                response.Usage = new UsageDetails
                {
                    InputTokenCount = input,
                    OutputTokenCount = output,
                    TotalTokenCount = total,
                };
            }

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Nothing in GraphRAG streams.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private GraphExtractionCache Fill() =>
        new(_root, Identity, GraphExtractionCacheMode.Fill,
            GraphExtractionCache.ReportsDirectoryName);

    private GraphExtractionCache Refuse() =>
        new(_root, Identity, GraphExtractionCacheMode.RefuseOnMiss,
            GraphExtractionCache.ReportsDirectoryName);

    /// <summary>
    /// A deterministic stand-in for the model: its answer is a function of the prompt alone, so a
    /// replayed report and a regenerated one are only equal if the prompt was reproduced exactly.
    /// </summary>
    /// <remarks>
    /// The call count is what makes "replayed" a measurement rather than a hope. A replay pass is
    /// constructed with no model at all, so this counter staying still is the second, independent
    /// statement that nothing was generated.
    /// </remarks>
    private sealed class CountingChatClient : IChatClient
    {
        private long _calls;

        public long Calls => Interlocked.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            _ = Interlocked.Increment(ref _calls);
            var prompt = GraphExtractionPrompt.Render(new List<ChatMessage>(messages));
            var text = FormattableString.Invariant(
                $"REPORT over a {prompt.Length}-character prompt ending \"{prompt[^24..]}\"");

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Nothing in GraphRAG streams.");

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
