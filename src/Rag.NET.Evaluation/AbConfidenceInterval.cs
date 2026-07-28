using System.Runtime.InteropServices;

namespace Rag.NET.Evaluation;

/// <summary>A two-sided confidence interval on a mean delta.</summary>
/// <remarks>
/// <para>
/// This is the only part of an A/B report that separates a result from noise. An A/B run always
/// produces a higher number on one side; an interval that spans zero says the run did not establish
/// which side that is. A mean delta of <c>+0.07</c> with <c>[+0.02, +0.12]</c> is a finding — the
/// same <c>+0.07</c> with <c>[-0.04, +0.18]</c> is not.
/// </para>
/// <para>
/// Produced by a seeded percentile bootstrap, so the same deltas and the same seed give the same
/// interval. An unreproducible interval is not evidence.
/// </para>
/// </remarks>
/// <param name="Lower">The lower bound.</param>
/// <param name="Upper">The upper bound. Equal to <paramref name="Lower"/> when a single pair leaves the interval degenerate.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct AbConfidenceInterval(double Lower, double Upper);
