using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models.Options;

public class RagOptionsCopyTests
{
    public static TheoryData<string> PropertyNames()
    {
        var data = new TheoryData<string>();
        foreach (var property in RagOptionsSamples.SettableProperties)
            data.Add(property.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(PropertyNames))]
    public void Sample_DiffersFromDefault(string name)
    {
        var property = RagOptionsSamples.Property(name);

        Assert.NotEqual(property.GetValue(new RagOptions()), property.GetValue(RagOptionsSamples.AllSet()));
    }

    [Theory]
    [MemberData(nameof(PropertyNames))]
    public void Copy_CarriesProperty(string name)
    {
        var property = RagOptionsSamples.Property(name);
        var source = RagOptionsSamples.AllSet();

        var copy = source.Copy();

        Assert.Equal(property.GetValue(source), property.GetValue(copy));
    }

    [Fact]
    public void Copy_ReturnsANewInstance()
    {
        var source = new RagOptions();

        Assert.NotSame(source, source.Copy());
    }

    [Fact]
    public void Copy_KeepsNoFieldsAssigned()
    {
        var copy = new RagOptions { Retrieval = new RetrievalOptions() }.Copy();

        Assert.Empty(copy.AssignedRetrievalFields);
    }

    [Theory]
    [MemberData(nameof(RagOptionsRetrievalFieldCases.Names), MemberType = typeof(RagOptionsRetrievalFieldCases))]
    public void Copy_KeepsAssignedField(string name)
    {
        var copy = RagOptionsRetrievalFieldCases.WithFieldAtDefault(name).Copy();

        Assert.Equal([name], copy.AssignedRetrievalFields);
    }

    [Fact]
    public void Copy_DoesNotShareAssignmentState()
    {
        var source = new RagOptions();
        var copy = source.Copy();

        copy.TopK = 3;

        Assert.Empty(source.AssignedRetrievalFields);
        Assert.Equal(5, source.TopK);
    }
}
