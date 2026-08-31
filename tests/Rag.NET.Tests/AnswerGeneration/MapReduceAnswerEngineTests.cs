using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class MapReduceAnswerEngineTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly MapReduceAnswerEngine _sut;

    public MapReduceAnswerEngineTests()
    {
        _sut = new MapReduceAnswerEngine(_chatClient, NullLogger<MapReduceAnswerEngine>.Instance);
    }

    private static SearchResult MakeSource(string text, string docId = "doc-1") =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = 0 }, Score = 0.9 };

    private static ChatResponse ChatReply(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    [Fact]
    public async Task AskAsync_ThreeSources_MapsEachThenReduces()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
            MakeSource("chunk C", "doc-3"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("partial"), ChatReply("partial"), ChatReply("final answer"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(4).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("final answer", result.Answer);
        Assert.Same(sources, result.Sources);
    }

    [Fact]
    public async Task AskAsync_OneSourceReturnsNotFound_FilteredBeforeReduce()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        // First map returns "not found", second returns a partial answer
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("not found"), ChatReply("partial answer"), ChatReply("final"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        // 2 map calls + 1 reduce = 3 total
        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("final", result.Answer);
    }

    [Fact]
    public async Task AskAsync_AllSourcesReturnNotFound_ReduceStillCalled()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A"),
            MakeSource("chunk B"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("not found"), ChatReply("  NOT FOUND  "), ChatReply("no information available"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        // 2 map + 1 reduce = 3 calls; reduce receives empty partials
        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("no information available", result.Answer);
    }

    [Fact]
    public async Task AskAsync_MapCallThrows_ChunkSkippedAndWarningLogged()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                x => throw new InvalidOperationException("LLM error"),
                x => ChatReply("partial answer"),
                x => ChatReply("final answer"));

        // Should not throw — failed chunk treated as "not found"
        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("final answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(x => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.AskAsync("What?", sources, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsSourcesThenSingleTextDelta()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("map answer"), ChatReply("final answer"));

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(2, updates.Count);
        Assert.Same(sources, updates[0].Sources);
        Assert.Null(updates[0].TextDelta);
        Assert.Equal("final answer", updates[1].TextDelta);
        Assert.Null(updates[1].Sources);
    }

    [Fact]
    public async Task AskAsync_WithSystemPrompt_IncludesItInMessages()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        var opts = new RagOptions { SystemPrompt = "You are a helpful assistant." };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("final"));

        await _sut.AskAsync("What?", sources, opts, TestContext.Current.CancellationToken);

        // Both the map call and the reduce call carry the caller's system prompt.
        //
        // Containment rather than equality since 2026-08-30: map calls append MapProtocol after the
        // caller's prompt, so that a caller instruction about the shape of a reply cannot reshape
        // the "not found" sentinel the reduce filter matches exactly. This assertion's intent — the
        // caller's prompt reaches both steps — is unchanged; only its strictness is, and
        // AskAsync_WithACallerSystemPrompt_TellsTheMapsToKeepTheRefusalSentinel pins the difference
        // between the two steps precisely.
        await _chatClient.Received(2).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs!.Any(m =>
                m.Role == ChatRole.System
                && m.Text != null
                && m.Text.Contains("You are a helpful assistant.", StringComparison.Ordinal))),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_WithCustomPromptTemplates_UsesCustomTemplates()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        var opts = new RagOptions
        {
            MapReduceOptions = new MapReduceOptions
            {
                MapPromptTemplate = "Custom map: {chunk} Q: {query}",
                ReducePromptTemplate = "Custom reduce: {partials} Q: {query}",
            }
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("final"));

        var result = await _sut.AskAsync("my question", sources, opts, TestContext.Current.CancellationToken);

        Assert.Equal("final", result.Answer);

        // Verify the map call used the custom map template
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs!.Any(m => m.Text != null && m.Text.Contains("Custom map:"))),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        // Verify the reduce call used the custom reduce template
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs!.Any(m => m.Text != null && m.Text.Contains("Custom reduce:"))),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A caller system prompt must not be able to reshape the map step's refusal sentinel.
    /// </summary>
    /// <remarks>
    /// <b>Regression test for a defect measured with a transcript on 2026-08-30.</b> Map partials
    /// that say <c>not found</c> are dropped by an exact match before the reduce runs. Under a
    /// caller prompt ending "end your reply with exactly this sentence: The answer to the question
    /// is …", real maps returned <c>Not found. The answer to the question is "not found".</c> —
    /// which is not equal to <c>not found</c>, so three such refusals survived into the reduce. The
    /// reduce saw one correct answer against three refusals, called it a contradiction, and
    /// discarded the answer. The engine had the answer and threw it away.
    /// <para>
    /// The fix appends <c>MapProtocol</c> after the caller's prompt on map calls only, so the
    /// sentinel survives any caller formatting instruction. This asserts the instruction actually
    /// reaches the maps and stays out of the reduce — the reduce produces the reply the caller
    /// asked for, so their prompt applies there as written.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AskAsync_WithACallerSystemPrompt_TellsTheMapsToKeepTheRefusalSentinel()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A", "doc-1") };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("final answer"));

        var options = new RagOptions
        {
            SystemPrompt = "End your reply with exactly this sentence: The answer to the question is \"...\"",
        };

        _ = await _sut.AskAsync("What?", sources, options, TestContext.Current.CancellationToken);

        // The map call carries the caller's prompt AND the protocol that protects the sentinel.
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs!.Any(m =>
                m.Role == ChatRole.System
                && m.Text != null
                && m.Text.Contains("End your reply with exactly this sentence", StringComparison.Ordinal)
                && m.Text.Contains("reply with exactly: not found", StringComparison.Ordinal))),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        // The reduce call carries the caller's prompt alone — it produces the caller's reply.
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs!.Any(m =>
                m.Role == ChatRole.System
                && m.Text != null
                && m.Text.Contains("End your reply with exactly this sentence", StringComparison.Ordinal)
                && !m.Text.Contains("reply with exactly: not found", StringComparison.Ordinal))),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// With no caller system prompt, the map prompt is left exactly as it was.
    /// </summary>
    /// <remarks>
    /// Nothing can reshape the sentinel when the caller supplies no prompt, so adding the protocol
    /// would change the prompt — and therefore the output, and any prompt-keyed cache — for every
    /// existing caller in order to fix a problem they do not have.
    /// </remarks>
    [Fact]
    public async Task AskAsync_WithNoCallerSystemPrompt_SendsNoSystemMessageAtAll()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A", "doc-1") };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("final answer"));

        _ = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(2).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs!.All(m => m.Role != ChatRole.System)),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
