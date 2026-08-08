using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Hosting.DependencyInjection;
using Xunit;

namespace Rag.NET.Hosting.Tests;

/// <summary>
/// Proves Phase 4.6 Task 2: <c>AddRagNetPipelineFromConfiguration</c> fails while the host is
/// being built, not the first time an MCP tool is invoked, and every failure names both the
/// setting and the configuration key that fixes it — never a bare "missing endpoint" or the
/// downstream <c>Unable to resolve service for type 'IRagPipeline'</c> the tool used to surface.
/// </summary>
public sealed class AddRagNetPipelineFromConfigurationValidationTests
{
    [Fact]
    public void UnrecognisedVectorStoreKindThrowsNamingTheKeyAndTheValidKinds()
    {
        var settings = ValidSettings();
        settings["RagNet:VectorStore:Kind"] = "Qdrnat";

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("RagNet:VectorStore:Kind", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'Qdrnat'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "InMemory, Qdrant, or PgVector", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnrecognisedVectorStoreKindDoesNotSilentlyBecomeInMemory()
    {
        // The regression this task exists to close: a typo used to resolve InMemory instead of
        // failing, giving a working-looking pipeline whose data vanishes on restart.
        var settings = ValidSettings();
        settings["RagNet:VectorStore:Kind"] = "Qdrnat";

        Assert.Throws<InvalidOperationException>(() => Build(settings));
    }

    [Fact]
    public void QdrantWithNoHostThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings["RagNet:VectorStore:Kind"] = "Qdrant";
        settings["RagNet:VectorStore:Qdrant:CollectionName"] = "chunks";

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains(
            "RagNet:VectorStore:Qdrant:Host", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QdrantWithNoCollectionNameThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings["RagNet:VectorStore:Kind"] = "Qdrant";
        settings["RagNet:VectorStore:Qdrant:Host"] = "localhost";

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains(
            "RagNet:VectorStore:Qdrant:CollectionName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PgVectorWithNoConnectionStringThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings["RagNet:VectorStore:Kind"] = "PgVector";

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains(
            "RagNet:VectorStore:PgVector:ConnectionString", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingChatClientEndpointThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings.Remove("RagNet:ChatClient:Endpoint");

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains(
            "RagNet:ChatClient:Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingChatClientModelThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings.Remove("RagNet:ChatClient:Model");

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("RagNet:ChatClient:Model", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingEmbeddingsEndpointThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings.Remove("RagNet:Embeddings:Endpoint");

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains(
            "RagNet:Embeddings:Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingEmbeddingsModelThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings.Remove("RagNet:Embeddings:Model");

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("RagNet:Embeddings:Model", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnparsableEndpointThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings["RagNet:ChatClient:Endpoint"] = "not a url";

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains(
            "RagNet:ChatClient:Endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "not a valid absolute URL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingVectorDimensionsThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings.Remove("RagNet:Embeddings:VectorDimensions");

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains(
            "RagNet:Embeddings:VectorDimensions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPositiveVectorDimensionsThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings["RagNet:Embeddings:VectorDimensions"] = "0";

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains(
            "RagNet:Embeddings:VectorDimensions", exception.Message, StringComparison.Ordinal);
        Assert.Contains("must be positive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingApiKeyForANonLoopbackEndpointThrowsNamingTheKey()
    {
        var settings = ValidSettings();
        settings["RagNet:ChatClient:Endpoint"] = "https://api.openai.com/v1";
        settings.Remove("RagNet:ChatClient:ApiKey");

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("RagNet:ChatClient:ApiKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RagNet:ChatClient:Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingApiKeyForALoopbackEndpointDoesNotThrow()
    {
        var settings = ValidSettings();
        settings["RagNet:ChatClient:Endpoint"] = "http://localhost:11434/v1";
        settings.Remove("RagNet:ChatClient:ApiKey");

        using var provider = Build(settings);

        Assert.NotNull(provider);
    }

    [Fact]
    public void EveryProblemIsReportedInOneException()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RagNet:VectorStore:Kind"] = "Qdrnat",
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("RagNet:ChatClient:Endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RagNet:ChatClient:Model", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RagNet:Embeddings:Endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RagNet:Embeddings:Model", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "RagNet:Embeddings:VectorDimensions", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RagNet:VectorStore:Kind", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddRagNetPipelineFromConfiguration(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ValidSettings() => new(StringComparer.Ordinal)
    {
        ["RagNet:ChatClient:Endpoint"] = "https://openrouter.ai/api/v1",
        ["RagNet:ChatClient:ApiKey"] = "test-key",
        ["RagNet:ChatClient:Model"] = "meta-llama/llama-3.3-70b-instruct",
        ["RagNet:Embeddings:Endpoint"] = "https://openrouter.ai/api/v1",
        ["RagNet:Embeddings:ApiKey"] = "test-key",
        ["RagNet:Embeddings:Model"] = "text-embedding-3-small",
        ["RagNet:Embeddings:VectorDimensions"] = "1536",
        ["RagNet:VectorStore:Kind"] = "InMemory",
    };
}
