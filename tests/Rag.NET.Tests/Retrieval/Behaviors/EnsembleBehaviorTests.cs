using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class EnsembleBehaviorTests
{
    [Fact]
    public void EnsembleOptions_Defaults_AreCorrect()
    {
        var opts = new EnsembleOptions();
        Assert.Equal(0.5f, opts.DenseWeight);
        Assert.Equal(0.5f, opts.Bm25Weight);
        Assert.Equal(60, opts.K);
    }

    [Fact]
    public void RetrievalOptions_EnsembleOptions_DefaultsToNull()
    {
        var opts = new RetrievalOptions();
        Assert.Null(opts.EnsembleOptions);
    }
}
