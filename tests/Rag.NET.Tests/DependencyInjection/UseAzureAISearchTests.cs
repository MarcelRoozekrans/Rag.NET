using Azure;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.AzureAISearch;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseAzureAISearchTests
{
    private static readonly Uri s_endpoint = new("https://example.search.windows.net");
    private static readonly AzureKeyCredential s_credential = new("fake-key");

    [Fact]
    public void UseAzureAISearch_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(s_endpoint, "index", s_credential))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseAzureAISearch_RegistersIHybridSearchable()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(s_endpoint, "index", s_credential))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(sp.GetRequiredService<IHybridSearchable>());
    }

    [Fact]
    public void UseAzureAISearch_RegistersICollectionManageable()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(s_endpoint, "index", s_credential))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(sp.GetRequiredService<ICollectionManageable>());
    }

    [Fact]
    public void UseAzureAISearch_CustomDimensions_Registered()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(s_endpoint, "index", s_credential, 768))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseAzureAISearch_AllInterfacesResolveSameInstance()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(s_endpoint, "index", s_credential))
            .BuildServiceProvider();

        var vectorStore = sp.GetRequiredService<IVectorStore>();
        var hybridSearchable = sp.GetRequiredService<IHybridSearchable>();
        var collectionManageable = sp.GetRequiredService<ICollectionManageable>();

        Assert.Same(vectorStore, hybridSearchable);
        Assert.Same(vectorStore, collectionManageable);
    }

    /// <summary>
    /// <c>KNearestNeighborsCount</c> is configurable and defaults to <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Null means the parameter is omitted from the query, so Azure applies its own documented
    /// default of 50. That matters: the store used to hard-code k to <c>TopK</c>, which at a
    /// typical top-5 narrowed vector recall to a tenth of what Azure would have used unasked
    /// (#328).
    /// </remarks>
    [Fact]
    public void UseAzureAISearch_KNearestNeighborsCount_DefaultsToNull()
    {
        AzureAISearchOptions? captured = null;

        _ = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(
                s_endpoint, "index", s_credential, configure: o => captured = o))
            .BuildServiceProvider();

        Assert.NotNull(captured);
        Assert.Null(captured.KNearestNeighborsCount);
    }

    [Fact]
    public void UseAzureAISearch_KNearestNeighborsCount_RoundTripsThroughConfigure()
    {
        AzureAISearchOptions? captured = null;

        _ = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(
                s_endpoint, "index", s_credential, configure: o =>
                {
                    o.KNearestNeighborsCount = 50;
                    captured = o;
                }))
            .BuildServiceProvider();

        Assert.NotNull(captured);
        Assert.Equal(50, captured.KNearestNeighborsCount);
    }

    /// <summary>A configured k below 1 is rejected eagerly, the way the sibling stores validate.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UseAzureAISearch_KNearestNeighborsCountBelowOne_Throws(int invalid)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseAzureAISearch(
                s_endpoint, "index", s_credential, configure: o => o.KNearestNeighborsCount = invalid)));
    }
}
