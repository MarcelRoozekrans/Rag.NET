using Xunit;

namespace Rag.NET.Reranking.Onnx.Tests;

/// <summary>
/// Serializes execution of telemetry test classes in this assembly.
/// </summary>
/// <remarks>
/// Mirrors <c>Rag.NET.Tests.Telemetry.TelemetryCollection</c>: the "Rag.NET"-named
/// <see cref="System.Diagnostics.ActivitySource"/> is process-global, and xUnit collections are
/// resolved per assembly, so this satellite needs its own definition of the same name rather
/// than sharing core's.
/// </remarks>
[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection
{
}
