namespace Rag.NET.Evaluation.Shadow;

/// <summary>What one pipeline did with one shadowed request: an answer or a failure, never both.</summary>
/// <remarks>
/// One side of a <see cref="ShadowCapture"/>. The fields are exactly what the offline A/B scorer
/// consumes: the answer becomes the projected sample's predicted answer, the context texts become
/// its source chunks, the latency and spend feed the per-variant columns, and a failure replays as
/// the run failure it was. Context is stored as plain text because that is all the scorer reads —
/// document ids and chunk indices are replay placeholders, not captured data.
/// </remarks>
/// <param name="VariantName">
/// The label this side is reported under. Non-blank, and distinct from the other side's — the
/// scorer keys everything by it.
/// </param>
/// <param name="Answer">The pipeline's answer, or <see langword="null"/> when it failed.</param>
/// <param name="ContextTexts">
/// The retrieved context, one string per chunk, in retrieval order. Empty when the call failed
/// before retrieving anything.
/// </param>
/// <param name="Latency">
/// Wall-clock for the call, or <see langword="null"/> when it failed: timing a call that threw
/// measures how fast it failed, which is not the latency being compared.
/// </param>
/// <param name="Spend">
/// What the call cost, or <see langword="null"/> when nothing measured it. Absent rather than
/// zero — a zero would claim the call was free. A failed call may still carry spend; it can have
/// cost money before it threw.
/// </param>
/// <param name="Failure">
/// What went wrong, or <see langword="null"/> when <paramref name="Answer"/> is present. Exactly
/// one of the two is set.
/// </param>
public sealed record ShadowVariantCapture(
    string VariantName,
    string? Answer,
    IReadOnlyList<string> ContextTexts,
    TimeSpan? Latency = null,
    decimal? Spend = null,
    ShadowVariantFailure? Failure = null)
{
    /// <inheritdoc cref="ShadowVariantCapture" path="/param[@name='VariantName']"/>
    public string VariantName { get; init; } = RequireName(VariantName);

    /// <inheritdoc cref="ShadowVariantCapture" path="/param[@name='ContextTexts']"/>
    public IReadOnlyList<string> ContextTexts { get; init; } =
        ContextTexts ?? throw new ArgumentNullException(nameof(ContextTexts));

    /// <inheritdoc cref="ShadowVariantCapture" path="/param[@name='Failure']"/>
    public ShadowVariantFailure? Failure { get; init; } = RequireOneOutcome(Answer, Failure);

    // MA0015 requires the paramName to be a parameter of the throwing method, and these helpers
    // validate record parameters they do not declare — so the helper's own parameter is named
    // and the name the caller actually wrote goes in the message, as AbOptions does.
    private static string RequireName(string variantName)
    {
        if (string.IsNullOrWhiteSpace(variantName))
        {
            throw new ArgumentException(
                $"A captured variant needs a non-blank {nameof(VariantName)}; the offline scorer keys everything by it.",
                nameof(variantName));
        }

        return variantName;
    }

    /// <summary>Refuses a side that is both an answer and a failure, or neither.</summary>
    /// <remarks>
    /// Either would sit quietly in storage and only surface when an offline comparison replayed
    /// it: a both-side is ambiguous about what the caller was told, and a neither-side replays as
    /// nothing at all. Refused where the capture is written instead.
    /// </remarks>
    private static ShadowVariantFailure? RequireOneOutcome(string? answer, ShadowVariantFailure? failure)
    {
        if (answer is null && failure is null)
        {
            throw new ArgumentException(
                $"A captured variant is an {nameof(Answer)} or a {nameof(Failure)}; this one is neither.",
                nameof(failure));
        }

        if (answer is not null && failure is not null)
        {
            throw new ArgumentException(
                $"A captured variant is an {nameof(Answer)} or a {nameof(Failure)}, never both; this " +
                $"one has an answer and the failure '{failure.Message}'.", nameof(failure));
        }

        return failure;
    }
}
