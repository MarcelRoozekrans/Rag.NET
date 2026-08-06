using System.Diagnostics;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rag.NET.PgVector.Tests;

[Collection("Telemetry")]
public class PgVectorStoreTelemetryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .Build();

    private PgVectorStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
        _sut = new PgVectorStore(_postgres.GetConnectionString(), vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _postgres.DisposeAsync();
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
        Assert.Equal(nameof(PgVectorStore), upsertSpan.GetTagItem("vector.store"));
        Assert.Equal("rag_chunks", upsertSpan.GetTagItem("vectorstore.collection"));
        Assert.Equal(1, upsertSpan.GetTagItem("vectorstore.batch.size"));

        var searchSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.search", StringComparison.Ordinal));
        Assert.NotNull(searchSpan);
        Assert.Equal(nameof(PgVectorStore), searchSpan.GetTagItem("vector.store"));
        Assert.Equal(1, searchSpan.GetTagItem("vectorstore.result.count"));

        var deleteSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.delete", StringComparison.Ordinal));
        Assert.NotNull(deleteSpan);
        Assert.Equal(nameof(PgVectorStore), deleteSpan.GetTagItem("vector.store"));
    }
}
