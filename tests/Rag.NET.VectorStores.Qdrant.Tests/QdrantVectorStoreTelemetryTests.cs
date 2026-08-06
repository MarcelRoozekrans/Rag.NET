using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Qdrant.Tests;

[Collection("Telemetry")]
public class QdrantVectorStoreTelemetryTests : IAsyncLifetime
{
    private readonly IContainer _qdrant = new ContainerBuilder("qdrant/qdrant:latest")
        .WithPortBinding(6334, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Actix runtime found"))
        .Build();

    private QdrantVectorStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        await _qdrant.StartAsync(TestContext.Current.CancellationToken);
        var port = _qdrant.GetMappedPublicPort(6334);
        _sut = new QdrantVectorStore("localhost", port, "telemetry-collection", vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _qdrant.DisposeAsync();
    }

    [Fact]
    public async Task StoreSearchDelete_EmitVectorStoreSpansWithTags()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        // Parent activity so spans from this test can be told apart from any concurrently
        // running test class hitting the same process-global "Rag.NET" ActivitySource.
        using var parent = new Activity("test-parent").Start();

        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "telemetry probe", DocumentId = new DocumentId("doc-telemetry"), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);
        await _sut.DeleteByDocumentIdAsync("doc-telemetry", TestContext.Current.CancellationToken);

        var ours = activities.Where(a => a.TraceId == parent.TraceId).ToList();

        var upsertSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.upsert", StringComparison.Ordinal));
        Assert.NotNull(upsertSpan);
        Assert.Equal(nameof(QdrantVectorStore), upsertSpan.GetTagItem("vector.store"));
        Assert.Equal("telemetry-collection", upsertSpan.GetTagItem("vectorstore.collection"));
        Assert.Equal(1, upsertSpan.GetTagItem("vectorstore.batch.size"));

        var searchSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.search", StringComparison.Ordinal));
        Assert.NotNull(searchSpan);
        Assert.Equal(nameof(QdrantVectorStore), searchSpan.GetTagItem("vector.store"));
        Assert.Equal(1, searchSpan.GetTagItem("vectorstore.result.count"));

        var deleteSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.delete", StringComparison.Ordinal));
        Assert.NotNull(deleteSpan);
        Assert.Equal(nameof(QdrantVectorStore), deleteSpan.GetTagItem("vector.store"));
    }
}
