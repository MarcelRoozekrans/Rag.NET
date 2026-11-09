using System.Text.Json;
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

    [Fact]
    public void AssignedRetrievalFields_EmptyByDefault()
    {
        Assert.Empty(new RagOptions().AssignedRetrievalFields);
    }

    [Theory]
    [MemberData(nameof(RagOptionsRetrievalFieldCases.Names), MemberType = typeof(RagOptionsRetrievalFieldCases))]
    public void AssignedRetrievalFields_RecordsAssignmentAtDefaultValue(string name)
    {
        var opts = RagOptionsRetrievalFieldCases.WithFieldAtDefault(name);

        Assert.Equal([name], opts.AssignedRetrievalFields);
    }

    [Fact]
    public void AssignedRetrievalFields_IgnoresNonRetrievalFields()
    {
        var opts = new RagOptions
        {
            SystemPrompt = "p",
            Temperature = 0.1f,
            Retrieval = new RetrievalOptions { UseMmr = true },
        };

        Assert.Empty(opts.AssignedRetrievalFields);
    }

    [Fact]
    public void AssignedRetrievalFields_ListsFieldsInDeclarationOrder()
    {
        var opts = new RagOptions { MetadataFilter = null, TopK = 3 };

        Assert.Equal([nameof(RagOptions.TopK), nameof(RagOptions.MetadataFilter)], opts.AssignedRetrievalFields);
    }

    [Fact]
    public void Json_RetrievalOnly_AssignsNoField()
    {
        var opts = JsonSerializer.Deserialize<RagOptions>("""{"retrieval":{"useMmr":true}}""", JsonSerializerOptions.Web);

        Assert.NotNull(opts);
        Assert.True(opts.Retrieval?.UseMmr);
        Assert.Empty(opts.AssignedRetrievalFields);
    }

    [Fact]
    public void Json_TopK_AssignsTopK()
    {
        var opts = JsonSerializer.Deserialize<RagOptions>("""{"retrieval":{},"topK":5}""", JsonSerializerOptions.Web);

        Assert.NotNull(opts);
        Assert.Equal([nameof(RagOptions.TopK)], opts.AssignedRetrievalFields);
    }
}
