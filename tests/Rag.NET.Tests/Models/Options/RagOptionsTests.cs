using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models.Options;

public class RagOptionsTests
{
    [Fact]
    public void SynthesisStrategy_DefaultsToDefault()
    {
        var opts = new RagOptions();
        Assert.Equal(SynthesisStrategy.Default, opts.SynthesisStrategy);
    }

    [Fact]
    public void MapReduceOptions_DefaultConcurrencyIsFive()
    {
        var opts = new MapReduceOptions();
        Assert.Equal(5, opts.MapConcurrency);
    }

    [Fact]
    public void MapReduceOptions_NullTemplatesByDefault()
    {
        var opts = new MapReduceOptions();
        Assert.Null(opts.MapPromptTemplate);
        Assert.Null(opts.ReducePromptTemplate);
    }

    [Fact]
    public void RefineOptions_NullTemplatesByDefault()
    {
        var opts = new RefineOptions();
        Assert.Null(opts.InitialPromptTemplate);
        Assert.Null(opts.RefinePromptTemplate);
    }
}
