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
}
