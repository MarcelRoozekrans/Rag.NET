using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Memory;

public class ConversationMemoryTests
{
    [Fact]
    public void Options_Defaults_AreCorrect()
    {
        var opts = new ConversationMemoryOptions();
        Assert.Null(opts.MaxExchanges);
        Assert.Null(opts.MaxTokens);
        Assert.False(opts.UseSummary);
        Assert.Null(opts.SummaryPromptTemplate);
    }
}
