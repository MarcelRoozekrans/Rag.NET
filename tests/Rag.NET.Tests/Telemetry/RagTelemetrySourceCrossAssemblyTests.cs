using System.Collections.Concurrent;
using System.Diagnostics;
using Rag.NET.Telemetry;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.Tests.Telemetry;

/// <summary>
/// Proves the mechanism every satellite instrumented under Phase 4.4 relies on: one
/// <see cref="ActivityListener"/> subscribed to the shared name <c>"Rag.NET"</c> receives spans
/// from independently-<c>new</c>'d <see cref="ActivitySource"/> instances in separate
/// assemblies, because an <see cref="ActivitySource"/> is matched to a listener by
/// <see cref="ActivitySource.Name"/>, not by object identity.
/// </summary>
/// <remarks>
/// The two instances here are core's real <see cref="RagTelemetry.ActivitySource"/> (compiled
/// into <c>Rag.NET</c>) and <see cref="Rag.NET.Testing.TelemetryProbe"/>'s instance (compiled
/// into <c>Rag.NET.Testing</c> from the same linked <c>src/Shared/RagTelemetrySource.cs</c>
/// file every satellite links) — a genuine second assembly, not a second instance created
/// inline in this test project. See the remarks on <c>RagTelemetrySource</c> for why the test
/// goes through <see cref="TelemetryProbe"/> rather than naming the internal
/// <c>RagTelemetrySource</c> type from both assemblies directly.
/// </remarks>
[Collection("Telemetry")]
public class RagTelemetrySourceCrossAssemblyTests
{
    [Fact]
    public void Listener_OnSharedSourceName_ReceivesSpansFromBothAssemblies()
    {
        var activities = new ConcurrentBag<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, RagTelemetry.SourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var _ = listener;

        // Start a parent activity so both spans inherit our TraceId (ActivitySource.StartActivity
        // picks up Activity.Current via AsyncLocal). Filtering by TraceId deterministically
        // excludes spans from concurrently running test classes that hit the same global sources.
        using var parent = new Activity("test-parent").Start();

        using (RagTelemetry.ActivitySource.StartActivity("cross-assembly.core"))
        {
            // Started and immediately disposed: only its arrival at the listener matters.
        }
        using (TelemetryProbe.StartActivity("cross-assembly.probe"))
        {
        }

        var names = activities
            .Where(a => a.TraceId == parent.TraceId)
            .Select(a => a.OperationName)
            .ToList();

        Assert.Contains("cross-assembly.core", names, StringComparer.Ordinal);
        Assert.Contains("cross-assembly.probe", names, StringComparer.Ordinal);
    }
}
