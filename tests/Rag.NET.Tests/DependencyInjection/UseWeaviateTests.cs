using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Weaviate;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseWeaviateTests
{
    private static readonly Uri Endpoint = new("http://localhost:8080");

    [Fact]
    public void UseWeaviate_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseWeaviate(Endpoint, "TestClass"))
            .BuildServiceProvider();

        Assert.IsType<WeaviateVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseWeaviate_AllInterfacesResolveSameInstance()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseWeaviate(Endpoint, "TestClass"))
            .BuildServiceProvider();

        var vectorStore = sp.GetRequiredService<IVectorStore>();
        var hybridSearchable = sp.GetRequiredService<IHybridSearchable>();
        var collectionManageable = sp.GetRequiredService<ICollectionManageable>();

        Assert.Same(vectorStore, hybridSearchable);
        Assert.Same(vectorStore, collectionManageable);
    }

    [Fact]
    public void UseWeaviate_EmptyClassName_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(rag => rag.UseWeaviate(Endpoint, "")));

        Assert.Equal("className", exception.ParamName);
    }

    [Fact]
    public void UseWeaviate_LowercaseClassName_Throws()
    {
        // Weaviate class names double as GraphQL fields and must start with a capital letter.
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(rag => rag.UseWeaviate(Endpoint, "lowercase")));

        Assert.Equal("className", exception.ParamName);
    }

    [Fact]
    public void UseWeaviate_ConfigureBreaksOptions_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(
                rag => rag.UseWeaviate(Endpoint, "TestClass", configure: options => options.ClassName = "")));

        Assert.Equal("configure", exception.ParamName);
    }
}
