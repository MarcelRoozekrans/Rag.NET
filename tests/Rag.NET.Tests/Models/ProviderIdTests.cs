using System.Text.Json;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class ProviderIdTests
{
    [Fact]
    public void ProviderId_EqualityByValue()
    {
        var a = new ProviderId("provider-1");
        var b = new ProviderId("provider-1");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void ProviderId_InequalityByValue()
    {
        var a = new ProviderId("provider-1");
        var b = new ProviderId("provider-2");
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void ProviderId_ImplicitToString()
    {
        var id = new ProviderId("provider-1");
        string s = id;
        Assert.Equal("provider-1", s);
    }

    [Fact]
    public void ProviderId_ExplicitFromString()
    {
        var id = (ProviderId)"provider-1";
        Assert.Equal(new ProviderId("provider-1"), id);
    }

    [Fact]
    public void ProviderId_ToStringReturnsValue()
    {
        Assert.Equal("provider-1", new ProviderId("provider-1").ToString());
    }

    [Fact]
    public void ProviderId_UsableAsDictionaryKey()
    {
        var dict = new Dictionary<ProviderId, int>();
        var id = new ProviderId("provider-1");
        dict[id] = 42;
        Assert.Equal(42, dict[new ProviderId("provider-1")]);
    }

    [Fact]
    public void ProviderId_NullStringThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ProviderId(null!));
    }

    [Fact]
    public void ProviderId_EmptyStringThrows()
    {
        Assert.Throws<ArgumentException>(() => new ProviderId(""));
    }

    [Fact]
    public void ProviderId_JsonRoundTrip()
    {
        var id = new ProviderId("provider-1");
        var json = JsonSerializer.Serialize(id);
        var restored = JsonSerializer.Deserialize<ProviderId>(json);
        Assert.Equal(id, restored);
    }
}
