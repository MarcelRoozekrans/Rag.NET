namespace Rag.NET.Diagnostics;

/// <summary>One pipeline stage, as it ran during a traced query.</summary>
/// <remarks>
/// These are the spans <c>Rag.NET</c> already emits — <c>ragnet.retrieve</c>, <c>ragnet.ask</c> and
/// the rest — recorded rather than re-measured. A trace holds only the stages that actually ran, so
/// a missing stage means the pipeline did not reach it, not that timing was unavailable.
/// </remarks>
public sealed record TraceStage
{
    /// <summary>The span name, for example <c>ragnet.retrieve</c>.</summary>
    public required string Name { get; init; }

    /// <summary>When the stage started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>How long the stage took.</summary>
    public required TimeSpan Duration { get; init; }
}
