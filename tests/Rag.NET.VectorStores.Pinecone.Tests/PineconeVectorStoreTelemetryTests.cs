using System.Diagnostics;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Pinecone.Tests;

/// <summary>
/// Joins the existing "PineconeLocal" collection rather than a dedicated "Telemetry" one:
/// <see cref="PineconeContainerFixture"/> binds the emulator to a fixed host port range
/// (5080-5090), so a second container instance for an isolated collection would fail to bind
/// alongside it. Isolation from concurrently running sibling tests in this collection is
/// instead achieved by filtering captured activities down to this test's own
/// <see cref="Activity.TraceId"/>, the same technique <c>RetrieveTelemetryTests</c> uses in
/// core.
/// </summary>
[Collection("PineconeLocal")]
public class PineconeVectorStoreTelemetryTests(PineconeContainerFixture fixture)
{
    [Fact]
    public async Task StoreSearchDelete_EmitVectorStoreSpansWithTags()
    {
        var indexName = $"ragnet-telemetry-{Guid.CreateVersion7():N}"[..24];
        var store = new PineconeVectorStore(new PineconeOptions
        {
            ApiKey = "pclocal",
            IndexName = indexName,
            Endpoint = fixture.Endpoint,
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

        try
        {
            await store.CreateCollectionAsync(indexName, 3, TestContext.Current.CancellationToken);

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
            Assert.Equal(nameof(PineconeVectorStore), upsertSpan.GetTagItem("vector.store"));
            Assert.Equal(indexName, upsertSpan.GetTagItem("vectorstore.collection"));
            Assert.Equal(1, upsertSpan.GetTagItem("vectorstore.batch.size"));

            var searchSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.search", StringComparison.Ordinal));
            Assert.NotNull(searchSpan);
            Assert.Equal(nameof(PineconeVectorStore), searchSpan.GetTagItem("vector.store"));

            var deleteSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.delete", StringComparison.Ordinal));
            Assert.NotNull(deleteSpan);
            Assert.Equal(nameof(PineconeVectorStore), deleteSpan.GetTagItem("vector.store"));
        }
        finally
        {
            await store.DeleteCollectionAsync(indexName, TestContext.Current.CancellationToken);
        }
    }
}
