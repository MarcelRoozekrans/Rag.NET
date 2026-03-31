using Rag.NET.Chunking.CSharp;
using Xunit;

namespace Rag.NET.Chunking.CSharp.Tests;

public class CSharpChunkingOptionsTests
{
    [Fact]
    public void Defaults_IncludePrivateMembers_IsFalse()
        => Assert.False(new CSharpChunkingOptions().IncludePrivateMembers);

    [Fact]
    public void Defaults_IncludeInternalMembers_IsTrue()
        => Assert.True(new CSharpChunkingOptions().IncludeInternalMembers);

    [Fact]
    public void Defaults_IncludeBodies_IsTrue()
        => Assert.True(new CSharpChunkingOptions().IncludeBodies);
}
