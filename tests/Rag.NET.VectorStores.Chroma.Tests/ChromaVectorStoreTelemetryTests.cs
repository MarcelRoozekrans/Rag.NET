using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chroma.Tests;

[Collection("Telemetry")]
public class ChromaVectorStoreTelemetryTests : IAsyncLifetime
{
    private readonly IContainer _chroma = new ContainerBuilder("chromadb/chroma:latest")
        .WithPortBinding(8000, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(8000).ForPath("/api/v2/heartbeat")))
        .Build();

    private Uri _endpoint = null!;

    public async ValueTask InitializeAsync()
    {
        await _chroma.StartAsync(TestContext.Current.CancellationToken);
        _endpoint = new Uri($"http://{_chroma.Hostname}:{_chroma.GetMappedPublicPort(8000)}");
    }

    public async ValueTask DisposeAsync() => await _chroma.DisposeAsync();

    [Fact]
    public async Task StoreSearchDelete_EmitVectorStoreSpansWithTags()
    {
        var collectionName = $"telemetry-{Guid.CreateVersion7():N}";
        using var store = new ChromaVectorStore(new ChromaOptions
        {
            Endpoint = _endpoint,
            CollectionName = collectionName,
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
        Assert.Equal(nameof(ChromaVectorStore), upsertSpan.GetTagItem("vector.store"));
        Assert.Equal(collectionName, upsertSpan.GetTagItem("vectorstore.collection"));
        Assert.Equal(1, upsertSpan.GetTagItem("vectorstore.batch.size"));

        var searchSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.search", StringComparison.Ordinal));
        Assert.NotNull(searchSpan);
        Assert.Equal(nameof(ChromaVectorStore), searchSpan.GetTagItem("vector.store"));
        Assert.Equal(1, searchSpan.GetTagItem("vectorstore.result.count"));

        var deleteSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.delete", StringComparison.Ordinal));
        Assert.NotNull(deleteSpan);
        Assert.Equal(nameof(ChromaVectorStore), deleteSpan.GetTagItem("vector.store"));
    }
}
