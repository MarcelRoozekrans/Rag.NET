using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Storage;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class GraphGlobalSearchBehaviorTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    /// <summary>
    /// The store global search now reads community reports from (#247).
    /// </summary>
    /// <remarks>
    /// It used to re-enter the caller's pipeline with a <c>graph_type</c> filter when the candidate
    /// set held no reports. With the graph's chunks moved to their own store that second pass could
    /// only ever come back empty, so global search asks this store directly — cheaper, and it cannot
    /// silently return nothing the way the old path would have.
    /// </remarks>
    private readonly GraphChunkStore _chunkStore = new(new InMemoryVectorStore());

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    public GraphGlobalSearchBehaviorTests() =>
        _embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                new([new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })])));

    /// <summary>Puts a community report in the graph chunk store, where the behaviour looks.</summary>
    private async Task SeedReportAsync(string text) =>
        await _chunkStore.Store.StoreAsync(
        [
            new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = text,
                    DocumentId = new DocumentId("graphrag://communities"),
                    ChunkIndex = -1,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["graph_type"] = "community_report",
                    },
                },
                Embedding = new float[] { 0.1f, 0.2f, 0.3f },
            },
        ]);

    private static RetrievalContext CreateContext() => new()
    {
        Query = "What are the main themes?",
        Options = new RetrievalOptions(),
    };

    [Fact]
    public void StableSeed_IsAPureFunctionOfTheQuery_AndNotOfTheProcess()
    {
        // #241: the shuffle seeded from string.GetHashCode, which .NET randomises per process, so
        // the "deterministic" report order — and every map prompt built from it — changed on every
        // run. A hard-coded expected value is the only assertion a per-process seed cannot pass:
        // FNV-1a (32-bit) over the UTF-16 code units of "What are the main themes?".
        Assert.Equal(unchecked((int)0xFED538B4), GraphGlobalSearchBehavior.StableSeed("What are the main themes?"));
        Assert.Equal(
            GraphGlobalSearchBehavior.StableSeed("the same query"),
            GraphGlobalSearchBehavior.StableSeed("the same query"));
        Assert.NotEqual(
            GraphGlobalSearchBehavior.StableSeed("one query"),
            GraphGlobalSearchBehavior.StableSeed("another query"));
    }

    [Fact]
    public async Task HandleAsync_CollectsCommunityReportsAndMapReduces()
    {
        var options = new GraphRagGlobalSearchOptions { GlobalBatchSize = 5 };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options, _chunkStore, _embedder);
        var ctx = CreateContext();
        var results = CreateCommunityResults(3);

        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "partial answer")]));

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        // 3 reports in 1 batch (batch size 5) => 1 map call + 1 reduce call = 2 total
        await _chatClient.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RespectsGlobalBatchSize()
    {
        var options = new GraphRagGlobalSearchOptions { GlobalBatchSize = 2 };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options, _chunkStore, _embedder);
        var ctx = CreateContext();
        var results = CreateCommunityResults(4);

        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        // 4 reports / batch size 2 = 2 map calls + 1 reduce call = 3 total
        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoCommunityReports_ReturnsStandardResults()
    {
        var options = new GraphRagGlobalSearchOptions();
        var sut = new GraphGlobalSearchBehavior(_chatClient, options, _chunkStore, _embedder);
        var ctx = CreateContext();

        var results = (IReadOnlyList<SearchResult>)new List<SearchResult>
        {
            new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = "plain result",
                    DocumentId = new DocumentId("doc1"),
                    ChunkIndex = 0,
                },
                Score = 0.8,
            },
        }.AsReadOnly();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Single(actual);
        Assert.Equal(0.8, actual[0].Score);
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UsesGlobalChatClient()
    {
        var globalClient = Substitute.For<IChatClient>();
        var options = new GraphRagGlobalSearchOptions
        {
            GlobalBatchSize = 5,
            GlobalChatClient = globalClient,
        };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options, _chunkStore, _embedder);
        var ctx = CreateContext();
        var results = CreateCommunityResults(2);

        globalClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "global answer")]));

        await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        // The global client should be used, not the default one
        await globalClient.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());

        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ReducesToSingleResult()
    {
        var options = new GraphRagGlobalSearchOptions { GlobalBatchSize = 5 };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options, _chunkStore, _embedder);
        var ctx = CreateContext();

        // Mix community reports with a regular result
        var communityResults = CreateCommunityResults(2);
        var regularResult = new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = "regular chunk",
                DocumentId = new DocumentId("doc-regular"),
                ChunkIndex = 100,
            },
            Score = 0.6,
        };
        var results = (IReadOnlyList<SearchResult>)communityResults.Concat([regularResult]).ToList().AsReadOnly();

        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "synthesized final answer")]));

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        // First result should be the synthesized answer with score 1.0
        Assert.Equal(1.0, actual[0].Score);
        Assert.Equal("synthesized final answer", actual[0].Chunk.Text);
        Assert.Equal<MetadataValue>("global_answer", actual[0].Chunk.Metadata["graph_type"]);

        // The regular result should follow
        Assert.Equal(2, actual.Count);
        Assert.Equal("regular chunk", actual[1].Chunk.Text);
    }

    [Fact]
    public async Task HandleAsync_SingleCommunityReport_StillProcesses()
    {
        var options = new GraphRagGlobalSearchOptions { GlobalBatchSize = 5 };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options, _chunkStore, _embedder);
        var ctx = CreateContext();
        var results = CreateCommunityResults(1);

        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "single community answer")]));

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        // 1 report => 1 map call + 1 reduce call = 2 total
        await _chatClient.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());

        Assert.NotEmpty(actual);
        Assert.Equal("single community answer", actual[0].Chunk.Text);
    }

    /// <summary>
    /// When the candidate set holds no community report, global search fetches its own.
    /// </summary>
    /// <remarks>
    /// <b>Without this the behavior was unreachable through the pipeline's own retrieval, and it
    /// failed by doing nothing rather than by failing.</b> A corpus produces a few hundred long,
    /// general, multi-entity reports against tens of thousands of short, specific entity and
    /// article chunks, and nothing reserved the reports a slot — so the partition came back empty,
    /// map-reduce never ran, and the behavior returned its input untouched while looking to every
    /// caller as though it had worked. The stub here answers the unfiltered call the way the real
    /// store does, with no reports at all, and only yields them to a filtered one.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_CandidateSetHasNoReports_FetchesThemFromTheGraphChunkStore()
    {
        await SeedReportAsync("a community report");
        var sut = new GraphGlobalSearchBehavior(
            _chatClient, new GraphRagGlobalSearchOptions(), _chunkStore, _embedder);
        SetupChatClient("global answer");

        var calls = 0;
        var actual = await sut.HandleAsync(CreateContext(), CancellationToken.None, (c, _) =>
        {
            calls++;
            return ValueTask.FromResult(PlainResults());
        });

        // ONE pass through the caller's pipeline, not two. Since #247 the reports come from the
        // graph's own store, so the second trip through every downstream behaviour is gone — and
        // with it the failure mode where that trip returned nothing because the document store no
        // longer holds a report to filter for.
        Assert.Equal(1, calls);
        Assert.Equal("global answer", actual[0].Chunk.Text);
    }

    /// <summary>A candidate set that already holds reports is used as-is.</summary>
    /// <remarks>
    /// Reports can still arrive in the candidate set — a caller may retrieve from the graph chunk
    /// store deliberately — and when they do there is nothing to fetch.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_CandidateSetAlreadyHasReports_UsesThemWithoutFetching()
    {
        var sut = new GraphGlobalSearchBehavior(
            _chatClient, new GraphRagGlobalSearchOptions(), _chunkStore, _embedder);
        SetupChatClient("global answer");

        // Nothing seeded into the chunk store: if the behaviour went there anyway it would find no
        // reports and produce no answer, which is what this asserts against.
        var calls = 0;
        var actual = await sut.HandleAsync(CreateContext(), CancellationToken.None, (c, _) =>
        {
            calls++;
            return ValueTask.FromResult(CreateCommunityResults(2));
        });

        Assert.Equal(1, calls);
        Assert.Equal("global answer", actual[0].Chunk.Text);
    }

    /// <summary>The fetch honours the caller's own filter instead of replacing it.</summary>
    /// <remarks>
    /// A caller scoping retrieval to one tenant, source or language must not have global search
    /// quietly widen it back to the whole corpus. The store it now reads is a different store, and
    /// that changes nothing about this: only the graph-type key is added to whatever filter the
    /// caller set.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_TheReportFetchKeepsTheCallersOwnMetadataFilter()
    {
        await SeedReportAsync("acme report");
        var sut = new GraphGlobalSearchBehavior(
            _chatClient, new GraphRagGlobalSearchOptions(), _chunkStore, _embedder);
        SetupChatClient("global answer");

        var ctx = new RetrievalContext
        {
            Query = "What are the main themes?",
            Options = new RetrievalOptions
            {
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["tenant"] = "acme",
                },
            },
        };

        var actual = await sut.HandleAsync(ctx, CancellationToken.None,
            (c, _) => ValueTask.FromResult(PlainResults()));

        // The seeded report carries no tenant tag, so the caller's filter excludes it and there is
        // nothing to reduce — which is the assertion: had the filter been dropped, the report would
        // have been found and an answer produced.
        Assert.DoesNotContain(actual, r =>
            string.Equals(r.Chunk.Text, "global answer", StringComparison.Ordinal));
    }

    private static bool IsFilteredToReports(RetrievalContext ctx) =>
        ctx.Options.MetadataFilter is { } filter
        && filter.TryGetValue("graph_type", out var kind)
        && kind == (MetadataValue)"community_report";

    /// <summary>A candidate set with nothing a global search can reduce — the real-world case.</summary>
    private static IReadOnlyList<SearchResult> PlainResults() =>
        new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "an ordinary article chunk",
                    DocumentId = new DocumentId("doc1"),
                    ChunkIndex = 0,
                },
                Score = 0.8,
            },
        }.AsReadOnly();

    /// <summary>Answers every map and reduce call with the given text.</summary>
    private void SetupChatClient(string response) =>
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

    private static IReadOnlyList<SearchResult> CreateCommunityResults(int count)
    {
        var results = new List<SearchResult>();
        for (var i = 0; i < count; i++)
        {
            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = $"Community report {i} about various topics",
                    DocumentId = new DocumentId($"community-{i}"),
                    ChunkIndex = i,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["graph_type"] = "community_report",
                    },
                },
                Score = 0.7 - i * 0.1,
            });
        }

        return results.AsReadOnly();
    }
}
