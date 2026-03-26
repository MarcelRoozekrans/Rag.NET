using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models.Options;

public class HierarchicalMergerOptionsTests
{
    [Fact]
    public void MaxDepth_DefaultsToTwo()
    {
        var opts = new HierarchicalMergerOptions();
        Assert.Equal(2, opts.MaxDepth);
    }

    [Fact]
    public void HeadingPatterns_DefaultsToNull()
    {
        var opts = new HierarchicalMergerOptions();
        Assert.Null(opts.HeadingPatterns);
    }
}
