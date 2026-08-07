using System.Diagnostics;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Rag.NET.Reranking.Cohere.Tests;

[Collection("Telemetry")]
public sealed class CohereRerankerTelemetryTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private static SearchResult MakeSearchResult(string text, int chunkIndex = 0) =>
        new()
        {
            Chunk = new TextChunk
            {
                Text = text,
                DocumentId = new DocumentId("doc-1"),
                ChunkIndex = chunkIndex,
            },
            Score = 0.5,
        };

    [Fact]
    public async Task RerankAsync_EmitsRerankSpanWithTags()
    {
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":\"x\",\"results\":[{\"index\":0,\"relevance_score\":0.95}]}"));

        var options = new CohereRerankerOptions
        {
            ApiKey = "test-key",
            Endpoint = _server.Url,
            MaxDocumentsPerBatch = 1000,
            TopN = 100,
        };
        // Wrapped deliberately. Since the pilot moved instrumentation into the generated proxy,
        // a directly-constructed CohereReranker emits no span at all — telemetry is now a
        // composition concern that UseReranking() applies, not a property of the type.
        using var inner = new CohereReranker(options);
        IReranker reranker = new RerankerInstrumented(inner);

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("test-parent").Start();

        var doc = MakeSearchResult("hello world");
        var results = await reranker.RerankAsync("query", [doc], TestContext.Current.CancellationToken);

        Assert.Single(results);

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.rerank.CohereReranker", StringComparison.Ordinal));
        Assert.NotNull(span);
        // The backend moved from a reranker.type tag into the span name, so the tag is gone.
        // Asserted rather than merely deleted: a silently-absent tag is how dashboards break.
        Assert.Null(span.GetTagItem("reranker.type"));
        Assert.Equal(1, span.GetTagItem("reranker.candidate.count"));
        Assert.Equal(1, span.GetTagItem("reranker.result.count"));
    }
}
