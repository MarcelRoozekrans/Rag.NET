using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Qdrant;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseQdrantTests
{
    [Fact]
    public void UseQdrant_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseQdrant("localhost", 6333, "test"))
            .BuildServiceProvider();

        Assert.IsType<QdrantVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseQdrant_RegistersICollectionManageable()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseQdrant("localhost", 6333, "test"))
            .BuildServiceProvider();

        Assert.IsType<QdrantVectorStore>(sp.GetRequiredService<ICollectionManageable>());
    }

    [Fact]
    public void UseQdrant_CustomDimensions_Registered()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseQdrant("localhost", 6333, "test", 768))
            .BuildServiceProvider();

        Assert.IsType<QdrantVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseQdrant_AllInterfacesResolveSameInstance()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseQdrant("localhost", 6333, "test"))
            .BuildServiceProvider();

        var vectorStore = sp.GetRequiredService<IVectorStore>();
        var collectionManageable = sp.GetRequiredService<ICollectionManageable>();

        Assert.Same(vectorStore, collectionManageable);
    }
}
