using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Qdrant.Tests;

public class QdrantBuilderExtensionsTests
{
    private static IServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddRagNet(rag => rag.UseQdrant("localhost", 6334, "test-collection"))
            .BuildServiceProvider();

    [Fact]
    public void UseQdrant_RegistersIVectorStore()
    {
        var provider = BuildProvider();

        var store = provider.GetRequiredService<IVectorStore>();

        Assert.IsType<QdrantVectorStore>(store);
    }

    [Fact]
    public void UseQdrant_RegistersICollectionManageable()
    {
        var provider = BuildProvider();

        var manageable = provider.GetRequiredService<ICollectionManageable>();

        Assert.IsType<QdrantVectorStore>(manageable);
    }

    [Fact]
    public void UseQdrant_BothInterfacesResolveSameInstance()
    {
        var provider = BuildProvider();

        var store = provider.GetRequiredService<IVectorStore>();
        var manageable = provider.GetRequiredService<ICollectionManageable>();

        Assert.Same(store, manageable);
    }
}
