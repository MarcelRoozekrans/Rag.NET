using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Weaviate.Tests;

[Collection("Telemetry")]
public class WeaviateVectorStoreTelemetryTests : IAsyncLifetime
{
    private readonly IContainer _weaviate = new ContainerBuilder("cr.weaviate.io/semitechnologies/weaviate:latest")
        .WithPortBinding(8080, true)
        .WithEnvironment("AUTHENTICATION_ANONYMOUS_ACCESS_ENABLED", "true")
        .WithEnvironment("PERSISTENCE_DATA_PATH", "/var/lib/weaviate")
        .WithEnvironment("DEFAULT_VECTORIZER_MODULE", "none")
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/v1/.well-known/ready")))
        .Build();

    private Uri _endpoint = null!;

    public async ValueTask InitializeAsync()
    {
        await _weaviate.StartAsync(TestContext.Current.CancellationToken);
        _endpoint = new Uri($"http://{_weaviate.Hostname}:{_weaviate.GetMappedPublicPort(8080)}");
    }

    public async ValueTask DisposeAsync() => await _weaviate.DisposeAsync();

    [Fact]
    public async Task StoreSearchDelete_EmitVectorStoreSpansWithTags()
    {
        var className = $"Telemetry{Guid.CreateVersion7():N}";
        using var store = new WeaviateVectorStore(new WeaviateOptions
        {
            Endpoint = _endpoint,
            ClassName = className,
            VectorDimensions = 3,
        });

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("test-parent").Start();

        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "telemetry probe", DocumentId = new DocumentId("doc-telemetry"), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        await store.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await store.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);
        await store.DeleteByDocumentIdAsync("doc-telemetry", TestContext.Current.CancellationToken);

        var ours = activities.Where(a => a.TraceId == parent.TraceId).ToList();

        var upsertSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.upsert", StringComparison.Ordinal));
        Assert.NotNull(upsertSpan);
        Assert.Equal(nameof(WeaviateVectorStore), upsertSpan.GetTagItem("vector.store"));
        Assert.Equal(className, upsertSpan.GetTagItem("vectorstore.collection"));
        Assert.Equal(1, upsertSpan.GetTagItem("vectorstore.batch.size"));

        var searchSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.search", StringComparison.Ordinal));
        Assert.NotNull(searchSpan);
        Assert.Equal(nameof(WeaviateVectorStore), searchSpan.GetTagItem("vector.store"));
        Assert.Equal(1, searchSpan.GetTagItem("vectorstore.result.count"));

        var deleteSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.delete", StringComparison.Ordinal));
        Assert.NotNull(deleteSpan);
        Assert.Equal(nameof(WeaviateVectorStore), deleteSpan.GetTagItem("vector.store"));
    }
}
