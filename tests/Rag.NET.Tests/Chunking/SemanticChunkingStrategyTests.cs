using Rag.NET.Chunking;
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

    [Theory]
    [InlineData("Hello world. How are you? Fine thanks!", 3)]
    [InlineData("Single sentence without ending punctuation", 1)]
    [InlineData("Dr. Smith went to Washington. He met Mr. Jones.", 2)]
    [InlineData("", 0)]
    [InlineData("First sentence. Second sentence. Third sentence.", 3)]
    public void SplitSentences_VariousInputs_ReturnsExpectedCount(string text, int expectedCount)
    {
        var sentences = SemanticChunkingStrategy.SplitSentences(text);
        Assert.Equal(expectedCount, sentences.Count);
    }

    [Fact]
    public void SplitSentences_PreservesAbbreviations()
    {
        var sentences = SemanticChunkingStrategy.SplitSentences(
            "Dr. Smith e.g. the doctor went home. Then he slept.");
        Assert.Equal(2, sentences.Count);
        Assert.Contains("Dr. Smith", sentences[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var a = new float[] { 1f, 0f, 0f };
        var b = new float[] { 1f, 0f, 0f };
        var sim = SemanticChunkingStrategy.CosineSimilarity(a, b);
        Assert.Equal(1.0, sim, precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 1f };
        var sim = SemanticChunkingStrategy.CosineSimilarity(a, b);
        Assert.Equal(0.0, sim, precision: 5);
    }
}
