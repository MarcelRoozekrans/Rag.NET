using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class TokenAwareChunkingOptionsTests
{
    [Fact]
    public void Defaults_AreExpected()
    {
        var o = new TokenAwareChunkingOptions();
        Assert.Equal("gpt-4", o.ModelName);
        Assert.Null(o.WindowSizeTokens);
        Assert.Null(o.OverlapTokens);
    }
}
