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
/// inside the method, and a proxy wrapping the call from outside.
/// </summary>
/// <remarks>
/// This is the shape the benchmark above does not cover, and the one where the two approaches
/// genuinely diverge: a proxy is a second <c>async</c> frame around the first, so it allocates an
/// additional state machine per call.
/// <para>
/// It exists because two published figures disagreed and neither described this shape. The
/// 2026-08-05 observability design recorded 144 B for a decorator against 72 B bare;
/// ZeroAlloc.Telemetry's README publishes parity at 72 B. Both measured with no listener
/// attached — where <c>StartActivity</c> returns null and nothing allocates at all — so neither
/// was answering this question. Measured here under a listener with tags set: 72 B bare, 632 B
/// span-in-method, 728 B proxied.
/// </para>
/// <para>
/// <see cref="ProxyShapedReranker"/> is hand-written rather than source-generated on purpose.
/// The cost being measured belongs to the <i>shape</i> — an async wrapper around an async call —
/// not to any one library, so this stays valid whether or not a generator is ever adopted. See
/// <c>docs/plans/2026-08-07-telemetry-conversion-assessment-design.md</c> §8.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class RerankerInstrumentationBenchmarks
{
    private static readonly IReadOnlyList<SearchResult> Candidates =
    [
        new() { Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 1 },
    ];

    /// <summary>
    /// Whether a listener is attached. With none, <c>StartActivity</c> returns null and the
    /// Activity itself costs nothing — but the proxy's extra async frame is allocated either
    /// way, so the two columns answer different questions and both are worth having.
    /// </summary>
    [Params(true, false)]
    public bool Listening { get; set; }

    private ActivityListener? _listener;
    private IReranker _bare = null!;
    private IReranker _handWritten = null!;
    private IReranker _proxied = null!;

    [GlobalSetup]
    public void Setup()
    {
        if (Listening)
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

        _bare = new BareReranker();
        _handWritten = new HandWrittenSpanReranker();
        _proxied = new ProxyShapedReranker(new BareReranker());
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
    public Task<IReadOnlyList<RerankResult>> ProxyAroundTheMethod() =>
        _proxied.RerankAsync("q", Candidates, CancellationToken.None);

    private sealed class BareReranker : IReranker
    {
        public Task<IReadOnlyList<RerankResult>> RerankAsync(
            string query, IReadOnlyList<SearchResult> results, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>([]);
    }

    /// <summary>How every reranker is instrumented today: the span is opened in the method.</summary>
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

    /// <summary>
    /// The alternative: instrumentation wrapped around the call rather than inside it.
    /// </summary>
    /// <remarks>
    /// Deliberately mirrors what a source generator emits for this interface — span name composed
    /// once in the constructor, tags read from the argument and the awaited result, error status
    /// on throw — so the measurement reflects the shape rather than any particular generator.
    /// The extra allocation against <see cref="HandWrittenSpanReranker"/> is the second async
    /// state machine this <c>await</c> introduces.
    /// </remarks>
    private sealed class ProxyShapedReranker : IReranker
    {
        private static readonly ActivitySource Source = new("Rag.NET", "1.0.0");

        private readonly IReranker _inner;
        private readonly string _spanName;

        public ProxyShapedReranker(IReranker inner)
        {
            _inner = inner;
            _spanName = "ragnet.rerank." + inner.GetType().Name;
        }

        public async Task<IReadOnlyList<RerankResult>> RerankAsync(
            string query, IReadOnlyList<SearchResult> results, CancellationToken cancellationToken = default)
        {
            using var activity = Source.StartActivity(_spanName);
            activity?.SetTag("reranker.candidate.count", results?.Count);
            try
            {
                var result = await _inner.RerankAsync(query, results!, cancellationToken).ConfigureAwait(false);
                activity?.SetTag("reranker.result.count", result?.Count);
                return result!;
            }
            catch (Exception ex)
            {
                // AddException as well as SetStatus: the generated proxy suppresses EPC12 with a
                // pragma it is allowed because its file is <auto-generated />, which this is not.
                // Recording the whole exception satisfies the analyzer honestly. The branch never
                // runs in these benchmarks, so it does not affect the numbers either way.
                activity?.AddException(ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }
    }
}
