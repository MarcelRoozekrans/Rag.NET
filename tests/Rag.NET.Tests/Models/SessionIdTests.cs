using System.Text.Json;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class SessionIdTests
{
    [Fact]
    public void SessionId_EqualityByValue()
    {
        var a = new SessionId("session-1");
        var b = new SessionId("session-1");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void SessionId_InequalityByValue()
    {
        var a = new SessionId("session-1");
        var b = new SessionId("session-2");
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void SessionId_ImplicitToString()
    {
        var id = new SessionId("session-1");
        string s = id;
        Assert.Equal("session-1", s);
    }

    [Fact]
    public void SessionId_ExplicitFromString()
    {
        var id = (SessionId)"session-1";
        Assert.Equal(new SessionId("session-1"), id);
    }

    [Fact]
    public void SessionId_ToStringReturnsValue()
    {
        Assert.Equal("session-1", new SessionId("session-1").ToString());
    }

    [Fact]
    public void SessionId_UsableAsDictionaryKey()
    {
        var dict = new Dictionary<SessionId, int>();
        var id = new SessionId("session-1");
        dict[id] = 42;
        Assert.Equal(42, dict[new SessionId("session-1")]);
    }

    [Fact]
    public void SessionId_NullStringThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionId(null!));
    }

    [Fact]
    public void SessionId_EmptyStringThrows()
    {
        Assert.Throws<ArgumentException>(() => new SessionId(""));
    }

    [Fact]
    public void SessionId_JsonRoundTrip()
    {
        var id = new SessionId("session-1");
        var json = JsonSerializer.Serialize(id);
        var restored = JsonSerializer.Deserialize<SessionId>(json);
        Assert.Equal(id, restored);
    }
}
