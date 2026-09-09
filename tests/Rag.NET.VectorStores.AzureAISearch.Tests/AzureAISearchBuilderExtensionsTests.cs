using Azure;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

public class AzureAISearchBuilderExtensionsTests
{
    private static IServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(
                new Uri("https://test.search.windows.net"),
                "test-index",
                new AzureKeyCredential("dummy-key")))
            .BuildServiceProvider();

    [Fact]
    public void UseAzureAISearch_RegistersIVectorStore()
    {
        var provider = BuildProvider();

        var store = provider.GetRequiredService<IVectorStore>();

        Assert.IsType<AzureAISearchVectorStore>(store);
    }

    [Fact]
    public void UseAzureAISearch_RegistersICollectionManageable()
    {
        var provider = BuildProvider();

        var manageable = provider.GetRequiredService<ICollectionManageable>();

        Assert.IsType<AzureAISearchVectorStore>(manageable);
    }

    [Fact]
    public void UseAzureAISearch_RegistersIHybridSearchable()
    {
        var provider = BuildProvider();

        var hybridSearchable = provider.GetRequiredService<IHybridSearchable>();

        Assert.IsType<AzureAISearchVectorStore>(hybridSearchable);
    }

    [Fact]
    public void UseAzureAISearch_AllInterfacesResolveSameInstance()
    {
        var provider = BuildProvider();

        var store = provider.GetRequiredService<IVectorStore>();
        var manageable = provider.GetRequiredService<ICollectionManageable>();
        var hybridSearchable = provider.GetRequiredService<IHybridSearchable>();

        Assert.Same(store, manageable);
        Assert.Same(store, hybridSearchable);
    }

    /// <summary>
    /// With no configured <c>k</c>, the query omits <c>KNearestNeighborsCount</c> entirely.
    /// </summary>
    /// <remarks>
    /// This is the assertion that matters for #328, and it is about an <i>absent</i> value, which
    /// no round-trip test of the options object can reach. Microsoft documents the unspecified
    /// default as 50; the store used to send <c>TopK</c> instead, so at a typical top-5 it asked
    /// for a tenth of Azure's default and starved RRF fusion of candidates to fuse.
    /// </remarks>
    [Fact]
    public void BuildVectorQuery_WithNoConfiguredCount_LeavesKNearestNeighborsCountUnset()
    {
        var query = AzureAISearchVectorStore.BuildVectorQuery(
            new ReadOnlyMemory<float>([0.1f, 0.2f]), kNearestNeighborsCount: null);

        Assert.Null(query.KNearestNeighborsCount);
        Assert.Contains("embedding", query.Fields, StringComparer.Ordinal);
    }

    /// <summary>A configured <c>k</c> reaches the query verbatim — notably semantic ranking's 50.</summary>
    [Fact]
    public void BuildVectorQuery_WithConfiguredCount_SendsItVerbatim()
    {
        var query = AzureAISearchVectorStore.BuildVectorQuery(
            new ReadOnlyMemory<float>([0.1f, 0.2f]), kNearestNeighborsCount: 50);

        Assert.Equal(50, query.KNearestNeighborsCount);
    }

    /// <summary>
    /// Microsoft's own guidance, already quoted in AzureAISearchOptions' remarks: "Whenever you use
    /// semantic ranking with vectors, set k to 50. Semantic ranker uses up to 50 matches as input.
    /// Specifying less than 50 deprives the semantic ranking models of necessary inputs." The
    /// damage is invisible — worse ranking, no error — so the combination is refused at
    /// registration, where both settings are made deliberately by the same person.
    /// </summary>
    [Fact]
    public void EnablingTheRankerWithKBelowFiftyIsRejected()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseAzureAISearch(
                new Uri("https://test.search.windows.net"),
                "test-index",
                new AzureKeyCredential("dummy-key"),
                configure: o =>
                {
                    o.EnableSemanticRanking = true;
                    o.KNearestNeighborsCount = 10;
                })));

        Assert.Contains("50", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Null is the right default: omitting k is what makes Azure apply its own 50.</summary>
    [Fact]
    public void EnablingTheRankerWithNoExplicitKIsAccepted()
    {
        var provider = new ServiceCollection().AddRagNet(rag => rag.UseAzureAISearch(
                new Uri("https://test.search.windows.net"),
                "test-index",
                new AzureKeyCredential("dummy-key"),
                configure: o => o.EnableSemanticRanking = true))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(provider.GetRequiredService<IVectorStore>());
    }

    /// <summary>
    /// Forty-nine is rejected, and this test exists because the mutation sweep proved the boundary
    /// was not pinned. With only a k=10 rejection and a k=50 acceptance, shifting the threshold
    /// from 50 to 11 passed every test while wrongly accepting 49 — the exact value Microsoft's
    /// guidance is about, since the ranker takes "up to 50 matches as input".
    /// </summary>
    [Fact]
    public void EnablingTheRankerWithKJustBelowFiftyIsRejected()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseAzureAISearch(
                new Uri("https://test.search.windows.net"),
                "test-index",
                new AzureKeyCredential("dummy-key"),
                configure: o =>
                {
                    o.EnableSemanticRanking = true;
                    o.KNearestNeighborsCount = 49;
                })));

        Assert.Contains("50", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Fifty exactly is the documented minimum, not a value to reject.</summary>
    [Fact]
    public void EnablingTheRankerWithKAtFiftyIsAccepted()
    {
        var provider = new ServiceCollection().AddRagNet(rag => rag.UseAzureAISearch(
                new Uri("https://test.search.windows.net"),
                "test-index",
                new AzureKeyCredential("dummy-key"),
                configure: o =>
                {
                    o.EnableSemanticRanking = true;
                    o.KNearestNeighborsCount = 50;
                }))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(provider.GetRequiredService<IVectorStore>());
    }
}
