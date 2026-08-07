// Benchmarks measuring OTel ActivitySource overhead:
// - No listener attached (StartActivity returns null — expected ~0ns overhead)
// - Listener attached with AllData sampling (full allocation path)
//
// These validate the "zero overhead when no listener" guarantee.

using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Benchmarks;

[MemoryDiagnoser]
public class TelemetryOverheadBenchmarks
{
    // Mirror the source name from RagTelemetry.SourceName ("Rag.NET") without
    // taking an internal dependency on the type.
    private static readonly ActivitySource Source = new("Rag.NET", "1.0.0");

    private ActivityListener _listener = null!;

    [GlobalSetup(Target = nameof(WithListener))]
    public void SetupWithListener()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [GlobalCleanup(Target = nameof(WithListener))]
    public void CleanupWithListener() => _listener?.Dispose();

    [Benchmark(Baseline = true)]
    public void NoListener()
    {
        using var activity = Source.StartActivity("ragnet.ingest");
    }

    [Benchmark]
    public void WithListener()
    {
        using var activity = Source.StartActivity("ragnet.ingest");
    }
}

/// <summary>
/// Compares the two ways of instrumenting an <c>async Task</c> interface method: a span opened
/// inside the method (Phase 4.4's approach) against the generated proxy wrapping it (the
/// telemetry pilot's approach).
/// </summary>
/// <remarks>
/// This is the shape the existing benchmark above does not cover, and the one where the two
/// approaches could genuinely diverge: the proxy is a second <c>async</c> frame around the
/// first, so it may allocate an additional state machine per call. Phase 4.4 recorded 144 B for
/// a decorator against 72 B bare; ZeroAlloc.Telemetry's own README publishes parity at 72 B.
/// Those disagree, and neither was measured on this shape — hence this.
/// <para>
/// Measured under a listener, because that is where allocation happens at all: with none
/// attached <c>StartActivity</c> returns null and both approaches are free.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class RerankerInstrumentationBenchmarks
{
    private static readonly IReadOnlyList<SearchResult> Candidates =
    [
        new() { Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 1 },
    ];

    private ActivityListener _listener = null!;
    private IReranker _bare = null!;
    private IReranker _handWritten = null!;
    private IReranker _proxied = null!;

    [GlobalSetup]
    public void Setup()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { },
        };
        ActivitySource.AddActivityListener(_listener);

        _bare = new BareReranker();
        _handWritten = new HandWrittenSpanReranker();
        _proxied = new RerankerInstrumented(new BareReranker());
    }

    [GlobalCleanup]
    public void Cleanup() => _listener?.Dispose();

    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<RerankResult>> NoInstrumentation() =>
        _bare.RerankAsync("q", Candidates, CancellationToken.None);

    [Benchmark]
    public Task<IReadOnlyList<RerankResult>> SpanInsideTheMethod() =>
        _handWritten.RerankAsync("q", Candidates, CancellationToken.None);

    [Benchmark]
    public Task<IReadOnlyList<RerankResult>> GeneratedProxy() =>
        _proxied.RerankAsync("q", Candidates, CancellationToken.None);

    private sealed class BareReranker : IReranker
    {
        public Task<IReadOnlyList<RerankResult>> RerankAsync(
            string query, IReadOnlyList<SearchResult> results, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>([]);
    }

    /// <summary>What every reranker looked like before the pilot.</summary>
    private sealed class HandWrittenSpanReranker : IReranker
    {
        private static readonly ActivitySource Source = new("Rag.NET", "1.0.0");

        public Task<IReadOnlyList<RerankResult>> RerankAsync(
            string query, IReadOnlyList<SearchResult> results, CancellationToken cancellationToken = default)
        {
            using var activity = Source.StartActivity("ragnet.rerank");
            activity?.SetTag("reranker.type", nameof(HandWrittenSpanReranker));
            activity?.SetTag("reranker.candidate.count", results.Count);
            return Task.FromResult<IReadOnlyList<RerankResult>>([]);
        }
    }
}
