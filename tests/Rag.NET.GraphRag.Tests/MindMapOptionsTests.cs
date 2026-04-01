using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class MindMapOptionsTests
{
    [Fact]
    public void DefaultOptions_ExtractAtIngestionIsFalse()
    {
        var options = new MindMapOptions();
        Assert.False(options.ExtractAtIngestion);
    }

    [Fact]
    public void DefaultOptions_MaxDepthIsThree()
    {
        var options = new MindMapOptions();
        Assert.Equal(3, options.MaxDepth);
    }

    [Fact]
    public void DefaultOptions_PromptContainsDepthPlaceholder()
    {
        var options = new MindMapOptions();
        Assert.Contains("{depth}", options.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultOptions_PromptContainsTextPlaceholder()
    {
        var options = new MindMapOptions();
        Assert.Contains("{text}", options.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void MindMapNode_ChildrenAreEmpty_ByDefault()
    {
        var node = new MindMapNode("Root", "Summary", []);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void MindMapNode_ChildrenAreAccessible()
    {
        var child = new MindMapNode("Child", "Child summary", []);
        var root = new MindMapNode("Root", "Root summary", [child]);
        Assert.Single(root.Children);
        Assert.Equal("Child", root.Children[0].Title);
    }
}
