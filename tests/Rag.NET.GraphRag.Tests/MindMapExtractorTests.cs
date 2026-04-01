using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class MindMapExtractorTests
{
    private const string ValidJson = """
        {
          "title": "Machine Learning",
          "summary": "Overview of ML concepts.",
          "children": [
            {
              "title": "Supervised Learning",
              "summary": "Learning with labeled data.",
              "children": []
            },
            {
              "title": "Unsupervised Learning",
              "summary": "Learning without labels.",
              "children": []
            }
          ]
        }
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    private MindMapExtractor CreateSut(MindMapOptions? options = null) =>
        new(_chatClient, graphStore: null, options ?? new MindMapOptions());

    private void SetupChatClient(string response) =>
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

    [Fact]
    public async Task ExtractAsync_ValidJson_ReturnsParsedTree()
    {
        SetupChatClient(ValidJson);
        var sut = CreateSut();

        var result = await sut.ExtractAsync("Some text about ML.", "doc-1", TestContext.Current.CancellationToken);

        Assert.Equal("Machine Learning", result.Title);
        Assert.Equal(2, result.Children.Count);
        Assert.Equal("Supervised Learning", result.Children[0].Title);
    }

    [Fact]
    public async Task ExtractAsync_ValidJson_ChildrenHaveSummaries()
    {
        SetupChatClient(ValidJson);
        var sut = CreateSut();

        var result = await sut.ExtractAsync("Some text.", "doc-1", TestContext.Current.CancellationToken);

        Assert.Equal("Learning with labeled data.", result.Children[0].Summary);
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ReturnsEmptyRoot()
    {
        SetupChatClient("not valid json {{");
        var sut = CreateSut();

        var result = await sut.ExtractAsync("Some text.", "doc-1", TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result.Title);
        Assert.Empty(result.Children);
    }

    [Fact]
    public async Task ExtractAsync_SendsPromptWithTextAndDepth()
    {
        SetupChatClient(ValidJson);
        var options = new MindMapOptions { MaxDepth = 5 };
        var sut = CreateSut(options);

        await sut.ExtractAsync("My document text.", "doc-1", TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IEnumerable<ChatMessage>>(msgs =>
                msgs.Any(m => m.Text != null &&
                              m.Text.Contains("My document text.", StringComparison.Ordinal) &&
                              m.Text.Contains("5", StringComparison.Ordinal))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_UsesCustomChatClientWhenProvided()
    {
        var customClient = Substitute.For<IChatClient>();
        customClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJson)]));

        var options = new MindMapOptions { ChatClient = customClient };
        var sut = new MindMapExtractor(_chatClient, graphStore: null, options);

        await sut.ExtractAsync("text", "doc-1", TestContext.Current.CancellationToken);

        await customClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
