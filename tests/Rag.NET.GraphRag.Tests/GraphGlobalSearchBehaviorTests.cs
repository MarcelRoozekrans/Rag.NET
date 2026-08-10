using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class GraphGlobalSearchBehaviorTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    private static RetrievalContext CreateContext() => new()
    {
        Query = "What are the main themes?",
        Options = new RetrievalOptions(),
    };

    [Fact]
    public async Task HandleAsync_CollectsCommunityReportsAndMapReduces()
    {
        var options = new GraphRagRetrievalOptions { GlobalBatchSize = 5 };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options);
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
        var options = new GraphRagRetrievalOptions { GlobalBatchSize = 2 };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options);
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
        var options = new GraphRagRetrievalOptions();
        var sut = new GraphGlobalSearchBehavior(_chatClient, options);
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
        var options = new GraphRagRetrievalOptions
        {
            GlobalBatchSize = 5,
            GlobalChatClient = globalClient,
        };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options);
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
        var options = new GraphRagRetrievalOptions { GlobalBatchSize = 5 };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options);
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
        var options = new GraphRagRetrievalOptions { GlobalBatchSize = 5 };
        var sut = new GraphGlobalSearchBehavior(_chatClient, options);
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
