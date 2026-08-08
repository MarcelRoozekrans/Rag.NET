using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Rag.NET.Abstractions;
using Rag.NET.Hosting.DependencyInjection;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Hosting.Tests;

/// <summary>
/// Proves Phase 4.6 Task 3: resolving the default <c>InMemory</c> vector store logs a warning
/// naming the consequence — ingested documents are lost when the process exits — and the setting
/// that avoids it. This is the guard against repeating <c>UseCostBudgeting()</c>'s in-memory
/// cost-ledger silence, so both directions matter here: the warning fires for <c>InMemory</c>,
/// whether configured explicitly or left unset, and it must NOT fire for <c>Qdrant</c> or
/// <c>PgVector</c> — a warning that always fires is noise, and noise is how this protection would
/// stop working without anyone noticing.
/// </summary>
public sealed class AddRagNetPipelineFromConfigurationInMemoryWarningTests
{
    [Fact]
    public void ExplicitInMemoryKindLogsAWarningNamingTheConsequenceAndTheFix()
    {
        var logger = new FakeLogger<InMemoryVectorStore>();
        using var provider = BuildProvider(BaseSettings("InMemory"), logger);

        _ = provider.GetRequiredService<IVectorStore>();

        var warning = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(
            "RagNet:VectorStore:Kind", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            "lost when the process exits", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Qdrant", warning.Message, StringComparison.Ordinal);
        Assert.Contains("PgVector", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsetKindDefaultsToInMemoryAndStillLogsTheWarning()
    {
        var logger = new FakeLogger<InMemoryVectorStore>();
        using var provider = BuildProvider(BaseSettings(string.Empty), logger);

        _ = provider.GetRequiredService<IVectorStore>();

        var warning = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
    }

    [Fact]
    public void QdrantKindDoesNotLogTheInMemoryWarning()
    {
        var settings = BaseSettings("Qdrant");
        settings["RagNet:VectorStore:Qdrant:Host"] = "localhost";
        settings["RagNet:VectorStore:Qdrant:Port"] = "6334";
        settings["RagNet:VectorStore:Qdrant:CollectionName"] = "test-collection";
        var logger = new FakeLogger<InMemoryVectorStore>();
        using var provider = BuildProvider(settings, logger);

        _ = provider.GetRequiredService<IVectorStore>();

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void PgVectorKindDoesNotLogTheInMemoryWarning()
    {
        var settings = BaseSettings("PgVector");
        settings["RagNet:VectorStore:PgVector:ConnectionString"] =
            "Host=localhost;Database=ragnet_test;Username=test;Password=test";
        var logger = new FakeLogger<InMemoryVectorStore>();
        using var provider = BuildProvider(settings, logger);

        _ = provider.GetRequiredService<IVectorStore>();

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?> settings, FakeLogger<InMemoryVectorStore> logger)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILogger<InMemoryVectorStore>>(logger);
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
