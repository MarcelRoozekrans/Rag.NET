using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.PgVector.Tests;

public class PgVectorBuilderExtensionsTests
{
    private static IServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost;Database=test"))
            .BuildServiceProvider();

    [Fact]
    public void UsePgVector_RegistersIVectorStore()
    {
        var provider = BuildProvider();

        var store = provider.GetRequiredService<IVectorStore>();

        Assert.IsType<PgVectorStore>(store);
    }

    [Fact]
    public void UsePgVector_RegistersICollectionManageable()
    {
        var provider = BuildProvider();

        var manageable = provider.GetRequiredService<ICollectionManageable>();

        Assert.IsType<PgVectorStore>(manageable);
    }

    [Fact]
    public void UsePgVector_BothInterfacesResolveSameInstance()
    {
        var provider = BuildProvider();

        var store = provider.GetRequiredService<IVectorStore>();
        var manageable = provider.GetRequiredService<ICollectionManageable>();

        Assert.Same(store, manageable);
    }
}
