using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class SynonymMapTests
{
    [Fact]
    public void Expand_UnknownTerm_ReturnsEmptySet()
    {
        var map = new SynonymMap();
        Assert.Empty(map.Expand("unknown"));
    }

    [Fact]
    public void AddGroup_BidirectionalExpansion()
    {
        var map = new SynonymMap();
        map.AddGroup("k8s", "kubernetes");

        Assert.Contains("kubernetes", map.Expand("k8s"));
        Assert.Contains("k8s", map.Expand("kubernetes"));
    }

    [Fact]
    public void AddGroup_ThreeTermGroup_AllTermsExpandToOthers()
    {
        var map = new SynonymMap();
        map.AddGroup("MI", "myocardial infarction", "heart attack");

        var miExpanded = map.Expand("mi");
        Assert.Contains("myocardial infarction", miExpanded);
        Assert.Contains("heart attack", miExpanded);
        Assert.DoesNotContain("mi", miExpanded); // self excluded

        var miExpanded2 = map.Expand("myocardial infarction");
        Assert.Contains("mi", miExpanded2);
        Assert.Contains("heart attack", miExpanded2);
    }

    [Fact]
    public void AddGroup_NormalisesToLowercase()
    {
        var map = new SynonymMap();
        map.AddGroup("K8S", "Kubernetes");

        Assert.Contains("kubernetes", map.Expand("k8s"));
        Assert.Contains("k8s", map.Expand("KUBERNETES"));
    }

    [Fact]
    public void RemoveGroup_RemovesAllListedTerms()
    {
        var map = new SynonymMap();
        map.AddGroup("k8s", "kubernetes");
        map.RemoveGroup("k8s", "kubernetes");

        Assert.Empty(map.Expand("k8s"));
        Assert.Empty(map.Expand("kubernetes"));
    }

    [Fact]
    public void RemoveGroup_UnknownTerms_NoException()
    {
        var map = new SynonymMap();
        map.RemoveGroup("nonexistent"); // should not throw
    }

    [Fact]
    public void AddGroup_FewerThanTwoTerms_ThrowsArgumentException()
    {
        var map = new SynonymMap();
        Assert.Throws<ArgumentException>((Action)(() => map.AddGroup("solo")));
    }

    [Fact]
    public void Constructor_WithGroups_ExpandsCorrectly()
    {
        var map = new SynonymMap([
            ["k8s", "kubernetes"],
            ["js", "javascript"],
        ]);

        Assert.Contains("kubernetes", map.Expand("k8s"));
        Assert.Contains("js", map.Expand("javascript"));
    }
}
