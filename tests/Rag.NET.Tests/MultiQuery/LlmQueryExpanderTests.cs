using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
using Xunit;

namespace Rag.NET.Tests.MultiQuery;

public class LlmQueryExpanderTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    [Fact]
    public async Task ExpandAsync_ParsesLlmResponseIntoVariants()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "variant 1\nvariant 2\nvariant 3")]));

        var sut = new LlmQueryExpander(_chatClient, new MultiQueryOptions { VariantCount = 3 });

        var result = await sut.ExpandAsync("what is rag?", 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
        Assert.Equal("variant 1", result[0]);
        Assert.Equal("variant 2", result[1]);
        Assert.Equal("variant 3", result[2]);
    }

    [Fact]
    public async Task ExpandAsync_WhenLlmReturnsFewLines_ReturnsWhatItGot()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "only one variant")]));

        var sut = new LlmQueryExpander(_chatClient, new MultiQueryOptions { VariantCount = 3 });

        var result = await sut.ExpandAsync("what is rag?", 3, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("only one variant", result[0]);
    }

    [Fact]
    public async Task ExpandAsync_InterpolatesCountAndQueryIntoPrompt()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient
            .GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(m => capturedMessages = m), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "a")]));

        var sut = new LlmQueryExpander(_chatClient, new MultiQueryOptions { VariantCount = 3 });

        await sut.ExpandAsync("test query", 3, TestContext.Current.CancellationToken);

        var prompt = capturedMessages!.Single().Text;
        Assert.Contains("3", prompt, StringComparison.Ordinal);
        Assert.Contains("test query", prompt, StringComparison.Ordinal);
    }
}
