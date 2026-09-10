using Rag.NET.Abstractions;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

/// <summary>
/// What the store declares about itself, as opposed to what it does. Deliberately outside the
/// <c>AzureAISearch</c> collection and free of <c>IAsyncLifetime</c>: every assertion here is
/// answered by the type or by a constructor argument, so provisioning the simulator container —
/// which that collection does per test class — would buy nothing and cost a container start each.
/// </summary>
public class AzureAISearchCapabilityDeclarationsTests
{
    /// <summary>
    /// The dense path returns a genuine cosine similarity under every option once the ranker moved
    /// to hybrid (#539), so a declaration of <see cref="ScoreScale.Similarity"/> would be exactly
    /// equal to the documented meaning of not implementing <see cref="IScoreScaleAware"/>. It was
    /// removed rather than left as a member indistinguishable from its own absence.
    /// </summary>
    [Fact]
    public void TheStoreDoesNotDeclareAScoreScale_BecauseTheDefaultIsAlreadyRight()
    {
        Assert.False(typeof(IScoreScaleAware).IsAssignableFrom(typeof(AzureAISearchVectorStore)));
    }

    /// <summary>
    /// The hybrid declaration is the one that still carries information, and it is unchanged: a
    /// fused score and a reranker score are both ordinal (6.2.33). A control on the test above —
    /// without it, removing the wrong interface would still look like a pass.
    /// </summary>
    [Fact]
    public void TheStoreStillDeclaresItsHybridScoreAsOrdinal()
    {
        Assert.True(typeof(IHybridSearchable).IsAssignableFrom(typeof(AzureAISearchVectorStore)));
    }
}
