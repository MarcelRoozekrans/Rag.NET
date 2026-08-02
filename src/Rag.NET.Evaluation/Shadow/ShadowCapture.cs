namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// One shadowed production request: the question, what both pipelines did with it, and when.
/// </summary>
/// <remarks>
/// <para>
/// Shadow mode captures and does not score. The pair holds enough for the offline A/B scorer to
/// run later and no more: the question, both answers, both retrieved context sets, both spends,
/// both latencies, the variant labels, a timestamp — and failures, because a secondary that threw
/// is a result, and dropping it would bias the comparison toward whatever the secondary handles
/// well.
/// </para>
/// <para>
/// <b>No reference answer, deliberately.</b> Production traffic has none, which is why the two
/// reference-needing metrics cannot run inline. The stored pair is where one can be added later —
/// at which point all four metrics become available offline.
/// </para>
/// <para>
/// <b>The question and context are verbatim user and document text.</b> Whoever persists captures
/// is persisting production data; see <see cref="IShadowCaptureStore"/>.
/// </para>
/// </remarks>
/// <param name="Question">The production question, exactly as the caller asked it.</param>
/// <param name="Primary">The pipeline whose answer the caller was served.</param>
/// <param name="Secondary">The shadowed pipeline, run out-of-band over the same question.</param>
/// <param name="CapturedAt">When the pair was captured.</param>
public sealed record ShadowCapture(
    string Question,
    ShadowVariantCapture Primary,
    ShadowVariantCapture Secondary,
    DateTimeOffset CapturedAt)
{
    /// <inheritdoc cref="ShadowCapture" path="/param[@name='Question']"/>
    public string Question { get; init; } =
        Question ?? throw new ArgumentNullException(nameof(Question));

    /// <inheritdoc cref="ShadowCapture" path="/param[@name='Primary']"/>
    public ShadowVariantCapture Primary { get; init; } =
        Primary ?? throw new ArgumentNullException(nameof(Primary));

    /// <inheritdoc cref="ShadowCapture" path="/param[@name='Secondary']"/>
    /// <remarks>
    /// The null check sits in the initializer rather than the helper because MA0015 requires a
    /// paramName to belong to the throwing method, and only the initializer is in the primary
    /// constructor's scope. <c>Primary</c> is safe to dereference in the helper: initializers run
    /// in declaration order, so a null primary has already thrown above.
    /// </remarks>
    public ShadowVariantCapture Secondary { get; init; } =
        RequireDistinct(Primary, Secondary ?? throw new ArgumentNullException(nameof(Secondary)));

    /// <summary>Refuses a pair whose two sides share a variant name.</summary>
    /// <remarks>
    /// The offline scorer refuses duplicated variant names at scoring time, so a capture that
    /// names both sides the same is unscoreable from the moment it is written — refused here,
    /// where it is written, rather than when someone finally replays it.
    /// </remarks>
    private static ShadowVariantCapture RequireDistinct(
        ShadowVariantCapture primary,
        ShadowVariantCapture secondary)
    {
        if (string.Equals(primary.VariantName, secondary.VariantName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Both sides of a captured pair are labelled '{primary.VariantName}'. The offline " +
                "scorer keys everything by variant name and refuses duplicates, so this capture " +
                "could never be scored.", nameof(secondary));
        }

        return secondary;
    }
}
