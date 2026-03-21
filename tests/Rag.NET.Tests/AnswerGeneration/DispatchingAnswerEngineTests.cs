using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class DispatchingAnswerEngineTests
{
    private readonly IAnswerEngine _chatEngine = Substitute.For<IAnswerEngine>();
    private readonly IAnswerEngine _mapReduceEngine = Substitute.For<IAnswerEngine>();
    private readonly IAnswerEngine _refineEngine = Substitute.For<IAnswerEngine>();
    private readonly DispatchingAnswerEngine _sut;

    public DispatchingAnswerEngineTests()
    {
        _sut = new DispatchingAnswerEngine(_chatEngine, _mapReduceEngine, _refineEngine);
    }

    private static IReadOnlyList<SearchResult> EmptySources() => Array.Empty<SearchResult>();

    private static RagResponse ReplyWith(string text) =>
        new() { Answer = text, Sources = EmptySources() };

    [Fact]
    public async Task AskAsync_DefaultStrategy_DelegatesToChatEngine()
    {
        var opts = new RagOptions { SynthesisStrategy = SynthesisStrategy.Default };
        _chatEngine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ReplyWith("chat answer"));

        var result = await _sut.AskAsync("q", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.Equal("chat answer", result.Answer);
        await _chatEngine.Received(1).AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
        await _mapReduceEngine.DidNotReceive().AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
        await _refineEngine.DidNotReceive().AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_NullOptions_DelegatesToChatEngine()
    {
        _chatEngine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ReplyWith("chat answer"));

        var result = await _sut.AskAsync("q", EmptySources(), null, TestContext.Current.CancellationToken);

        Assert.Equal("chat answer", result.Answer);
        await _chatEngine.Received(1).AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_MapReduceStrategy_DelegatesToMapReduceEngine()
    {
        var opts = new RagOptions { SynthesisStrategy = SynthesisStrategy.MapReduce };
        _mapReduceEngine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ReplyWith("mapreduce answer"));

        var result = await _sut.AskAsync("q", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.Equal("mapreduce answer", result.Answer);
        await _mapReduceEngine.Received(1).AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
        await _chatEngine.DidNotReceive().AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_RefineStrategy_DelegatesToRefineEngine()
    {
        var opts = new RagOptions { SynthesisStrategy = SynthesisStrategy.Refine };
        _refineEngine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ReplyWith("refine answer"));

        var result = await _sut.AskAsync("q", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.Equal("refine answer", result.Answer);
        await _refineEngine.Received(1).AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
        await _chatEngine.DidNotReceive().AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskStreamingAsync_MapReduceStrategy_DelegatesToMapReduceEngine()
    {
        var opts = new RagOptions { SynthesisStrategy = SynthesisStrategy.MapReduce };
        var sources = EmptySources();
        var updates = new List<RagStreamingUpdate>
        {
            new() { Sources = sources },
            new() { TextDelta = "mapreduce result" },
        };

        _mapReduceEngine.AskStreamingAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(updates.ToAsyncEnumerable());

        var received = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, opts, TestContext.Current.CancellationToken))
            received.Add(update);

        Assert.Equal(2, received.Count);
        Assert.Equal("mapreduce result", received[1].TextDelta);
    }

    [Fact]
    public async Task AskStreamingAsync_RefineStrategy_DelegatesToRefineEngine()
    {
        var opts = new RagOptions { SynthesisStrategy = SynthesisStrategy.Refine };
        var sources = EmptySources();
        var updates = new List<RagStreamingUpdate>
        {
            new() { Sources = sources },
            new() { TextDelta = "refine result" },
        };

        _refineEngine.AskStreamingAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(updates.ToAsyncEnumerable());

        var received = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, opts, TestContext.Current.CancellationToken))
            received.Add(update);

        Assert.Equal(2, received.Count);
        Assert.Equal("refine result", received[1].TextDelta);
    }

    [Fact]
    public async Task AskStreamingAsync_DefaultStrategy_DelegatesToChatEngine()
    {
        var sources = EmptySources();
        var updates = new List<RagStreamingUpdate>
        {
            new() { Sources = sources },
            new() { TextDelta = "chat result" },
        };

        _chatEngine.AskStreamingAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(updates.ToAsyncEnumerable());

        var received = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, null, TestContext.Current.CancellationToken))
            received.Add(update);

        Assert.Equal(2, received.Count);
        Assert.Equal("chat result", received[1].TextDelta);
    }
}
