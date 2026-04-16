using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class RbacRetrievalGuardTests
{
    private static SearchResult MakeResult(string? allowedRoles, string docId = "doc1")
    {
        var chunk = new TextChunk
        {
            Text = "chunk text",
            DocumentId = new DocumentId(docId),
            ChunkIndex = 0,
        };
        if (allowedRoles is not null)
            chunk.Metadata["allowed_roles"] = allowedRoles;
        return new SearchResult { Score = 1.0, Chunk = chunk };
    }

    private static ICallerContext CallerWith(params string[] roles)
    {
        var ctx = Substitute.For<ICallerContext>();
        ctx.GetRoles().Returns(roles);
        return ctx;
    }

    [Fact]
    public void Inspect_NoAllowedRoles_PassesThrough()
    {
        var sut = new RbacRetrievalGuard(CallerWith("user"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult(null) };
        Assert.Single(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_CallerHasMatchingRole_PassesThrough()
    {
        var sut = new RbacRetrievalGuard(CallerWith("hr"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr,finance") };
        Assert.Single(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_CallerLacksRole_ChunkFiltered()
    {
        var sut = new RbacRetrievalGuard(CallerWith("user"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr,finance") };
        Assert.Empty(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_EmptyCallerRoles_RestrictedChunkFiltered()
    {
        var sut = new RbacRetrievalGuard(CallerWith(), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr") };
        Assert.Empty(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_MixedChunks_OnlyRestrictedFiltered()
    {
        var sut = new RbacRetrievalGuard(CallerWith("hr"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[]
        {
            MakeResult(null, "public"),
            MakeResult("hr", "hr-only"),
            MakeResult("finance", "finance-only"),
        };
        var inspected = sut.Inspect(results);
        Assert.Equal(2, inspected.Count);
        Assert.DoesNotContain(inspected, r => string.Equals(r.Chunk.DocumentId.Value, "finance-only", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_RoleCaseInsensitive_Matches()
    {
        var sut = new RbacRetrievalGuard(CallerWith("HR"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr") };
        Assert.Single(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_AllPublicChunks_ReturnsOriginalList()
    {
        var sut = new RbacRetrievalGuard(CallerWith("user"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new List<SearchResult> { MakeResult(null), MakeResult(null) }.AsReadOnly();
        Assert.Same(results, sut.Inspect(results));
    }

    [Fact]
    public void Inspect_RolesWithSpacesInCsv_TrimmedAndMatched()
    {
        // "hr, finance" has a space after the comma — TrimEntries should handle it
        var sut = new RbacRetrievalGuard(CallerWith("finance"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr, finance") };
        Assert.Single(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_EmptyEntriesInCsv_Ignored()
    {
        // "hr,,finance" has an empty entry between the two commas — RemoveEmptyEntries should handle it
        var sut = new RbacRetrievalGuard(CallerWith("hr"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr,,finance") };
        Assert.Single(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_CorrelationContextNull_DoesNotCrash()
    {
        // Verify no NullReferenceException when allowed_roles is set and caller has a valid role
        var sut = new RbacRetrievalGuard(CallerWith("hr"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr") };
        var inspected = sut.Inspect(results);
        Assert.Single(inspected);
    }
}
