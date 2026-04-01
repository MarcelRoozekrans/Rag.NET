using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Graph;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class MindMapExtractorGraphPersistenceTests : IAsyncDisposable
{
    private const string NestedJson = """
        {
          "title": "Root",
          "summary": "Root summary.",
          "children": [
            {
              "title": "Child A",
              "summary": "Child A summary.",
              "children": [
                {
                  "title": "Grandchild",
                  "summary": "Grandchild summary.",
                  "children": []
                }
              ]
            },
            {
              "title": "Child B",
              "summary": "Child B summary.",
              "children": []
            }
          ]
        }
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly SqliteGraphStore _graphStore = new(":memory:");

    public ValueTask DisposeAsync() => _graphStore.DisposeAsync();

    private void SetupChatClient(string response) =>
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

    [Fact]
    public async Task ExtractAsync_PersistsAllNodesAsEntities()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, _graphStore, new MindMapOptions());

        await sut.ExtractAsync("text", "doc-1", TestContext.Current.CancellationToken);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var mindMapNodes = snapshot.Entities.Where(e => string.Equals(e.Type, "mind_map_node", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, mindMapNodes.Count); // Root + Child A + Child B + Grandchild
    }

    [Fact]
    public async Task ExtractAsync_PersistsEdgesAsHasSubtopicRelationships()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, _graphStore, new MindMapOptions());

        await sut.ExtractAsync("text", "doc-1", TestContext.Current.CancellationToken);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var edges = snapshot.Relationships.Where(r => string.Equals(r.Description, "has_subtopic", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, edges.Count); // Root→ChildA, Root→ChildB, ChildA→Grandchild
    }

    [Fact]
    public async Task ExtractAsync_TagsEntitiesWithDocumentId()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, _graphStore, new MindMapOptions());

        await sut.ExtractAsync("text", "my-doc", TestContext.Current.CancellationToken);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.All(
            snapshot.Entities.Where(e => string.Equals(e.Type, "mind_map_node", StringComparison.Ordinal)),
            e => Assert.Equal("my-doc", e.SourceDocumentId));
    }

    [Fact]
    public async Task ExtractAsync_NoGraphStore_DoesNotThrow()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, graphStore: null, new MindMapOptions());

        var result = await sut.ExtractAsync("text", "doc-1", TestContext.Current.CancellationToken);

        Assert.Equal("Root", result.Title);
    }

    [Fact]
    public async Task ExtractAsync_TagsRelationshipsWithDocumentId()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, _graphStore, new MindMapOptions());

        await sut.ExtractAsync("text", "my-doc", TestContext.Current.CancellationToken);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.All(
            snapshot.Relationships.Where(r => string.Equals(r.Description, "has_subtopic", StringComparison.Ordinal)),
            r => Assert.Equal("my-doc", r.SourceDocumentId));
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesBothEntitiesAndRelationships()
    {
        SetupChatClient(NestedJson);
        var sut = new MindMapExtractor(_chatClient, _graphStore, new MindMapOptions());
        await sut.ExtractAsync("text", "doc-to-delete", TestContext.Current.CancellationToken);

        await _graphStore.DeleteByDocumentIdAsync("doc-to-delete", TestContext.Current.CancellationToken);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var mindMapEntities = snapshot.Entities.Where(e => string.Equals(e.Type, "mind_map_node", StringComparison.Ordinal)).ToList();
        var hasSubtopicEdges = snapshot.Relationships.Where(r => string.Equals(r.Description, "has_subtopic", StringComparison.Ordinal)).ToList();
        Assert.Empty(mindMapEntities);
        Assert.Empty(hasSubtopicEdges);
    }
}
