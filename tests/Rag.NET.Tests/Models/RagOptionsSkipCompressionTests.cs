using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models;

public class RagOptionsSkipCompressionTests
{
    [Fact]
    public void SkipCompression_DefaultsToFalse()
    {
        var sut = new RagOptions();
        Assert.False(sut.SkipCompression);
    }

    [Fact]
    public void SkipCompression_CanBeSet()
    {
        var sut = new RagOptions { SkipCompression = true };
        Assert.True(sut.SkipCompression);
    }
}
