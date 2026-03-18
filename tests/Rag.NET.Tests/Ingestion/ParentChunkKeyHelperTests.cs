using Rag.NET.Ingestion;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class ParentChunkKeyHelperTests
{
    [Fact]
    public void FindParentIndex_ChildWithinFirstParent_ReturnsZero()
    {
        IReadOnlyList<(int start, int end)> boundaries = [(0, 50), (51, 100)];
        Assert.Equal(0, ParentChunkKeyHelper.FindParentIndex(boundaries, childStart: 25));
    }

    [Fact]
    public void FindParentIndex_ChildWithinSecondParent_ReturnsOne()
    {
        IReadOnlyList<(int start, int end)> boundaries = [(0, 50), (51, 100)];
        Assert.Equal(1, ParentChunkKeyHelper.FindParentIndex(boundaries, childStart: 75));
    }

    [Fact]
    public void FindParentIndex_ChildBeyondAllParents_ReturnsLastIndex()
    {
        // Fallback path: child starts at 200 but boundaries only go to 100
        IReadOnlyList<(int start, int end)> boundaries = [(0, 50), (51, 100)];
        Assert.Equal(1, ParentChunkKeyHelper.FindParentIndex(boundaries, childStart: 200));
    }

    [Fact]
    public void FindParentIndex_ChildAtExactBoundaryStart_ReturnsCorrectParent()
    {
        IReadOnlyList<(int start, int end)> boundaries = [(0, 49), (50, 100)];
        Assert.Equal(1, ParentChunkKeyHelper.FindParentIndex(boundaries, childStart: 50));
    }

    [Fact]
    public void GetParentKey_FormatsCorrectly()
    {
        Assert.Equal("doc-1:3", ParentChunkKeyHelper.GetParentKey("doc-1", 3));
    }
}
