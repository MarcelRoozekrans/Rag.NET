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
}
