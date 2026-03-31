using System.Text;
using BenchmarkDotNet.Attributes;
using Rag.NET.Models;
using Rag.NET.Reranking.Cohere;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the end-to-end cost of <see cref="CohereReranker.RerankAsync"/> at various
/// document counts. Network I/O is replaced by a local WireMock stub so that timings reflect
/// HTTP serialisation, SDK overhead, and result mapping rather than real API latency.
/// </summary>
[MemoryDiagnoser]
public class CohereRerankerBenchmarks
{
    [Params(10, 50, 100, 500, 1000)]
    public int DocumentCount { get; set; }

    private WireMockServer _server = null!;
    private CohereReranker _reranker = null!;
    private IReadOnlyList<SearchResult> _docs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _server = WireMockServer.Start();

        // Build the fixed stub response containing the first Min(DocumentCount, 5) results.
        // TopN defaults to 5 in CohereRerankerOptions so the reranker will only return 5 items
        // regardless of how many documents are sent; matching that here avoids SDK validation errors.
        var topN = Math.Min(DocumentCount, 5);
        var stubBody = BuildStubBody(topN);

        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubBody));

        _reranker = new CohereReranker(new CohereRerankerOptions
        {
            ApiKey = "bench-key",
            Endpoint = _server.Url,
            TopN = topN,
            MaxDocumentsPerBatch = 1000,
        });

        _docs = BuildDocs(DocumentCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reranker.Dispose();
        _server.Stop();
    }

    [Benchmark]
    public async Task<int> RerankAsync_Stub()
    {
        var results = await _reranker.RerankAsync("benchmark query", _docs).ConfigureAwait(false);
        return results.Count;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string BuildStubBody(int topN)
    {
        // {"id":"bench","results":[{"index":0,"relevance_score":0.9},{"index":1,"relevance_score":0.8},...]}
        var sb = new StringBuilder(64 + topN * 48);
        sb.Append("{\"id\":\"bench\",\"results\":[");

        for (var i = 0; i < topN; i++)
        {
            if (i > 0)
                sb.Append(',');

            // Scores decrease by 0.1 per entry; keep all values positive.
            var score = Math.Max(0.9 - (i * 0.1), 0.1);
            sb.Append("{\"index\":");
            sb.Append(i);
            sb.Append(",\"relevance_score\":");
            sb.Append(score.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static IReadOnlyList<SearchResult> BuildDocs(int count)
    {
        var docs = new SearchResult[count];
        for (var i = 0; i < count; i++)
        {
            docs[i] = new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = $"Benchmark document {i}: short text for reranking.",
                    DocumentId = new DocumentId("bench-doc"),
                    ChunkIndex = i,
                },
                Score = 1.0 - (i * 0.001),
            };
        }

        return docs;
    }
}
