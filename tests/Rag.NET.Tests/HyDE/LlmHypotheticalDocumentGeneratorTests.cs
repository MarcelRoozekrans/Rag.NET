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
}
