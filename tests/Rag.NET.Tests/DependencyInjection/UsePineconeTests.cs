using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Pinecone;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UsePineconeTests
{
    [Fact]
    public void UsePinecone_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePinecone("api-key", "test-chunks"))
            .BuildServiceProvider();

        // Assert.IsType is exact: the dense default must NOT be the sparse subtype.
        Assert.IsType<PineconeVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UsePinecone_Default_AllInterfacesResolveSameInstance_NoSparseRegistration()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePinecone("api-key", "test-chunks"))
            .BuildServiceProvider();

        var vectorStore = sp.GetRequiredService<IVectorStore>();
        var collectionManageable = sp.GetRequiredService<ICollectionManageable>();

        Assert.Same(vectorStore, collectionManageable);
        // Honest capability: dense-only registration serves no ISparseSearchable.
        Assert.Null(sp.GetService<ISparseSearchable>());
    }

    [Fact]
    public void UsePinecone_EnableSparseVectors_RegistersSparseStoreForAllThreeInterfaces()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePinecone(
                "api-key", "test-chunks", configure: options => options.EnableSparseVectors = true))
            .BuildServiceProvider();

        var vectorStore = sp.GetRequiredService<IVectorStore>();
        var collectionManageable = sp.GetRequiredService<ICollectionManageable>();
        var sparseSearchable = sp.GetRequiredService<ISparseSearchable>();

        Assert.IsType<PineconeSparseVectorStore>(vectorStore);
        Assert.Same(vectorStore, collectionManageable);
        Assert.Same(vectorStore, sparseSearchable);
    }

    [Fact]
    public void UsePinecone_EmptyApiKey_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(rag => rag.UsePinecone("", "test-chunks")));

        Assert.Equal("apiKey", exception.ParamName);
    }

    [Fact]
    public void UsePinecone_EmptyIndexName_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(rag => rag.UsePinecone("api-key", "")));

        Assert.Equal("indexName", exception.ParamName);
    }

    [Theory]
    [InlineData("Test-Chunks")]  // uppercase not allowed
    [InlineData("-chunks-")]     // must start and end with a letter or digit
    [InlineData("chunks_a")]     // underscores not allowed
    public void UsePinecone_InvalidIndexName_Throws(string indexName)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(rag => rag.UsePinecone("api-key", indexName)));

        Assert.Equal("indexName", exception.ParamName);
    }

    [Fact]
    public void UsePinecone_ConfigureBreaksOptions_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddRagNet(
                rag => rag.UsePinecone("api-key", "test-chunks", configure: options => options.Region = " ")));

        Assert.Equal("configure", exception.ParamName);
    }
}
