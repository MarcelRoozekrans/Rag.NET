using Rag.NET.Models;

namespace Rag.NET.Evaluation.Internal;

/// <summary>What every variant did with one sample: the answers, the wall-clocks, and any failure.</summary>
/// <remarks>
/// Raw execution output. Nothing here is scored — scoring happens afterwards, over the whole run —
/// which is what keeps the runner testable without a judge.
/// </remarks>
/// <param name="Sample">The sample every variant was asked.</param>
/// <param name="Responses">Each variant's response, keyed by variant name. A variant that failed is absent.</param>
/// <param name="Elapsed">
/// Wall-clock per variant, keyed by variant name. Present only for variants that answered: timing a
/// call that threw measures how fast it failed, which is not the latency being compared.
/// </param>
/// <param name="Failure">
/// The first variant that failed on this sample, or <see langword="null"/> when all of them
/// answered. When more than one variant fails on the same sample only the first is kept; the pair
/// is excluded either way, so the second message would change nothing but the diagnostics.
/// </param>
internal sealed record VariantRun(
    EvaluationSample Sample,
    IReadOnlyDictionary<string, RagResponse> Responses,
    IReadOnlyDictionary<string, TimeSpan> Elapsed,
    VariantFailure? Failure)
{
    /// <summary>Whether this sample can take part in the paired statistics.</summary>
    /// <remarks>
    /// A run is comparable only when <b>every</b> variant answered. Comparing the sides that
    /// happened to succeed would compute the two means over different sample sets while still
    /// describing the result as paired.
    /// </remarks>
    public bool IsComparable => Failure is null;
}
