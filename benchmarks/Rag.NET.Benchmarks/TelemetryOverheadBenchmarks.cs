// Benchmarks measuring OTel ActivitySource overhead:
// - No listener attached (StartActivity returns null — expected ~0ns overhead)
// - Listener attached with AllData sampling (full allocation path)
//
// These validate the "zero overhead when no listener" guarantee.

using System.Diagnostics;
using BenchmarkDotNet.Attributes;

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
