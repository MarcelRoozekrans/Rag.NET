using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.HyDE;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.HyDE;

public class LlmHypotheticalDocumentGeneratorTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    [Fact]
    public async Task GenerateAsync_ReturnsLlmResponseAsHypotheticalDocument()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Retrieval-Augmented Generation is a technique...")]));

        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        var result = await sut.GenerateAsync("what is rag?", TestContext.Current.CancellationToken);

        Assert.Equal("Retrieval-Augmented Generation is a technique...", result);
    }

    [Fact]
    public async Task GenerateAsync_WhenLlmResponseTextIsNull_ReturnsEmptyString()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([]));

        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        var result = await sut.GenerateAsync("what is rag?", TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task GenerateAsync_InterpolatesQueryPlaceholder()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => capturedMessages = m),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        await sut.GenerateAsync("test query", TestContext.Current.CancellationToken);

        var prompt = capturedMessages!.Single().Text;
        Assert.Contains("test query", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{query}", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_WhenQueryIsNull_ThrowsArgumentNullException()
    {
        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.GenerateAsync(null!, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GenerateAsync_WhenQueryIsEmptyOrWhiteSpace_ThrowsArgumentException(string query)
    {
        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.GenerateAsync(query, TestContext.Current.CancellationToken));
    }

    // ── GenerateManyAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateManyAsync_ReturnsCountDocs()
    {
        var callCount = 0;
        ChatOptions? capturedOptions = null;
        _chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var n = Interlocked.Increment(ref callCount);
                return new ChatResponse([new ChatMessage(ChatRole.Assistant, $"hypothesis-{n}")]);
            });

        var sut = new LlmHypotheticalDocumentGenerator(
            _chatClient, new HydeOptions { HypothesisTemperature = 0.8f });

        var docs = await sut.GenerateManyAsync("what is rag?", 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, docs.Count);
        Assert.Equal(3, callCount);
        Assert.Equal(3, docs.Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(capturedOptions);
        Assert.Equal(0.8f, capturedOptions.Temperature);
    }

    [Fact]
    public async Task GenerateManyAsync_PartialFailure_ReturnsSurvivors()
    {
        var callCount = 0;
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n == 2)
                    throw new HttpRequestException("transient failure");
                return new ChatResponse([new ChatMessage(ChatRole.Assistant, $"hypothesis-{n}")]);
            });

        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        var docs = await sut.GenerateManyAsync("what is rag?", 3, TestContext.Current.CancellationToken);

        Assert.Equal(2, docs.Count);
    }

    [Fact]
    public async Task GenerateManyAsync_AllFail_Throws()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new HttpRequestException("model down"));

        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.GenerateManyAsync("what is rag?", 3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenerateAsync_DelegatesToSingle()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "single hypothesis")]));

        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        var result = await sut.GenerateAsync("what is rag?", TestContext.Current.CancellationToken);

        Assert.Equal("single hypothesis", result);
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GenerateManyAsync_InvalidCount_Throws(int count)
    {
        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sut.GenerateManyAsync("what is rag?", count, TestContext.Current.CancellationToken));
    }

    // ── Default interface method (external implementers without an override) ──

    /// <summary>
    /// Implements only <see cref="Rag.NET.Abstractions.IHypotheticalDocumentGenerator.GenerateAsync"/>,
    /// exercising the interface's default sequential GenerateManyAsync.
    /// </summary>
    private sealed class SingleDocOnlyGenerator(Func<int, string> responder) : Rag.NET.Abstractions.IHypotheticalDocumentGenerator
    {
        private int _calls;

        public Task<string> GenerateAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult(responder(Interlocked.Increment(ref _calls)));
    }

    [Fact]
    public async Task DefaultGenerateManyAsync_LoopsGenerateAsync_ReturnsCountDocs()
    {
        Rag.NET.Abstractions.IHypotheticalDocumentGenerator sut =
            new SingleDocOnlyGenerator(n => $"seq-doc-{n}");

        var docs = await sut.GenerateManyAsync("what is rag?", 3, TestContext.Current.CancellationToken);

        Assert.Equal(["seq-doc-1", "seq-doc-2", "seq-doc-3"], docs);
    }

    [Fact]
    public async Task DefaultGenerateManyAsync_PartialFailure_ReturnsSurvivors()
    {
        Rag.NET.Abstractions.IHypotheticalDocumentGenerator sut =
            new SingleDocOnlyGenerator(n => n == 2
                ? throw new HttpRequestException("transient failure")
                : $"seq-doc-{n}");

        var docs = await sut.GenerateManyAsync("what is rag?", 3, TestContext.Current.CancellationToken);

        Assert.Equal(["seq-doc-1", "seq-doc-3"], docs);
    }

    [Fact]
    public async Task DefaultGenerateManyAsync_AllFail_Throws()
    {
        Rag.NET.Abstractions.IHypotheticalDocumentGenerator sut =
            new SingleDocOnlyGenerator(_ => throw new HttpRequestException("model down"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.GenerateManyAsync("what is rag?", 3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DefaultGenerateManyAsync_DropsEmptyResponses()
    {
        Rag.NET.Abstractions.IHypotheticalDocumentGenerator sut =
            new SingleDocOnlyGenerator(n => n == 2 ? "   " : $"seq-doc-{n}");

        var docs = await sut.GenerateManyAsync("what is rag?", 3, TestContext.Current.CancellationToken);

        Assert.Equal(["seq-doc-1", "seq-doc-3"], docs);
    }
}
