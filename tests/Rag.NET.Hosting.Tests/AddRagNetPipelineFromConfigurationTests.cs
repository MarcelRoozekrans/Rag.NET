using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Hosting.DependencyInjection;
using Rag.NET.PgVector;
using Rag.NET.Qdrant;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Hosting.Tests;

/// <summary>
/// Proves the wiring seam from Phase 4.6 Task 1: a configuration handed to
/// <c>AddRagNetPipelineFromConfiguration</c> resolves the vector store its <c>Kind</c> names, and
/// a configured chat/embeddings endpoint resolves a usable client. Nothing here makes a network
/// call — every assertion is about what got <em>registered</em>, never about what a provider
/// returns, matching the design's §6 testing strategy.
/// </summary>
public sealed class AddRagNetPipelineFromConfigurationTests
{
    [Fact]
    public void DefaultKindResolvesAnInMemoryVectorStore()
    {
        using var provider = BuildProvider(BaseSettings("InMemory"));

        Assert.IsType<InMemoryVectorStore>(provider.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void AnEmptyKindResolvesAnInMemoryVectorStoreToo()
    {
        var settings = BaseSettings(string.Empty);
        using var provider = BuildProvider(settings);

        Assert.IsType<InMemoryVectorStore>(provider.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void QdrantKindResolvesAQdrantVectorStore()
    {
        var settings = BaseSettings("Qdrant");
        settings["RagNet:VectorStore:Qdrant:Host"] = "localhost";
        settings["RagNet:VectorStore:Qdrant:Port"] = "6334";
        settings["RagNet:VectorStore:Qdrant:CollectionName"] = "test-collection";
        using var provider = BuildProvider(settings);

        Assert.IsType<QdrantVectorStore>(provider.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void PgVectorKindResolvesAPgVectorStore()
    {
        var settings = BaseSettings("PgVector");
        settings["RagNet:VectorStore:PgVector:ConnectionString"] =
            "Host=localhost;Database=ragnet_test;Username=test;Password=test";
        using var provider = BuildProvider(settings);

        Assert.IsType<PgVectorStore>(provider.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void QdrantKindIsCaseInsensitive()
    {
        var settings = BaseSettings("qDRANT");
        settings["RagNet:VectorStore:Qdrant:Host"] = "localhost";
        settings["RagNet:VectorStore:Qdrant:Port"] = "6334";
        settings["RagNet:VectorStore:Qdrant:CollectionName"] = "test-collection";
        using var provider = BuildProvider(settings);

        Assert.IsType<QdrantVectorStore>(provider.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void AConfiguredEndpointProducesAUsableChatClient()
    {
        using var provider = BuildProvider(BaseSettings("InMemory"));

        Assert.NotNull(provider.GetRequiredService<IChatClient>());
    }

    [Fact]
    public void AConfiguredEndpointProducesAUsableEmbeddingGenerator()
    {
        using var provider = BuildProvider(BaseSettings("InMemory"));

        Assert.NotNull(
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void AnUnsetApiKeyForALoopbackEndpointStillProducesAUsableChatClient()
    {
        var settings = BaseSettings("InMemory");
        settings["RagNet:ChatClient:Endpoint"] = "http://localhost:11434/v1";
        settings["RagNet:Embeddings:Endpoint"] = "http://127.0.0.1:11434/v1";
        settings.Remove("RagNet:ChatClient:ApiKey");
        settings.Remove("RagNet:Embeddings:ApiKey");
        using var provider = BuildProvider(settings);

        Assert.NotNull(provider.GetRequiredService<IChatClient>());
        Assert.NotNull(
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddRagNetPipelineFromConfiguration(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> BaseSettings(string kind) => new(StringComparer.Ordinal)
    {
        ["RagNet:ChatClient:Endpoint"] = "https://openrouter.ai/api/v1",
        ["RagNet:ChatClient:ApiKey"] = "test-key",
        ["RagNet:ChatClient:Model"] = "meta-llama/llama-3.3-70b-instruct",
        ["RagNet:Embeddings:Endpoint"] = "https://openrouter.ai/api/v1",
        ["RagNet:Embeddings:ApiKey"] = "test-key",
        ["RagNet:Embeddings:Model"] = "text-embedding-3-small",
        ["RagNet:Embeddings:VectorDimensions"] = "1536",
        ["RagNet:VectorStore:Kind"] = kind,
    };
}
