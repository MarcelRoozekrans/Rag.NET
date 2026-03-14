using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models;

public class RetrievalOptionsTests
{
    [Fact]
    public void With_TopK_ReturnsNewInstanceWithUpdatedValue()
    {
        var original = new RetrievalOptions { TopK = 5 };
        var modified = original with { TopK = 15 };

        Assert.Equal(5, original.TopK);
        Assert.Equal(15, modified.TopK);
    }

    [Fact]
    public void With_PreservesOtherProperties()
    {
        var original = new RetrievalOptions
        {
            TopK = 5,
            MinScore = 0.5,
            UseHybridSearch = true,
            UseReranking = false,
            RedundancyThreshold = 0.8f,
        };
        var modified = original with { TopK = 15 };

        Assert.Equal(0.5, modified.MinScore);
        Assert.True(modified.UseHybridSearch);
        Assert.False(modified.UseReranking);
        Assert.Equal(0.8f, modified.RedundancyThreshold);
    }
}
