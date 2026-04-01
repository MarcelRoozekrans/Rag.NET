using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.PgVector;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UsePgVectorTests
{
    [Fact]
    public void UsePgVector_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost"))
            .BuildServiceProvider();

        Assert.IsType<PgVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UsePgVector_RegistersICollectionManageable()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost"))
            .BuildServiceProvider();

        Assert.IsType<PgVectorStore>(sp.GetRequiredService<ICollectionManageable>());
    }

    [Fact]
    public void UsePgVector_CustomDimensions_Registered()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost", 768))
            .BuildServiceProvider();

        Assert.IsType<PgVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UsePgVector_AllInterfacesResolveSameInstance()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost"))
            .BuildServiceProvider();

        var vectorStore = sp.GetRequiredService<IVectorStore>();
        var collectionManageable = sp.GetRequiredService<ICollectionManageable>();

        Assert.Same(vectorStore, collectionManageable);
    }
}
