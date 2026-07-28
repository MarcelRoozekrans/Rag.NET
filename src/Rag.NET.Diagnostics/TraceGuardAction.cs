namespace Rag.NET.Diagnostics;

/// <summary>What one guard or sanitiser did to the data passing through it.</summary>
/// <remarks>
/// <para>
/// This is the answer to <i>"why is that chunk missing from the answer"</i>. Guards and sanitisers
/// change what the pipeline saw and nothing else records that they fired, so an action is written
/// even when the component changed nothing: <i>"the guard ran and did nothing"</i> and <i>"the guard
/// never ran"</i> are different answers, and only the first produces a <see cref="TraceGuardAction"/>.
/// </para>
/// <para>
/// <see cref="InputText"/> and <see cref="OutputText"/> are populated only under content capture, and
/// <b>which flag governs them depends on which component produced the action</b>. One record shape is
/// written by three decorators over three interfaces, and what they hold is not the same kind of
/// thing:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// a query sanitiser's text <b>is the user's raw question</b>, governed by
/// <see cref="RagTraceOptions.CaptureQueryText"/>;
/// </description>
/// </item>
/// <item>
/// <description>
/// a retrieval guard's and a chunk sanitiser's text is retrieved document text, governed by
/// <see cref="RagTraceOptions.CaptureChunkText"/>.
/// </description>
/// </item>
/// </list>
/// <para>
/// Reading <see cref="RagTraceOptions.CaptureChunkText"/> alone as the gate for every action — which
/// is what this remark used to say — would mean enabling chunk text silently began retaining user
/// questions with <see cref="RagTraceOptions.CaptureQueryText"/> still off. The producer declares its
/// kind through <see cref="TraceContentKind"/> and the collector applies the matching flag.
/// </para>
/// </remarks>
public sealed record TraceGuardAction
{
    /// <summary>The decorated implementation's type name, for example <c>RbacRetrievalGuard</c>.</summary>
    public required string Component { get; init; }

    /// <summary>How many results, or characters of text, went in.</summary>
    public required int InputCount { get; init; }

    /// <summary>How many came out. Below <see cref="InputCount"/> means the component removed something.</summary>
    public required int OutputCount { get; init; }

    /// <summary>
    /// Whether the component altered its input at all. Distinct from comparing the counts: a
    /// sanitiser rewrites text in place, so it can change everything while both counts stay equal.
    /// </summary>
    public required bool Changed { get; init; }

    /// <summary>
    /// What the component was given. <see langword="null"/> unless the <c>Capture*</c> flag matching
    /// this component's own content is on — see the type remarks for which flag that is.
    /// </summary>
    public string? InputText { get; init; }

    /// <summary>
    /// What the component returned. <see langword="null"/> unless the <c>Capture*</c> flag matching
    /// this component's own content is on — see the type remarks for which flag that is.
    /// </summary>
    public string? OutputText { get; init; }
}
