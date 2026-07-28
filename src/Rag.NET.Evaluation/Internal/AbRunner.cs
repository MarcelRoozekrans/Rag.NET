using Rag.NET.Models;
using System.Diagnostics;

namespace Rag.NET.Evaluation.Internal;

/// <summary>
/// Executes an evaluation dataset through both variants, alternating which one leads, and times
/// each call. Performs no scoring.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the order alternates.</b> Whichever variant runs second benefits from provider-side prompt
/// caching and a warm vector store. A fixed order therefore hands one variant a systematic
/// advantage on every sample and reports it as a result. The lead rotates by sample index: A,B then
/// B,A then A,B. This matters least for quality scores and most for latency, and latency is half the
/// reason to run this at all.
/// </para>
/// <para>
/// <b>Exactly two variants.</b> Not "at least two". Everything downstream is strictly pairwise —
/// <c>AbStatistics.PairedDeltas</c> takes two score arrays, <c>AbTally</c> has a <c>BWins</c> and an
/// <c>AWins</c> and nowhere to put a third — and the Phase 3.3 design rules N-way out until someone
/// works through the multiple-comparisons correction it needs. A third variant could only be
/// executed and then dropped on the floor, so it is refused here, where the caller can still see why.
/// </para>
/// <para>
/// <b>Why not concurrently.</b> Running both variants at once roughly halves wall-clock, but the
/// two then contend for the same provider and the same connection pool, so the latency numbers
/// measure contention as much as they measure the variants.
/// </para>
/// <para>
/// Static because it holds nothing between calls: the run is fully described by its arguments, and
/// a stateless instance would only be ceremony. Same shape as <c>AbStatistics</c> and
/// <c>ReservoirSampler</c>.
/// </para>
/// </remarks>
internal static class AbRunner
{
    /// <summary>Runs every sample through both variants.</summary>
    /// <param name="variants">The two variants to compare, with distinct non-blank names.</param>
    /// <param name="samples">The dataset. Each variant is asked <c>Sample.Question</c>.</param>
    /// <param name="cancellationToken">Token to cancel the run.</param>
    /// <returns>One <see cref="VariantRun"/> per sample, in input order.</returns>
    /// <exception cref="ArgumentException">
    /// Not exactly two variants, or a blank or duplicated name.
    /// </exception>
    /// <remarks>
    /// A variant that throws is recorded rather than propagated: one bad sample must not lose the
    /// other ninety-nine. Cancellation is the exception to that — it is the caller asking to stop,
    /// not a variant misbehaving, so it propagates.
    /// </remarks>
    public static async Task<IReadOnlyList<VariantRun>> RunAsync(
        IReadOnlyList<AbVariant> variants,
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default)
    {
        ValidateVariants(variants);
        ArgumentNullException.ThrowIfNull(samples);

        var runs = new List<VariantRun>(samples.Count);

        // Indexed rather than foreach: the sample index is what rotates the lead, and there is no
        // enumerator to allocate over the interface.
        for (var s = 0; s < samples.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            runs.Add(await RunSampleAsync(variants, samples[s], s, cancellationToken).ConfigureAwait(false));
        }

        return runs;
    }

    private static async Task<VariantRun> RunSampleAsync(
        IReadOnlyList<AbVariant> variants,
        EvaluationSample sample,
        int sampleIndex,
        CancellationToken cancellationToken)
    {
        var responses = new Dictionary<string, RagResponse>(variants.Count, StringComparer.Ordinal);
        var elapsed = new Dictionary<string, TimeSpan>(variants.Count, StringComparer.Ordinal);
        VariantFailure? failure = null;

        for (var v = 0; v < variants.Count; v++)
        {
            // The rotation: sample 0 leads with variants[0], sample 1 with variants[1], and so on.
            var variant = variants[(sampleIndex + v) % variants.Count];
            var started = Stopwatch.GetTimestamp();

            try
            {
                var response = await variant.Pipeline
                    .AskAsync(sample.Question, variant.Options, cancellationToken)
                    .ConfigureAwait(false);

                if (response is null)
                {
                    failure ??= new VariantFailure(variant.Name, "The pipeline returned no response.");
                    continue;
                }

                elapsed[variant.Name] = Stopwatch.GetElapsedTime(started);
                responses[variant.Name] = response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller asked to stop. Recording this as a variant failure would report a
                // cancelled run as a comparison in which every sample went wrong — and would keep
                // asking the other variant questions after the stop was requested.
                throw;
            }
            catch (Exception ex)
            {
                // Type as well as message: "Object reference not set to an instance of an object"
                // on its own tells a reader nothing about which layer gave way. The exception is
                // kept whole beside it, so the report gets one line and whoever fixes the variant
                // gets the stack trace.
                failure ??= new VariantFailure(variant.Name, $"{ex.GetType().Name}: {ex.Message}")
                {
                    Exception = ex,
                };
            }
        }

        return new VariantRun(sample, responses, elapsed, failure);
    }

    private static void ValidateVariants(IReadOnlyList<AbVariant> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);

        if (variants.Count != 2)
        {
            throw new ArgumentException(
                $"An A/B comparison is exactly two variants; got {variants.Count}. N-way comparison " +
                "needs a multiple-comparisons correction and is out of scope for this phase — see " +
                "the Phase 3.3 design, 'Out of scope'. Compare the pairs you care about separately.",
                nameof(variants));
        }

        var names = new HashSet<string>(variants.Count, StringComparer.Ordinal);
        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants[i];

            if (string.IsNullOrWhiteSpace(variant?.Name))
            {
                throw new ArgumentException(
                    $"Every variant needs a name; the one at index {i} has none. Names key the report.",
                    nameof(variants));
            }

            if (!names.Add(variant.Name))
            {
                throw new ArgumentException(
                    $"Variant names must be unique within a comparison; '{variant.Name}' appears more than once.",
                    nameof(variants));
            }
        }
    }
}
