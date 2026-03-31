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
}
