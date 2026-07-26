using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Chroma;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseChromaTests
{
    private static readonly Uri Endpoint = new("http://localhost:8000");

    [Fact]
    public void UseChroma_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseChroma(Endpoint, "test-chunks"))
            .BuildServiceProvider();

        Assert.IsType<ChromaVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseChroma_AllInterfacesResolveSameInstance()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseChroma(Endpoint, "test-chunks"))
            .BuildServiceProvider();

        var vectorStore = sp.GetRequiredService<IVectorStore>();
        var collectionManageable = sp.GetRequiredService<ICollectionManageable>();

        Assert.Same(vectorStore, collectionManageable);
    }

    [Fact]
    public void UseChroma_EmptyCollectionName_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(rag => rag.UseChroma(Endpoint, "")));

        Assert.Equal("collectionName", exception.ParamName);
    }

    [Fact]
    public void UseChroma_InvalidCollectionName_Throws()
    {
        // Chroma names must start and end with a letter or digit.
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(rag => rag.UseChroma(Endpoint, "-chunks-")));

        Assert.Equal("collectionName", exception.ParamName);
    }

    [Fact]
    public void UseChroma_ConfigureBreaksOptions_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(
                rag => rag.UseChroma(Endpoint, "test-chunks", configure: options => options.Tenant = "")));

        Assert.Equal("configure", exception.ParamName);
    }
}
