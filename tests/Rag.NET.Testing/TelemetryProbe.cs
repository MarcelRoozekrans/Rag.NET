using System.Diagnostics;
using Rag.NET.Telemetry;

namespace Rag.NET.Testing;

/// <summary>
/// Starts an activity on this assembly's own copy of the shared "Rag.NET"-named
/// <see cref="ActivitySource"/> — the same <c>src/Shared/RagTelemetrySource.cs</c> file core
/// links, compiled here into a second, independently-<c>new</c>'d instance.
/// </summary>
/// <remarks>
/// Exists only so <c>RagTelemetrySourceCrossAssemblyTests</c> can prove that one
/// <see cref="ActivityListener"/> subscribed to the shared name receives spans from two separate
/// assemblies — the mechanism every satellite instrumented under Phase 4.4 relies on — without
/// the test needing to name the internal <c>RagTelemetrySource</c> type directly from a third
/// assembly. Naming it directly would be ambiguous once that third assembly also sees core's
/// own copy (both are <c>Rag.NET.Telemetry.RagTelemetrySource</c>); see the remarks on
/// <c>RagTelemetrySource</c> itself. Going through this small public wrapper instead sidesteps
/// that entirely.
/// </remarks>
public static class TelemetryProbe
{
    /// <summary>Starts an activity on this assembly's instance of the shared "Rag.NET" source.</summary>
    public static Activity? StartActivity(string name) => RagTelemetrySource.ActivitySource.StartActivity(name);
}
