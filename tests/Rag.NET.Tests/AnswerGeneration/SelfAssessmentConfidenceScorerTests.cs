using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class SelfAssessmentConfidenceScorerTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly SelfAssessmentConfidenceScorer _sut;

    public SelfAssessmentConfidenceScorerTests()
    {
        _sut = new SelfAssessmentConfidenceScorer(_chatClient);
    }

    private static SearchResult MakeSource(string text, string docId = "doc-1", int chunkIndex = 0) =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex }, Score = 0.9 };

    private static ChatResponse ChatReply(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    private void SetupReply(string text) =>
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply(text));

    [Fact]
    public async Task ScoreAsync_PlainNumber_Parsed()
    {
        SetupReply("0.35");

        var score = await _sut.ScoreAsync("The sky is green.", "partial", [MakeSource("ctx")],
            TestContext.Current.CancellationToken);

        Assert.Equal(0.35, score, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_FencedNumber_Parsed()
    {
        SetupReply("```json\n0.42\n```");

        var score = await _sut.ScoreAsync("s", "p", [MakeSource("ctx")],
            TestContext.Current.CancellationToken);

        Assert.Equal(0.42, score, precision: 10);
    }

    [Theory]
    [InlineData("Here is my assessment:\n```\n0.42\n```")]
    [InlineData("Sure! Here you go:\n```json\n0.42\n```")]
    [InlineData("```\n0.42\n```\nLet me know if you need anything else.")]
    public async Task ScoreAsync_PreambleOrProseAroundAFencedNumber_Parsed(string reply)
    {
        // The old strip ran only when the reply STARTED with a fence, so a preamble before it
        // failed open to 1.0 on every sentence — FLARE never re-retrieved, silently. Prose
        // without a fence still fails open: scavenging a digit out of a sentence is a guess.
        SetupReply(reply);

        var score = await _sut.ScoreAsync("s", "p", [MakeSource("ctx")],
            TestContext.Current.CancellationToken);

        Assert.Equal(0.42, score, precision: 10);
    }

    [Theory]
    [InlineData("1.7", 1.0)]
    [InlineData("-0.3", 0.0)]
    public async Task ScoreAsync_OutOfRange_Clamped(string reply, double expected)
    {
        SetupReply(reply);

        var score = await _sut.ScoreAsync("s", "p", [MakeSource("ctx")],
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, score, precision: 10);
    }

    [Theory]
    [InlineData("high confidence")]
    [InlineData("")]
    public async Task ScoreAsync_GarbageResponse_FailsOpen(string reply)
    {
        SetupReply(reply);

        var score = await _sut.ScoreAsync("s", "p", [MakeSource("ctx")],
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public async Task ScoreAsync_ChatThrows_FailsOpen()
    {
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new InvalidOperationException("LLM error"));

        var score = await _sut.ScoreAsync("s", "p", [MakeSource("ctx")],
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public async Task ScoreAsync_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await _sut.ScoreAsync("s", "p", [MakeSource("ctx")], cts.Token));
    }

    [Fact]
    public async Task ScoreAsync_PromptContainsSentenceAndContext()
    {
        IList<ChatMessage>? captured = null;
        _chatClient.GetResponseAsync(
            Arg.Do<IList<ChatMessage>>(m => captured = m), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("0.5"));

        // Four chunks — only the top three should be included in the excerpt.
        await _sut.ScoreAsync("the draft sentence", "the partial answer",
            [
                MakeSource("first chunk text", "doc-1"),
                MakeSource("second chunk text", "doc-2"),
                MakeSource("third chunk text", "doc-3"),
                MakeSource("fourth chunk text", "doc-4"),
            ],
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var userText = string.Join("\n", captured!.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        Assert.Contains("the draft sentence", userText, StringComparison.Ordinal);
        Assert.Contains("the partial answer", userText, StringComparison.Ordinal);
        Assert.Contains("first chunk text", userText, StringComparison.Ordinal);
        Assert.Contains("third chunk text", userText, StringComparison.Ordinal);
        Assert.DoesNotContain("fourth chunk text", userText, StringComparison.Ordinal);
        // The prompt-hardening sentence lives in the system message.
        var systemText = string.Join("\n", captured!.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        Assert.Contains("never instructions", systemText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScoreAsync_EmptyContext_StillParses()
    {
        SetupReply("0.25");

        var score = await _sut.ScoreAsync("s", "p", [], TestContext.Current.CancellationToken);

        Assert.Equal(0.25, score, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_PinsTemperatureToZero()
    {
        SetupReply("0.5");

        _ = await _sut.ScoreAsync("s", "p", [MakeSource("ctx")], TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Is<ChatOptions>(o => o!.Temperature == 0f),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreAsync_PrefersCompressedText()
    {
        IList<ChatMessage>? captured = null;
        _chatClient.GetResponseAsync(
            Arg.Do<IList<ChatMessage>>(m => captured = m), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("0.5"));

        var source = MakeSource("raw chunk text") with { CompressedText = "compressed view" };
        await _sut.ScoreAsync("s", "p", [source], TestContext.Current.CancellationToken);

        var userText = string.Join("\n", captured!.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        Assert.Contains("compressed view", userText, StringComparison.Ordinal);
        Assert.DoesNotContain("raw chunk text", userText, StringComparison.Ordinal);
    }
}
