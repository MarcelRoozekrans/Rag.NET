using System.Text.Json;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class EntryIdTests
{
    [Fact]
    public void EntryId_EqualityByValue()
    {
        var a = new EntryId("entry-1");
        var b = new EntryId("entry-1");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void EntryId_InequalityByValue()
    {
        var a = new EntryId("entry-1");
        var b = new EntryId("entry-2");
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void EntryId_ImplicitToString()
    {
        var id = new EntryId("entry-1");
        string s = id;
        Assert.Equal("entry-1", s);
    }

    [Fact]
    public void EntryId_ExplicitFromString()
    {
        var id = (EntryId)"entry-1";
        Assert.Equal(new EntryId("entry-1"), id);
    }

    [Fact]
    public void EntryId_ToStringReturnsValue()
    {
        Assert.Equal("entry-1", new EntryId("entry-1").ToString());
    }

    [Fact]
    public void EntryId_UsableAsDictionaryKey()
    {
        var dict = new Dictionary<EntryId, int>();
        var id = new EntryId("entry-1");
        dict[id] = 42;
        Assert.Equal(42, dict[new EntryId("entry-1")]);
    }

    [Fact]
    public void EntryId_NullStringThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new EntryId(null!));
    }

    [Fact]
    public void EntryId_EmptyStringThrows()
    {
        Assert.Throws<ArgumentException>(() => new EntryId(""));
    }

    [Fact]
    public void EntryId_JsonRoundTrip()
    {
        var id = new EntryId("entry-1");
        var json = JsonSerializer.Serialize(id);
        var restored = JsonSerializer.Deserialize<EntryId>(json);
        Assert.Equal(id, restored);
    }
}
