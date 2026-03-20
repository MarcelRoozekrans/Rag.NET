using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class DocumentIdTests
{
    [Fact]
    public void DocumentId_EqualityByValue()
    {
        var a = new DocumentId("doc-1");
        var b = new DocumentId("doc-1");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void DocumentId_InequalityByValue()
    {
        var a = new DocumentId("doc-1");
        var b = new DocumentId("doc-2");
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void DocumentId_ImplicitToString()
    {
        var id = new DocumentId("doc-1");
        string s = id;
        Assert.Equal("doc-1", s);
    }

    [Fact]
    public void DocumentId_ExplicitFromString()
    {
        var id = (DocumentId)"doc-1";
        Assert.Equal(new DocumentId("doc-1"), id);
    }

    [Fact]
    public void DocumentId_ToStringReturnsValue()
    {
        Assert.Equal("doc-1", new DocumentId("doc-1").ToString());
    }

    [Fact]
    public void DocumentId_UsableAsDictionaryKey()
    {
        var dict = new Dictionary<DocumentId, int>();
        var id = new DocumentId("doc-1");
        dict[id] = 42;
        Assert.Equal(42, dict[new DocumentId("doc-1")]);
    }
}
