using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class SemanticChunkingStrategyTests
{
    [Fact]
    public void Options_Defaults_AreCorrect()
    {
        var opts = new SemanticChunkingOptions();
        Assert.Equal(0.25f, opts.BreakpointPercentile);
        Assert.Equal(100, opts.MinChunkSize);
        Assert.Equal(1500, opts.MaxChunkSize);
        Assert.Null(opts.ChunkingEmbedder);
    }
}
