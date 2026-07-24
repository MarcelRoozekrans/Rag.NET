using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class PropositionChunkingStrategyTests
{
    [Fact]
    public void Options_Defaults_AreCorrect()
    {
        var options = new PropositionChunkingOptions();

        Assert.Equal(1000, options.MaxPassageTokens);
        Assert.Equal(50, options.MaxPropositionsPerPassage);
        Assert.False(options.EmitParentPassages);
        Assert.Null(options.ChatClient);
    }
}
