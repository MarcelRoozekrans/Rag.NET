using System.Diagnostics;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Reranking.Onnx.Tests;

/// <summary>
/// Real-model smoke test over a real cross-encoder ONNX model. Skipped unless the
/// <c>RAGNET_ONNX_RERANK_MODEL</c> and <c>RAGNET_ONNX_RERANK_VOCAB</c> environment variables
/// point to existing files — the same gate <c>OnnxEmbeddingGeneratorSmokeTests</c> uses for the
/// embeddings package, since neither model ships in this repository.
/// </summary>
[Collection("Telemetry")]
public sealed class OnnxRerankerTelemetryTests
{
    [Fact]
    public async Task RerankAsync_EmitsRerankSpanWithTags()
    {
        var modelPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_RERANK_MODEL");
        var vocabPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_RERANK_VOCAB");
        Assert.SkipWhen(
            string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath) ||
            string.IsNullOrEmpty(vocabPath) || !File.Exists(vocabPath),
            "Set RAGNET_ONNX_RERANK_MODEL and RAGNET_ONNX_RERANK_VOCAB to existing model/vocab files to run this test.");

        // Wrapped deliberately — see the Cohere equivalent. Since the pilot moved instrumentation
        // into the generated proxy, a directly-constructed OnnxReranker emits no span at all.
        using var inner = new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = modelPath!,
            VocabPath = vocabPath!,
        });
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

        var results = await reranker.RerankAsync(
            "what is dense retrieval?",
            [
                new SearchResult
                {
                    Chunk = new TextChunk { Text = "dense retrieval embeds queries and passages", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
                    Score = 0.5,
                },
            ],
            TestContext.Current.CancellationToken);

        Assert.Single(results);

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.rerank.OnnxReranker", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Null(span.GetTagItem("reranker.type"));
        Assert.Equal(1, span.GetTagItem("reranker.candidate.count"));
        // New: this reranker never set a result count by hand. The proxy tags it from the
        // return value, so both rerankers now report it.
        Assert.Equal(1, span.GetTagItem("reranker.result.count"));
    }
}
