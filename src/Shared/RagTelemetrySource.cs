using System.Diagnostics;

namespace Rag.NET.Telemetry;

/// <summary>
/// The <see cref="ActivitySource"/> every Rag.NET assembly traces spans on.
/// </summary>
/// <remarks>
/// <para>
/// This file lives in <c>src/Shared</c> and is <b>linked</b>, not referenced, into every
/// assembly that emits spans — core (<c>Rag.NET</c>) and each satellite instrumented under
/// Phase 4.4. Core's <c>RagTelemetry</c> is <c>internal</c> and cannot be depended on from
/// outside <c>Rag.NET</c> (see <c>Rag.NET.Evaluation.Shadow.ShadowTelemetry</c> for the same
/// constraint applied to the meter side), and a <c>ProjectReference</c> to core from a
/// satellite that has no other reason to depend on it — a vector store, a reranker — is
/// exactly the coupling the package decomposition exists to avoid. Linking keeps one
/// definition and adds neither.
/// </para>
/// <para>
/// <b>The mechanism this relies on:</b> an <see cref="ActivitySource"/> is matched to a
/// listener by its <em>name</em>, not by object identity — <see cref="ActivityListener.ShouldListenTo"/>
/// receives the source and compares <see cref="ActivitySource.Name"/>. So <c>N</c>
/// independently-<c>new</c>'d <see cref="ActivitySource"/> instances that all share the name
/// <c>"Rag.NET"</c> are all heard by one listener subscribed to that name, including across
/// assembly boundaries. Every assembly that links this file therefore gets <b>its own</b>
/// <c>static readonly ActivitySource</c> field, compiled separately — that is correct, proven
/// by <c>RagTelemetrySourceCrossAssemblyTests</c>, and not something to "fix" by trying to
/// route every assembly through one shared static instance, which would require the very
/// dependency this design avoids.
/// </para>
/// <para>
/// <b>Ambiguity note for anyone tempted to reference this type directly across assemblies:</b>
/// every linked copy declares the identical <c>Rag.NET.Telemetry.RagTelemetrySource</c>
/// namespace-qualified name. An assembly that references two others which both grant it
/// <c>InternalsVisibleTo</c> for this type (for example core plus a satellite) would see two
/// candidate types with the same fully-qualified name and fail to compile without an
/// <c>extern alias</c>. Nothing in the shipped packages does this — each assembly only ever
/// resolves its own linked copy — but a test that wants to reach two assemblies' instances at
/// once should go through a small per-assembly public wrapper instead of naming this internal
/// type from a third assembly. <c>Rag.NET.Testing.TelemetryProbe</c> is that wrapper for the
/// cross-assembly test.
/// </para>
/// </remarks>
internal static class RagTelemetrySource
{
    /// <summary>
    /// The name every Rag.NET <see cref="ActivitySource"/> shares. A listener or an exporter
    /// that subscribes to this one name receives spans from core and every instrumented
    /// satellite alike — see <c>docs/reference/opentelemetry.md</c> for the span-naming
    /// convention satellites follow so their spans compose with core's.
    /// </summary>
    internal const string Name = "Rag.NET";

    /// <summary>
    /// The version stamped on every Rag.NET <see cref="ActivitySource"/>, pinned in this one
    /// place so every linked copy stays in lockstep.
    /// </summary>
    internal const string Version = "1.0.0";

    /// <summary>This assembly's own instance. See the remarks on the type for why it is one of many.</summary>
    internal static readonly ActivitySource ActivitySource = new(Name, Version);
}
